module Ordo.Synchroniser

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open EventStore.Client
open Ordo.Core.JobState
open Ordo.Core.Events

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
      Jobs: ConcurrentDictionary<Guid, JobStateWithVersion>
      mutable Subscription: IDisposable option
      Metrics: SynchroniserMetrics }

type ProjectionSynchroniser(client: EventStoreClient, logger: ILogger) =
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
          Jobs = ConcurrentDictionary<Guid, JobStateWithVersion>() 
          Subscription = None
          Metrics = metrics }

    let handleEvent (subscription: StreamSubscription) (resolvedEvent: ResolvedEvent) (cancellationToken: CancellationToken) : Task =
        task {
            match tryParseJobEvent resolvedEvent with
            | Some jobEvent ->
                let jobId = getJobId jobEvent
                try
                    let updateAction (current: JobStateWithVersion) =
                        let updatedJob = applyEvent current.Job jobEvent
                        { Job = updatedJob; Version = (uint64 (resolvedEvent.Event.EventNumber.ToInt64())) }

                    let newJobState = state.Jobs.AddOrUpdate(
                        jobId, 
                        { Job = initialState jobId; Version = (uint64 (resolvedEvent.Event.EventNumber.ToInt64())) },
                        (fun _ existing -> updateAction existing)
                    )

                    state.Metrics.EventsProcessed <- state.Metrics.EventsProcessed + 1L
                    state.Metrics.LastEventProcessed <- DateTimeOffset.UtcNow
                    state.Metrics.TotalJobs <- int64 state.Jobs.Count

                    state.Logger.LogDebug("Processed {JobEventType} for job {JobId}. New status: {Status}", 
                                           jobEvent.GetType().Name, jobId, newJobState.Job.Status)
                with ex ->
                    state.Metrics.ProcessingErrors <- state.Metrics.ProcessingErrors + 1L
                    state.Logger.LogError(ex, "Failed to apply {JobEventType} for job {JobId} from stream {StreamId}", 
                                           jobEvent.GetType().Name, jobId, resolvedEvent.OriginalStreamId)
            | None -> 
                if resolvedEvent.Event.EventType.StartsWith("$") |> not then
                    state.Logger.LogTrace("Ignoring event type {EventType} from stream {StreamId}", 
                                          resolvedEvent.Event.EventType, resolvedEvent.OriginalStreamId)
                ()
        } :> Task

    let subscriptionDropped (subscription: StreamSubscription) (reason: SubscriptionDroppedReason) (ex: Exception) =
        state.Metrics.SubscriptionDrops <- state.Metrics.SubscriptionDrops + 1L
        state.Metrics.LastSubscriptionDrop <- Some DateTimeOffset.UtcNow
        
        state.Logger.LogWarning("Subscription {SubscriptionId} dropped. Reason: {Reason}", subscription.SubscriptionId, reason)
        if ex <> null then 
            state.Logger.LogError(ex, "Subscription dropped due to exception.")
        
        state.Subscription <- None

    member _.Start(cancellationToken: CancellationToken) : Task =
        task {
            if state.Subscription.IsNone then
                state.Logger.LogInformation("Starting projection synchroniser subscription to $all...")
                
                let! sub = 
                    state.Client.SubscribeToAllAsync(
                        FromAll.Start, 
                        eventAppeared = handleEvent, 
                        subscriptionDropped = subscriptionDropped,
                        cancellationToken = cancellationToken)

                state.Subscription <- Some sub
                state.Logger.LogInformation("Subscription started with ID: {SubscriptionId}", sub.SubscriptionId)
            else
                 state.Logger.LogInformation("Subscription already active.")

        } :> Task

    member this.Stop(cancellationToken: CancellationToken) : Task =
        task {
            match state.Subscription with
            | Some sub ->
                state.Logger.LogInformation("Stopping projection synchroniser subscription...")
                sub.Dispose()
                state.Subscription <- None
                state.Logger.LogInformation("Subscription stopped.")
            | None -> 
                state.Logger.LogInformation("Synchroniser subscription was not active.")
            ()
        } :> Task

    member _.GetJobState(jobId: Guid) : (Job * uint64) option =
        match state.Jobs.TryGetValue(jobId) with
        | true, state -> Some (state.Job, state.Version)
        | false, _ -> None

    member _.GetDueJobs(currentTime: DateTimeOffset) : (Job * uint64) list =
        state.Jobs.Values
        |> Seq.filter (fun state -> 
            match state.Job.Status, state.Job.ScheduledTime with
            | JobStatus.StatusScheduled, Some scheduledTime when scheduledTime <= currentTime -> true
            | _ -> false)
        |> Seq.map (fun state -> (state.Job, state.Version))
        |> List.ofSeq

    member _.GetMetrics() : SynchroniserMetrics =
        state.Metrics 