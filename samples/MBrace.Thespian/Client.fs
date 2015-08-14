﻿namespace MBrace.Thespian

open System
open System.IO
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading

open Nessos.Thespian

open MBrace.Core
open MBrace.Core.Internals

open MBrace.Runtime
open MBrace.Runtime.Utils
open MBrace.Runtime.Store

open MBrace.Thespian.Runtime
open MBrace.Thespian.Runtime.WorkerConfiguration

/// A system logger that writes entries to stdout
type ConsoleLogger = MBrace.Runtime.ConsoleLogger
/// Log level used by the MBrace runtime implementation
type LogLevel = MBrace.Runtime.LogLevel

/// Defines a client object used for administering MBrace worker processes.
[<AutoSerializable(false)>]
type MBraceWorker private (uri : string) =
    let protectAsync (w:Async<'T>) = async {
        try return! w
        with 
        | :? CommunicationException
        | :? UnknownRecipientException as e ->
            let msg = sprintf "Failed to communicate with mbrace worker at '%s'." uri
            return raise <| new Exception(msg, e)
    }

    let protect (f : unit -> 'T) =
        try f ()
        with 
        | :? CommunicationException
        | :? UnknownRecipientException as e ->
            let msg = sprintf "Failed to communicate with mbrace worker at '%s'." uri
            raise <| new Exception(msg, e)

    let aref = parseUri uri
    let state = CacheAtom.Create(protectAsync(async { return! aref <!- GetState }))
    do ignore state.Value

    static let mutable executable = None

    /// Gets or sets a local path to the MBrace.Thespian worker executable.
    /// This is used for spawning of child worker processes from the client.
    static member LocalExecutable
        with get () = match executable with None -> invalidOp "unset executable path." | Some e -> e
        and set path = 
            let path = Path.GetFullPath path
            if File.Exists path then executable <- Some path
            else raise <| FileNotFoundException(path)

    /// MBrace uri identifier for worker instance.
    member __.Uri = uri

    /// <summary>
    ///     Sends a kill signal to the worker process.
    /// </summary>
    /// <param name="signal">Process exit signal. Defaults to 1.</param>
    member __.Kill(?signal : int) : unit = 
        let signal = defaultArg signal 1
        protect(fun () -> aref <-- Kill signal)

    /// <summary>
    ///     Resets the cluster state of the worker process.
    /// </summary>
    member __.Reset() =
        protect(fun () -> aref <!= Reset)

    /// Gets whether worker process is idle.
    member __.IsIdle : bool =
        match state.Value with
        | None -> true
        | _ -> false
    
    /// Gets whether worker process acts as host (master node) to an MBrace cluster.
    member __.IsMasterNode : bool =
        match state.Value with
        | None -> false
        | Some(isMaster,_) -> true

    /// Gets whether worker process is subscribed (slave node) to an MBrace cluster state.
    member __.IsWorkerNode : bool =
        match state.Value with
        | None -> false
        | Some(isMaster,_) -> not isMaster

    /// Gets the runtime state that the worker is participating, if applicable.
    member internal __.RuntimeState : RuntimeState option =
        match state.Value with
        | None -> None
        | Some(_,state) -> Some state

    /// Initializes worker instance as master node in new cluster state.
    member internal __.InitAsClusterMasterNode(storeConfig : CloudFileStoreConfiguration, ?miscResources : ResourceRegistry) = async {
        return! protectAsync (aref <!- fun ch -> InitMasterNode(storeConfig, miscResources, ch))
    }

    /// Initializes worker instance as slave node in supplied cluster state.
    member internal __.SubscribeToCluster(state : RuntimeState) = async {
        return! protectAsync (aref <!- fun ch -> Subscribe(state, ch))
    }

    /// <summary>
    ///     Connects to an MBrace worker process with supplied MBrace uri.
    /// </summary>
    /// <param name="uri">MBrace uri to worker.</param>
    static member Connect(uri : string) : MBraceWorker =
        new MBraceWorker(uri)

    /// <summary>
    ///     Gets an MBrace worker client instance from supplied WorkerRef object.
    /// </summary>
    /// <param name="worker">Input worker ref.</param>
    static member Connect(worker : IWorkerRef) : MBraceWorker =
        new MBraceWorker(worker.Id)

    /// <summary>
    ///     Asynchronously spawns a new worker process in the current machine with supplied configuration parameters.
    /// </summary>
    /// <param name="hostname">Hostname or IP address on which the worker will be listening to.</param>
    /// <param name="port">Master TCP port used by the worker. Defaults to self-assigned.</param>
    /// <param name="workingDirectory">Working directory used by the worker. Defaults to system temp folder.</param>
    /// <param name="maxConcurrentJobs">Maximum number of concurrent jobs executed if running as slave node. Defaults to 20.</param>
    /// <param name="logLevel">Loglevel used by the worker process. Defaults to no log level.</param>
    /// <param name="logFiles">Paths to text logfiles written to by worker process.</param>
    /// <param name="useAppDomainIsolation">Use AppDomain isolation when executing cloud jobs. Defaults to true.</param>
    /// <param name="runAsBackground">Run as background process. Defaults to false.</param>
    static member SpawnAsync (?hostname : string, ?port : int, ?workingDirectory : string, ?maxConcurrentJobs : int,
                                    ?logLevel : LogLevel, ?logFiles : seq<string>, ?useAppDomainIsolation : bool, ?runAsBackground : bool) =
        async {
            let exe = MBraceWorker.LocalExecutable
            let logFiles = match logFiles with None -> [] | Some ls -> Seq.toList ls
            let runAsBackground = defaultArg runAsBackground false
            let config = 
                { MaxConcurrentJobs = maxConcurrentJobs ; UseAppDomainIsolation = useAppDomainIsolation ;
                    Hostname = hostname ; Port = port ; WorkingDirectory = workingDirectory ;
                    LogLevel = logLevel ; LogFiles = logFiles ; Parent = None }

            let! ref = spawnAsync exe runAsBackground config
            return new MBraceWorker(mkUri ref)
        }

    /// <summary>
    ///     Spawns a new worker process in the current machine with supplied configuration parameters.
    /// </summary>
    /// <param name="hostname">Hostname or IP address on which the worker will be listening to.</param>
    /// <param name="port">Master TCP port used by the worker. Defaults to self-assigned.</param>
    /// <param name="workingDirectory">Working directory used by the worker. Defaults to system temp folder.</param>
    /// <param name="maxConcurrentJobs">Maximum number of concurrent jobs executed if running as slave node. Defaults to 20.</param>
    /// <param name="logLevel">Loglevel used by the worker process. Defaults to no log level.</param>
    /// <param name="logFiles">Paths to text logfiles written to by worker process.</param>
    /// <param name="useAppDomainIsolation">Use AppDomain isolation when executing cloud jobs. Defaults to true.</param>
    /// <param name="runAsBackground">Run as background process. Defaults to false.</param>
    static member Spawn (?hostname : string, ?port : int, ?workingDirectory : string, ?maxConcurrentJobs : int,
                                ?logLevel : LogLevel, ?logFiles : seq<string>, ?useAppDomainIsolation : bool, ?runAsBackground : bool) =

        MBraceWorker.SpawnAsync(?port = port, ?maxConcurrentJobs = maxConcurrentJobs, ?logLevel = logLevel, 
                                ?logFiles = logFiles, ?runAsBackground = runAsBackground, ?useAppDomainIsolation = useAppDomainIsolation)
        |> Async.RunSync


/// MBrace.Thespian client object used to manage cluster and submit jobs for computation.
[<AutoSerializable(false)>]
type MBraceCluster private (state : RuntimeState, manager : IRuntimeManager) =
    inherit MBraceClient(manager)
    static do Config.Initialize(populateDirs = true)
    static let initWorkers logLevel (count : int) (target : RuntimeState) = async {
        if count < 0 then invalidArg "workerCount" "must be non-negative."
        let exe = MBraceWorker.LocalExecutable
        let attachNewWorker _ = async {
            let! (node : MBraceWorker) = MBraceWorker.SpawnAsync(?logLevel = logLevel)
            do! node.SubscribeToCluster(target)
        }

        do! Array.init count attachNewWorker |> Async.Parallel |> Async.Ignore
    }

    private new (state : RuntimeState, logLevel : LogLevel option) = 
        let manager = state.GetLocalRuntimeManager()
        Actor.Logger <- manager.SystemLogger
        manager.LogLevel <- defaultArg logLevel LogLevel.Info
        new MBraceCluster(state, manager)

    /// Gets the the uri identifier of the process hosting the cluster.
    member __.Uri = manager.Id.Id

    /// <summary>
    ///     Spawns provided count of new local worker processes and subscibes them to the cluster.
    /// </summary>
    /// <param name="count">Number of workers to be spawned and appended.</param>
    member __.AttachNewLocalWorkers (workerCount : int, ?logLevel : LogLevel) =
        let _ = initWorkers logLevel workerCount state |> Async.RunSync
        ()

    /// <summary>
    ///     Subscribe a given worker instance as slave to current cluster.
    /// </summary>
    /// <param name="worker">Worker to be attached.</param>
    member __.AttachWorker (worker : MBraceWorker) =
        worker.SubscribeToCluster(state) |> Async.RunSync

    /// <summary>
    ///     Detaches supplied worker from current cluster.
    /// </summary>
    /// <param name="worker">Worker to be detached.</param>
    member __.DetachWorker (worker : IWorkerRef) =
        let node = MBraceWorker.Connect worker
        node.Reset()

    /// <summary>
    ///     Sends a kill signal to supplied worker process.
    /// </summary>
    /// <param name="worker">Worker to be killed.</param>
    member __.KillWorker (worker : IWorkerRef) =
        let node = MBraceWorker.Connect worker
        node.Kill()

    /// Sends a kill signal to all worker processes currently subscribed to cluster.
    member __.KillAllWorkers () =
        base.Workers |> Array.Parallel.iter (fun w -> __.KillWorker w)

    /// <summary>
    ///     Initializes a new MBrace cluster running within the current machine.
    ///     Cluster state will be hosted in the current client process and workers nodes
    ///     are processes that will be spawned for this purpose.
    /// </summary>
    /// <param name="workerCount">Number of workers to spawn for cluster.</param>
    /// <param name="storeConfig">File store configuration to be used for cluster. Defaults to file system store in the temp folder.</param>
    /// <param name="resources">Additional resources to be appended to the MBrace execution context.</param>
    /// <param name="logLevel">Sets the log level for the cluster. Defaults to LogLevel.Info.</param>
    static member InitOnCurrentMachine(workerCount : int, ?storeConfig : CloudFileStoreConfiguration, 
                                        ?resources : ResourceRegistry, ?logLevel : LogLevel) : MBraceCluster =

        if workerCount < 0 then invalidArg "workerCount" "must be non-negative."
        let storeConfig = 
            match storeConfig with 
            | Some c -> c
            | None -> 
                let fs = FileSystemStore.CreateSharedLocal()
                CloudFileStoreConfiguration.Create fs

        let state = RuntimeState.Create(storeConfig, ?miscResources = resources)
        let _ = initWorkers logLevel workerCount state |> Async.RunSync
        new MBraceCluster(state, logLevel)

    /// <summary>
    ///     Initializes a new cluster state that is hosted on provided worker instance.
    /// </summary>
    /// <param name="worker">MBrace worker to host the cluster state.</param>
    /// <param name="storeConfig">File store configuration to be used for cluster. Defaults to file system store in the temp folder.</param>
    /// <param name="miscResources">Additional resources to be appended to the MBrace execution context.</param>
    /// <param name="logLevel">Sets the log level for the client instance. Defaults to LogLevel.Info.</param>
    static member InitOnWorker(worker : MBraceWorker, ?storeConfig : CloudFileStoreConfiguration, 
                                    ?miscResources : ResourceRegistry, ?logLevel : LogLevel) : MBraceCluster =

        let storeConfig = 
            match storeConfig with 
            | Some c -> c
            | None -> 
                let fs = FileSystemStore.CreateSharedLocal()
                CloudFileStoreConfiguration.Create fs

        let state = worker.InitAsClusterMasterNode(storeConfig, ?miscResources = miscResources) |> Async.RunSync
        new MBraceCluster(state, logLevel)

    /// <summary>
    ///     Connects to the cluster instance that is active in supplied MBrace worker instance.
    /// </summary>
    /// <param name="worker">Worker instance to extract runtime state from.</param>
    /// <param name="logLevel">Sets the log level for the client instance. Defaults to LogLevel.Info.</param>
    static member Connect(worker : MBraceWorker, ?logLevel : LogLevel) : MBraceCluster =
        match worker.RuntimeState with
        | None -> invalidOp "Worker '%s' is not part of an active cluster." worker.Uri
        | Some state -> new MBraceCluster(state, logLevel)

    /// <summary>
    ///     Connects to the cluster instance that is identified by supplied MBrace uri.
    /// </summary>
    /// <param name="uri">MBrace uri to connect to.</param>
    /// <param name="logLevel">Sets the log level for the client instance. Defaults to LogLevel.Info.</param>
    static member Connect(uri : string, ?logLevel : LogLevel) : MBraceCluster = 
        MBraceCluster.Connect(MBraceWorker.Connect uri, ?logLevel = logLevel)

[<AutoOpen>]
module ClientExtensions =

    type IWorkerRef with
        /// Gets a worker management object from supplied worker ref.
        member w.WorkerManager = MBraceWorker.Connect(w)