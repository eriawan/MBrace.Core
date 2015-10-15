﻿namespace MBrace.Runtime

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open System.Runtime.Serialization
open System.Collections.Generic
open System.Collections.Concurrent

open Nessos.FsPickler
open Nessos.Vagabond

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Runtime.Utils
open MBrace.Runtime.Utils.PrettyPrinters

[<NoEquality; NoComparison>]
type private CloudProcessData =
    {
        Id : string
        Info : CloudProcessInfo
        State : CloudProcessState
        StartTime : DateTimeOffset option
        ExecutionTime : TimeSpan option
        CompletionTime : DateTimeOffset option
    }
with
    static member OfCloudProcessEntry(entry : ICloudProcessEntry) = async {
        let! state = entry.GetState()
        return {
            Id = entry.Id
            Info = entry.Info
            State = state
            StartTime =
                match state.ExecutionTime with
                | NotStarted -> None
                | Started(st,_) -> Some (st.ToLocalTime())
                | Finished(st,_) -> Some (st.ToLocalTime())

            ExecutionTime =
                match state.ExecutionTime with
                | NotStarted -> None
                | Started(_,et) -> Some et
                | Finished(_,et) -> Some et

            CompletionTime =
                match state.ExecutionTime with
                | Finished(st,et) -> Some (st.ToLocalTime() + et)
                | _ -> None
        }
    }

/// Represents a cloud computation that is being executed in the cluster.
[<AbstractClass>]
type CloudProcess internal () =

    /// Gets the parent cancellation token for the cloud process
    abstract CancellationToken : ICloudCancellationToken

    /// <summary>
    ///     Asynchronously awaits boxed result of given cloud process.
    /// </summary>
    /// <param name="timeoutMilliseconds">Timeout in milliseconds. Defaults to infinite timeout.</param>
    abstract AwaitResultBoxed : ?timeoutMilliseconds:int -> Async<obj>
    /// <summary>
    ///     Return the result if available or None if not available.
    /// </summary>
    abstract TryGetResultBoxed : unit -> Async<obj option>

    /// Awaits the boxed result of the process.
    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    abstract ResultBoxed : obj

    /// Date of process execution start.
    abstract StartTime : DateTimeOffset option

    /// TimeSpan of executing process.
    abstract ExecutionTime : TimeSpan option

    /// DateTime of cloud process completion
    abstract CompletionTime : DateTimeOffset option

    /// Active number of work items related to the process.
    abstract ActiveWorkItems : int
    /// Number of work items that have been completed for process.
    abstract CompletedWorkItems : int
    /// Number of faults encountered while executing work items for process.
    abstract FaultedWorkItems : int
    /// Total number of work items related to the process.
    abstract TotalWorkItems : int
    /// Process execution status.
    abstract Status : CloudProcessStatus

    /// Cloud process identifier
    abstract Id : string
    /// Cloud process user-supplied name
    abstract Name : string option
    /// Process return type
    abstract Type : Type

    /// Cancels execution of given process
    abstract Cancel : unit -> unit

    /// Cloud process cloud logs observable
    [<CLIEvent>]
    abstract Logs : IEvent<CloudLogEntry>

    /// <summary>
    ///     Asynchronously fetches log all log entries generated by given cloud process.  
    /// </summary>
    /// <param name="filter">User-specified log entry filtering function.</param>
    abstract GetLogsAsync : ?filter:(CloudLogEntry -> bool) -> Async<CloudLogEntry []>

    /// <summary>
    ///     Asynchronously fetches log all log entries generated by given cloud process.  
    /// </summary>
    /// <param name="filter">User-specified log entry filtering function.</param>
    member __.GetLogs(?filter: CloudLogEntry -> bool) = __.GetLogsAsync(?filter = filter) |> Async.RunSync

    /// <summary>
    ///     Asynchronously fetches log all log entries generated by given cloud process.  
    /// </summary>
    /// <param name="filter">User-specified log entry filtering function.</param>
    abstract ShowLogs : ?filter:(CloudLogEntry -> bool) -> unit

    interface ICloudProcess with
        member x.Id: string = x.Id

        member x.AwaitResultBoxedAsync(?timeoutMilliseconds: int): Async<obj> = 
            x.AwaitResultBoxed(?timeoutMilliseconds = timeoutMilliseconds)
    
        member x.CancellationToken = x.CancellationToken
        member x.IsCanceled: bool = 
            match x.Status with
            | CloudProcessStatus.Canceled -> true
            | _ -> false
        
        member x.IsCompleted: bool = 
            match x.Status with
            | CloudProcessStatus.Completed -> true
            | _ -> false
        
        member x.IsFaulted: bool = 
            match x.Status with
            | CloudProcessStatus.Faulted | CloudProcessStatus.UserException -> true
            | _ -> false

        [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
        member x.ResultBoxed: obj = x.ResultBoxed
        member x.Status: CloudProcessStatus = x.Status
        member x.TryGetResultBoxedAsync(): Async<obj option> = x.TryGetResultBoxed()

    /// Gets a printed report on the current process status
    abstract GetInfo : unit -> string 

    /// Prints a report on the current process status to stdout
    member p.ShowInfo () : unit = Console.WriteLine(p.GetInfo())

/// Represents a cloud computation that is being executed in the cluster.
and [<Sealed; DataContract; NoEquality; NoComparison>] CloudProcess<'T> internal (source : ICloudProcessEntry, runtime : IRuntimeManager) =
    inherit CloudProcess()

    let [<DataMember(Name = "ProcessCompletionSource")>] entry = source
    let [<DataMember(Name = "RuntimeId")>] runtimeId = runtime.Id

    let mkCell () = CacheAtom.Create(CloudProcessData.OfCloudProcessEntry entry, intervalMilliseconds = 500)

    let [<IgnoreDataMember>] mutable lockObj = new obj()
    let [<IgnoreDataMember>] mutable cell = mkCell()
    let [<IgnoreDataMember>] mutable runtime = runtime
    let [<IgnoreDataMember>] mutable logPoller : ILogPoller<CloudLogEntry> option = None

    let getLogEvent() =
        match logPoller with
        | Some l -> l
        | None ->
            lock lockObj (fun () ->
                match logPoller with
                | None ->
                    let l = runtime.CloudLogManager.GetCloudLogPollerByProcess(source.Id) |> Async.RunSync
                    logPoller <- Some l
                    l
                | Some l -> l)

    /// Triggers elevation in event of serialization
    [<OnDeserialized>]
    let _onDeserialized (_ : StreamingContext) = 
        lockObj <- new obj()
        cell <- mkCell()
        runtime <- RuntimeManagerRegistry.Resolve runtimeId

    /// <summary>
    ///     Asynchronously awaits cloud process result
    /// </summary>
    /// <param name="timeoutMilliseconds">Timeout in milliseconds. Defaults to infinite timeout.</param>
    member __.AwaitResult (?timeoutMilliseconds:int) : Async<'T> = async {
        let timeoutMilliseconds = defaultArg timeoutMilliseconds Timeout.Infinite
        let! result = Async.WithTimeout(async { return! entry.AwaitResult() }, timeoutMilliseconds) 
        return unbox<'T> result.Value
    }

    /// <summary>
    ///     Attempts to get cloud process result. Returns None if not completed.
    /// </summary>
    member __.TryGetResult () : Async<'T option> = async {
        let! result = entry.TryGetResult()
        return result |> Option.map (fun r -> unbox<'T> r.Value)
    }

    /// Synchronously awaits cloud process result 
    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member __.Result : 'T = __.AwaitResult() |> Async.RunSync

    override __.AwaitResultBoxed (?timeoutMilliseconds:int) = async {
        let! r = __.AwaitResult(?timeoutMilliseconds = timeoutMilliseconds)
        return box r
    }

    override __.TryGetResultBoxed () = async {
        let! r = __.TryGetResult()
        return r |> Option.map box
    }

    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    override __.ResultBoxed = __.Result |> box

    override __.StartTime = cell.Value.StartTime
    override __.ExecutionTime = cell.Value.ExecutionTime
    override __.CompletionTime = cell.Value.CompletionTime

    override __.CancellationToken = entry.Info.CancellationTokenSource.Token
    /// Active number of work items related to the process.
    override __.ActiveWorkItems = cell.Value.State.ActiveWorkItemCount
    override __.CompletedWorkItems = cell.Value.State.CompletedWorkItemCount
    override __.FaultedWorkItems = cell.Value.State.FaultedWorkItemCount
    override __.TotalWorkItems = cell.Value.State.TotalWorkItemCount
    override __.Status = cell.Value.State.Status
    override __.Id = entry.Id
    override __.Name = entry.Info.Name
    override __.Type = typeof<'T>
    override __.Cancel() = entry.Info.CancellationTokenSource.Cancel()

    [<CLIEvent>]
    override __.Logs = getLogEvent() :> IEvent<CloudLogEntry>

    override __.GetInfo() = CloudProcessReporter.Report([|cell.Value|], "Process", false)

    override __.GetLogsAsync(?filter : CloudLogEntry -> bool) = async { 
        let! entries = runtime.CloudLogManager.GetAllCloudLogsByProcess __.Id
        let filtered = match filter with None -> entries | Some f -> Seq.filter f entries
        return filtered |> Seq.toArray
    }

    override __.ShowLogs (?filter : CloudLogEntry -> bool) =
        let entries = runtime.CloudLogManager.GetAllCloudLogsByProcess __.Id |> Async.RunSync
        let filtered = match filter with None -> entries | Some f -> Seq.filter f entries
        for e in filtered do Console.WriteLine(CloudLogEntry.Format(e, showDate = true))

    interface ICloudProcess<'T> with
        member x.AwaitResultAsync(timeoutMilliseconds: int option): Async<'T> =
            x.AwaitResult(?timeoutMilliseconds = timeoutMilliseconds)
        
        member x.CancellationToken: ICloudCancellationToken = 
            entry.Info.CancellationTokenSource.Token
        
        member x.Result: 'T = x.Result
        member x.Status: CloudProcessStatus = cell.Value.State.Status
        member x.TryGetResultAsync(): Async<'T option> = x.TryGetResult()

/// Cloud Process client object
and [<AutoSerializable(false)>] internal CloudProcessManagerClient(runtime : IRuntimeManager) =
    static let clients = new ConcurrentDictionary<IRuntimeId, IRuntimeManager> ()
    do clients.TryAdd(runtime.Id, runtime) |> ignore

    member __.Id = runtime.Id

    /// <summary>
    ///     Fetches cloud process by provided cloud process id.
    /// </summary>
    /// <param name="procId">Cloud process identifier.</param>
    member self.GetProcessBySource (entry : ICloudProcessEntry) = async {
        let! assemblies = runtime.AssemblyManager.DownloadAssemblies(entry.Info.Dependencies)
        let loadInfo = runtime.AssemblyManager.LoadAssemblies(assemblies)
        for li in loadInfo do
            match li with
            | NotLoaded id -> runtime.SystemLogger.Logf LogLevel.Error "could not load assembly '%s'" id.FullName 
            | LoadFault(id, e) -> runtime.SystemLogger.Logf LogLevel.Error "error loading assembly '%s':\n%O" id.FullName e
            | Loaded _ -> ()

        let returnType = runtime.Serializer.UnPickleTyped entry.Info.ReturnType
        let ex = Existential.FromType returnType
        let task = ex.Apply { 
            new IFunc<CloudProcess> with 
                member __.Invoke<'T> () = new CloudProcess<'T>(entry, runtime) :> CloudProcess
        }

        return task
    }

    member self.TryGetProcessById(id : string) = async {
        let! source = runtime.ProcessManager.TryGetProcessById id
        match source with
        | None -> return None
        | Some e ->
            let! t = self.GetProcessBySource e
            return Some t
    }


    member self.GetAllProcesses() = async {
        let! entries = runtime.ProcessManager.GetAllProcesses()
        return!
            entries
            |> Seq.map (fun e -> self.GetProcessBySource e)
            |> Async.Parallel
    }

    member __.ClearProcess(cloudProcess:CloudProcess) = async {
        do! runtime.ProcessManager.ClearProcess(cloudProcess.Id)
    }

    /// <summary>
    ///     Clears all processes from the runtime.
    /// </summary>
    member pm.ClearAllProcesses() = async {
        do! runtime.ProcessManager.ClearAllProcesses()
    }

    /// Gets a printed report of all currently executing processes
    member pm.FormatProcessesAsync() : Async<string> = async {
        let! entries = runtime.ProcessManager.GetAllProcesses()
        let! data = entries |> Seq.map CloudProcessData.OfCloudProcessEntry |> Async.Parallel
        return CloudProcessReporter.Report(data, "Processes", borders = false)
    }

    /// Gets a printed report of all currently executing processes
    member pm.FormatProcesses() : string = pm.FormatProcessesAsync() |> Async.RunSync

    /// Prints a report of all currently executing processes to stdout.
    member pm.ShowProcesses() : unit =
        /// TODO : add support for filtering processes
        Console.WriteLine(pm.FormatProcesses())

    static member TryGetById(id : IRuntimeId) = clients.TryFind id

    interface IDisposable with
        member __.Dispose() = clients.TryRemove runtime.Id |> ignore
        
         
and private CloudProcessReporter private () = 
    static let template : Field<CloudProcessData> list = 
        [ Field.create "Name" Left (fun p -> match p.Info.Name with Some n -> n | None -> "")
          Field.create "Process Id" Right (fun p -> p.Id)
          Field.create "Status" Right (fun p -> sprintf "%A" p.State.Status)
          Field.create "Execution Time" Left (fun p -> Option.toNullable p.ExecutionTime)
          Field.create "Work items" Center (fun p -> sprintf "%3d / %3d / %3d / %3d"  p.State.ActiveWorkItemCount p.State.FaultedWorkItemCount p.State.CompletedWorkItemCount p.State.TotalWorkItemCount)
          Field.create "Result Type" Left (fun p -> p.Info.ReturnTypeName) 
          Field.create "Start Time" Left (fun p -> p.StartTime |> Option.map (fun d -> d.LocalDateTime) |> Option.toNullable)
          Field.create "Completion Time" Left (fun p -> p.CompletionTime |> Option.map (fun d -> d.LocalDateTime) |> Option.toNullable)
        ]
    
    static member Report(processes : seq<CloudProcessData>, title : string, borders : bool) = 
        let ps = processes 
                 |> Seq.sortBy (fun p -> p.StartTime)
                 |> Seq.toList

        sprintf "%s\nWork items : Active / Faulted / Completed / Total\n" <| Record.PrettyPrint(template, ps, title, borders)