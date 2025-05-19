module Ordo.Synchroniser

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open EventStore.Client
open Ordo.Core.Rebuilding
open Ordo.Core.Model
open Ordo.Core.Events
open Ordo.Core

type JobStatusCounts = {
    ImmediateScheduled: int
    DueScheduled: int
    FutureScheduled: int
    Triggered: int
    Executed: int
    Cancelled: int
    Failed: int
    Unknown: int
}

type SynchroniserMetrics = {
    mutable TotalJobs: int64
    mutable EventsProcessed: int64
    mutable LastEventProcessed: DateTimeOffset
    mutable SubscriptionDrops: int64
    mutable LastSubscriptionDrop: DateTimeOffset option
    mutable ProcessingErrors: int64
}

type JobStateWithVersion = {
    Job: Job
    Version: uint64
}

type SynchroniserState =
    { Client: EventStoreClient
      Logger: ILogger
      Jobs: ConcurrentDictionary<string, JobStateWithVersion>
      mutable Subscription: IDisposable option
      Metrics: SynchroniserMetrics
      mutable IsStartupPhase: bool
      ConfigService: JobTypeConfigService }

type ProjectionSynchroniser(client: EventStoreClient, logger: ILogger, configService: JobTypeConfigService) =
    let metrics = {
        TotalJobs = 0L
        EventsProcessed = 0L
        LastEventProcessed = DateTimeOffset.MinValue
        SubscriptionDrops = 0L
        LastSubscriptionDrop = None
        ProcessingErrors = 0L
    }

    let state =
        { Client = client
          Logger = logger
          Jobs = ConcurrentDictionary<string, JobStateWithVersion>() 
          Subscription = None
          Metrics = metrics
          IsStartupPhase = true
          ConfigService = configService }

    let handleEvent (subscription: StreamSubscription) (resolvedEvent: ResolvedEvent) (cancellationToken: CancellationToken) : Task =
        task {
            if not (resolvedEvent.OriginalStreamId.StartsWith("ordo-job-")) then
                return ()
            
            if state.IsStartupPhase then
                logger.LogDebug("Processing startup event from stream {StreamId}", resolvedEvent.OriginalStreamId)
            else
                logger.LogDebug("Received job event from stream {StreamId}", resolvedEvent.OriginalStreamId)
            
            match resolve resolvedEvent with
            | Some jobEvent ->
                let jobId = getJobId jobEvent
                try
                    let updateAction (current: JobStateWithVersion) =
                        let updatedJob = applyEvent current.Job jobEvent
                        if not state.IsStartupPhase && current.Job.Status <> updatedJob.Status then
                            logger.LogInformation("Job {JobId} status changed: {OldStatus} -> {NewStatus}", 
                                jobId, current.Job.Status, updatedJob.Status)
                        { Job = updatedJob; Version = (uint64 (resolvedEvent.Event.EventNumber.ToInt64())) }

                    let newJobState = state.Jobs.AddOrUpdate(
                        jobId, 
                        (fun _ -> 
                            match jobEvent with
                            | EventScheduled evt ->
                                let initialJob = initialState evt
                                if not state.IsStartupPhase then
                                    logger.LogInformation("New job {JobId} created with status {Status}", 
                                        jobId, initialJob.Status)
                                { Job = initialJob; Version = (uint64 (resolvedEvent.Event.EventNumber.ToInt64())) }
                            | _ -> failwith "First event for a job must be EventScheduled"),
                        (fun _ existing -> updateAction existing)
                    )

                    metrics.EventsProcessed <- metrics.EventsProcessed + 1L
                    metrics.LastEventProcessed <- DateTimeOffset.UtcNow
                    metrics.TotalJobs <- int64 state.Jobs.Count

                    if not state.IsStartupPhase then
                        logger.LogDebug("Processed {JobEventType} for job {JobId}", 
                            jobEvent.GetType().Name, jobId)
                with ex ->
                    metrics.ProcessingErrors <- metrics.ProcessingErrors + 1L
                    logger.LogError(ex, "Failed to apply {JobEventType} for job {JobId}", 
                        jobEvent.GetType().Name, jobId)
            | None -> ()
        } :> Task

    let subscriptionDropped (subscription: StreamSubscription) (reason: SubscriptionDroppedReason) (ex: Exception) =
        metrics.SubscriptionDrops <- metrics.SubscriptionDrops + 1L
        metrics.LastSubscriptionDrop <- Some DateTimeOffset.UtcNow
        
        logger.LogWarning("Subscription {SubscriptionId} dropped. Reason: {Reason}", subscription.SubscriptionId, reason)
        if ex <> null then 
            logger.LogError(ex, "Subscription dropped due to exception.")
        
        state.Subscription <- None

    member _.Start(cancellationToken: CancellationToken) : Task =
        task {
            if state.Subscription.IsNone then
                logger.LogInformation("Starting projection synchroniser")
                
                let! sub = 
                    state.Client.SubscribeToAllAsync(
                        FromAll.Start,
                        eventAppeared = handleEvent, 
                        subscriptionDropped = subscriptionDropped,
                        cancellationToken = cancellationToken)

                state.Subscription <- Some sub
                
                // Wait a short time to process initial events
                do! Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)
                state.IsStartupPhase <- false
                
                logger.LogInformation("Projection synchroniser ready with {JobCount} jobs loaded", state.Jobs.Count)
            else
                 logger.LogInformation("Projection synchroniser already running")
        } :> Task

    member this.Stop(cancellationToken: CancellationToken) : Task =
        task {
            match state.Subscription with
            | Some sub ->
                logger.LogInformation("Stopping projection synchroniser")
                sub.Dispose()
                state.Subscription <- None
            | None -> 
                logger.LogInformation("Projection synchroniser not running")
            ()
        } :> Task

    member _.GetJobState(jobId: string) : (Job * uint64) option =
        match state.Jobs.TryGetValue(jobId) with
        | true, state -> Some (state.Job, state.Version)
        | false, _ -> None

    member private this.GetConfiguredDelay(jobType: string) : Task<TimeSpan> =
        task {
            let! config = configService.GetConfigForJobType(jobType)
            return match config with
                   | Some config -> TimeSpan.Parse(config.DefaultDelay)
                   | None -> TimeSpan.FromHours(1.0)
        }

    member this.GetDueJobs(currentTime: DateTimeOffset) : Task<Job list> =
        task {
            let mutable result = []
            for jobState in state.Jobs.Values do
                match jobState.Job.Status with
                | JobStatus.StatusScheduled ->
                    match jobState.Job.Schedule with
                    | Immediate -> 
                        result <- jobState.Job :: result
                    | Precise scheduledTime when scheduledTime <= currentTime ->
                        result <- jobState.Job :: result
                    | Configured config ->
                        let! delay = this.GetConfiguredDelay(config.Type.ToString())
                        let scheduledTime = config.From.Add(delay)
                        if scheduledTime <= currentTime then
                            result <- jobState.Job :: result
                    | _ -> ()
                | _ -> ()
            return result
        }

    member this.GetStatus(currentTime: DateTimeOffset) : Task<((Job * uint64) list * JobStatusCounts)> =
        task {
            let mutable result = []
            for jobState in state.Jobs.Values do
                match jobState.Job.Status with
                | JobStatus.StatusScheduled ->
                    match jobState.Job.Schedule with
                    | Immediate -> 
                        result <- (jobState.Job, jobState.Version) :: result
                    | Precise scheduledTime when scheduledTime <= currentTime ->
                        result <- (jobState.Job, jobState.Version) :: result
                    | Configured config ->
                        let! delay = this.GetConfiguredDelay(config.Type.ToString())
                        let scheduledTime = config.From.Add(delay)
                        if scheduledTime <= currentTime then
                            result <- (jobState.Job, jobState.Version) :: result
                    | _ -> ()
                | _ -> ()
            let status = this.GetJobStatusCounts()
            return (result, status)
        }

    member this.GetAllJobs() : (Job * uint64) list =
        let jobs = 
            state.Jobs.Values
            |> Seq.map (fun jobState -> 
                let job = jobState.Job
                let version = jobState.Version
                let scheduledTime = 
                    match job.Schedule with
                    | Immediate -> DateTimeOffset.UtcNow
                    | Precise time -> time
                    | Configured config -> config.From.Add(TimeSpan.FromSeconds(10.0)) // Using 10 second default delay
                let jobWithScheduledTime = { job with ScheduledTime = Some scheduledTime }
                (jobWithScheduledTime, version))
            |> Seq.toList
        logger.LogDebug("Retrieved {Count} jobs", jobs.Length)
        jobs

    member this.GetJobStatusCounts() : JobStatusCounts =
        let currentTime = DateTimeOffset.UtcNow
        state.Jobs.Values
        |> Seq.fold (fun counts jobState ->
            match jobState.Job.Status with
            | JobStatus.StatusScheduled ->
                match jobState.Job.Schedule with
                | Immediate -> { counts with ImmediateScheduled = counts.ImmediateScheduled + 1 }
                | Precise scheduledTime when scheduledTime <= currentTime -> 
                    { counts with DueScheduled = counts.DueScheduled + 1 }
                | Precise _ -> 
                    { counts with FutureScheduled = counts.FutureScheduled + 1 }
                | Configured config ->
                    let delay = TimeSpan.Parse(configService.GetConfigForJobType(config.Type.ToString()).Result.Value.DefaultDelay)
                    let scheduledTime = config.From.Add(delay)
                    if scheduledTime <= currentTime then
                        { counts with DueScheduled = counts.DueScheduled + 1 }
                    else
                        { counts with FutureScheduled = counts.FutureScheduled + 1 }
            | JobStatus.StatusTriggered -> 
                { counts with Triggered = counts.Triggered + 1 }
            | JobStatus.StatusExecuted -> 
                { counts with Executed = counts.Executed + 1 }
            | JobStatus.StatusCancelled -> 
                { counts with Cancelled = counts.Cancelled + 1 }
            | _ -> 
                { counts with Unknown = counts.Unknown + 1 }
        ) {
            ImmediateScheduled = 0
            DueScheduled = 0
            FutureScheduled = 0
            Triggered = 0
            Executed = 0
            Cancelled = 0
            Failed = 0
            Unknown = 0
        }

    member _.GetMetrics() : SynchroniserMetrics =
        metrics 
