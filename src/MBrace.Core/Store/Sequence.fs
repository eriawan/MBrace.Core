﻿namespace MBrace

open System
open System.Collections
open System.Collections.Generic
open System.Runtime.Serialization
open System.Text
open System.IO

open MBrace.Store
open MBrace.Continuation

#nowarn "444"

/// <summary>
///     Ordered, immutable collection of values persisted in a single FileStore entity.
/// </summary>
[<DataContract; StructuredFormatDisplay("{StructuredFormatDisplay}")>]
type CloudSequence<'T> =

    // https://visualfsharp.codeplex.com/workitem/199
    [<DataMember(Name = "UUID")>]
    val mutable private uuid : string
    [<DataMember(Name = "Path")>]
    val mutable private path : string
    [<DataMember(Name = "Count")>]
    val mutable private count : int64 option
    [<DataMember(Name = "Deserializer")>]
    val mutable private deserializer : (Stream -> seq<'T>) option
    [<DataMember(Name = "CacheByDefault")>]
    val mutable private cacheByDefault : bool

    internal new (path, count, deserializer, ?cacheByDefault) = 
        let uuid = Guid.NewGuid().ToString()
        let cacheByDefault = defaultArg cacheByDefault false
        { uuid = uuid ; path = path ; count = count ; deserializer = deserializer ; cacheByDefault = cacheByDefault }

    member private c.GetSequenceFromStore () = local {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration> ()
        let! stream = ofAsync <| config.FileStore.BeginRead(c.path)
        match c.deserializer with
        | None -> return config.Serializer.SeqDeserialize<'T>(stream, leaveOpen = false)
        | Some ds -> return ds stream
    }

    interface ICloudCacheable<'T []> with
        member c.UUID = c.uuid
        member c.GetSourceValue () = local {
            let! seq = c.GetSequenceFromStore()
            return Seq.toArray seq
        }

    /// Returns an enumerable that lazily fetches elements of the cloud sequence from store.
    member c.ToEnumerable () = local {
        if c.CacheByDefault then
            let! array = CloudCache.GetCachedValue c
            return array :> seq<'T>
        else
            let! cachedValue = CloudCache.TryGetCachedValue c
            match cachedValue with
            | None -> return! c.GetSequenceFromStore()
            | Some cv -> return cv :> seq<'T>
    }

    /// Fetches all elements of the cloud sequence and returns them as a local array.
    member c.ToArray () : Local<'T []> = local { return! CloudCache.GetCachedValue(c, cacheIfNotExists = c.CacheByDefault) }

    /// Caches all elements to local execution context. Returns true iff succesful.
    member c.ForceCache () = local { return! CloudCache.PopulateLocalCache c }

    /// Indicates if array is cached in local execution context
    member c.IsCachedLocally = local { return! CloudCache.IsCachedLocally c }

    /// Path to Cloud sequence in store
    member c.Path = c.path

    /// Enables or disables implicit, on-demand caching of values when first dereferenced.
    member c.CacheByDefault
        with get () = c.cacheByDefault
        and set s = c.cacheByDefault <- s

    /// Cloud sequence element count
    member c.Count = local {
        match c.count with
        | Some l -> return l
        | None ->
            // this is a potentially costly operation
            let! seq = c.ToEnumerable()
            let l = int64 <| Seq.length seq
            c.count <- Some l
            return l
    }

    /// Underlying sequence size in bytes
    member c.Size = local {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration> ()
        return! ofAsync <| config.FileStore.GetFileSize c.path
    }

    interface ICloudStorageEntity with
        member c.Type = sprintf "CloudSequence:%O" typeof<'T>
        member c.Id = c.path

    interface ICloudDisposable with
        member c.Dispose () = local {
            let! config = Cloud.GetResource<CloudFileStoreConfiguration> ()
            return! ofAsync <| config.FileStore.DeleteFile c.path
        }

    interface ICloudCollection<'T> with
        member c.Count = c.Count
        member c.ToEnumerable() = c.ToEnumerable()

    override c.ToString() = sprintf "CloudSequence[%O] at %s" typeof<'T> c.path
    member private c.StructuredFormatDisplay = c.ToString()

[<DataContract>]
type private TextLineSequence(path : string, ?encoding : Encoding, ?cacheByDefault) =
    inherit CloudSequence<string>(path, None, Some(fun stream -> new LineEnumerable(stream, ?encoding = encoding) :> _), ?cacheByDefault = cacheByDefault)

    interface IPartitionableCollection<string> with
        member __.GetPartitions(partitionCount : int) = local {
            let! size = CloudFile.GetSize path
            let getDeserializer s e stream = new LineEnumerable(stream, s, e, ?encoding = encoding) :> seq<string>
            return
                Array.splitByPartitionCountRange partitionCount 0L size
                |> Array.map (fun (s,e) -> new CloudSequence<string>(path, None, Some(getDeserializer s e), ?cacheByDefault = cacheByDefault) :> _)
        }

type CloudSequence =

    /// <summary>
    ///     Creates a new Cloud sequence with given values in the underlying store.
    ///     Cloud sequences are cached locally for performance.
    /// </summary>
    /// <param name="values">Input sequence.</param>
    /// <param name="directory">FileStore directory used for Cloud sequence. Defaults to execution context.</param>
    /// <param name="serializer">Serializer used in sequence serialization. Defaults to execution context.</param>
    /// <param name="cacheByDefault">Enables implicit, on-demand caching of instance value. Defaults to false.</param>
    static member New(values : seq<'T>, ?directory : string, ?serializer : ISerializer, ?cacheByDefault : bool) : Local<CloudSequence<'T>> = local {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration> ()
        let directory = defaultArg directory config.DefaultDirectory
        let _serializer = defaultArg serializer config.Serializer
        let deserializer = serializer |> Option.map (fun ser stream -> ser.SeqDeserialize<'T>(stream, leaveOpen = false))
        let path = config.FileStore.GetRandomFilePath directory
        let writer (stream : Stream) = async {
            return _serializer.SeqSerialize<'T>(stream, values, leaveOpen = false) |> int64
        }
        let! length = ofAsync <| config.FileStore.Write(path, writer)
        return new CloudSequence<'T>(path, Some length, deserializer, ?cacheByDefault = cacheByDefault)
    }

    /// <summary>
    ///     Creates a collection of Cloud sequences partitioned by file size.
    /// </summary>
    /// <param name="values">Input sequence.</param>
    /// <param name="maxPartitionSize">Maximum size in bytes per Cloud sequence partition.</param>
    /// <param name="directory"></param>
    /// <param name="serializer"></param>
    /// <param name="directory">FileStore directory used for Cloud sequence. Defaults to execution context.</param>
    /// <param name="serializer">Serializer used in sequence serialization. Defaults to execution context.</param>
    /// <param name="cacheByDefault">Enable caching by default on every node where cell is dereferenced. Defaults to false.</param>
    static member NewPartitioned(values : seq<'T>, maxPartitionSize : int64, ?directory : string, ?serializer : ISerializer, ?cacheByDefault : bool) : Local<CloudSequence<'T> []> = local {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration> ()
        let directory = defaultArg directory config.DefaultDirectory
        let _serializer = defaultArg serializer config.Serializer
        let deserializer = serializer |> Option.map (fun ser stream -> ser.SeqDeserialize<'T>(stream, leaveOpen = false))
        return! ofAsync <| async {
            if maxPartitionSize <= 0L then return invalidArg "maxPartitionSize" "Must be greater that 0."

            let seqs = new ResizeArray<CloudSequence<'T>>()
            let currentStream = ref Unchecked.defaultof<Stream>
            let splitNext () = currentStream.Value.Position >= maxPartitionSize
            let partitionedValues = PartitionedEnumerable.ofSeq splitNext values
            for partition in partitionedValues do
                let path = config.FileStore.GetRandomFilePath directory
                let writer (stream : Stream) = async {
                    currentStream := stream
                    return _serializer.SeqSerialize<'T>(stream, partition, leaveOpen = false) |> int64
                }
                let! length = config.FileStore.Write(path, writer)
                let seq = new CloudSequence<'T>(path, Some length, deserializer, ?cacheByDefault = cacheByDefault)
                seqs.Add seq

            return seqs.ToArray()
        }
    }

    /// <summary>
    ///     Creates a CloudSequence from file path with user-provided deserialization function.
    /// </summary>
    /// <param name="path">Path to file.</param>
    /// <param name="deserializer">Sequence deserializer function.</param>
    /// <param name="force">Force evaluation. Defaults to false.</param>
    /// <param name="cacheByDefault">Enable caching by default on every node where cell is dereferenced. Defaults to false.</param>
    static member FromFile<'T>(path : string, ?deserializer : Stream -> seq<'T>, ?force : bool, ?cacheByDefault : bool) : Local<CloudSequence<'T>> = local {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration> ()
        let cseq = new CloudSequence<'T>(path, None, deserializer, ?cacheByDefault = cacheByDefault)
        if defaultArg force false then
            let! _ = cseq.Count in ()
        else
            let! exists = ofAsync <| config.FileStore.FileExists path
            if not exists then return raise <| new FileNotFoundException(path)
            
        return cseq
    }

    /// <summary>
    ///     Creates an already existing sequence of given type in provided file store.
    /// </summary>
    /// <param name="path">Path to Cloud sequence.</param>
    /// <param name="serializer">Serializer used in sequence serialization. Defaults to execution context.</param>
    /// <param name="force">Force evaluation. Defaults to false.</param>
    /// <param name="cacheByDefault">Enable caching by default on every node where cell is dereferenced. Defaults to false.</param>
    static member FromFile<'T>(path : string, serializer : ISerializer, ?force : bool, ?cacheByDefault) : Local<CloudSequence<'T>> = local {
        let deserializer stream = serializer.SeqDeserialize<'T>(stream, leaveOpen = false)
        return! CloudSequence.FromFile<'T>(path, deserializer = deserializer, ?force = force, ?cacheByDefault = cacheByDefault)
    }

    /// <summary>
    ///     Creates a CloudSequence from text file.
    /// </summary>
    /// <param name="path">Path to file.</param>
    /// <param name="textDeserializer">Text deserializer function.</param>
    /// <param name="encoding">Text encoding. Defaults to UTF8.</param>
    /// <param name="force">Force evaluation. Defaults to false.</param>
    /// <param name="cacheByDefault">Enable caching by default on every node where cell is dereferenced. Defaults to false.</param>
    static member FromTextFile<'T>(path : string, textDeserializer : StreamReader -> seq<'T>, ?encoding : Encoding, ?force : bool, ?cacheByDefault : bool) : Local<CloudSequence<'T>> = local {
        let deserializer (stream : Stream) =
            let sr = 
                match encoding with
                | None -> new StreamReader(stream)
                | Some e -> new StreamReader(stream, e)

            textDeserializer sr 
        
        return! CloudSequence.FromFile(path, deserializer, ?force = force, ?cacheByDefault = cacheByDefault)
    }

    /// <summary>
    ///     Creates a string CloudSequence as line reader abstraction.
    /// </summary>
    /// <param name="path">Path to file.</param>
    /// <param name="encoding">Text encoding. Defaults to UTF8.</param>
    /// <param name="force">Force evaluation. Defaults to false.</param>
    /// <param name="cacheByDefault">Enable caching by default on every node where cell is dereferenced. Defaults to false.</param>
    static member FromLineSeparatedTextFile(path : string, ?encoding : Encoding, ?force : bool, ?cacheByDefault : bool) : Local<CloudSequence<string>> = local {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration> ()
        let cseq = TextLineSequence(path, ?encoding = encoding, ?cacheByDefault = cacheByDefault)
        if defaultArg force false then
            let! _ = cseq.Count in ()
        else
            let! exists = ofAsync <| config.FileStore.FileExists path
            if not exists then return raise <| new FileNotFoundException(path)

        return cseq :> _
    }


[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CloudSequence =
    
    /// <summary>
    ///     Returns an enumerable that lazily fetches elements of the cloud sequence from store.
    /// </summary>
    /// <param name="cseq">Input cloud sequence</param>
    let toSeq (cseq : CloudSequence<'T>) = cseq.ToEnumerable()

    /// <summary>
    ///     Fetches all elements of the cloud sequence and returns them as a local array.
    /// </summary>
    /// <param name="cseq">Input cloud sequence</param>
    let toArray (cseq : CloudSequence<'T>) = cseq.ToArray()

    /// <summary>
    ///     Cache contents to local execution context. Returns true iff succesful.
    /// </summary>
    /// <param name="cseq">Input cloud sequence.</param>
    let cache (cseq : CloudSequence<'T>) = cseq.ForceCache()