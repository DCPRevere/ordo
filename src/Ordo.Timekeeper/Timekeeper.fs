namespace Ordo.Timekeeper

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open EventStore.Client
open System.Text.Json
open Ordo.Core
open Ordo.Core.JobState
open Ordo.Core.Events
open Ordo.Synchroniser

type TimekeeperConfig = {
    EventStoreConnectionString: string
    CheckInterval: TimeSpan
}

type Timekeeper(esClient: EventStoreClient, logger: ILogger<Timekeeper>, config: TimekeeperConfig) =
    let mutable isRunning = false
    let mutable cancellationTokenSource = new CancellationTokenSource()
    let synchroniser = ProjectionSynchroniser(esClient, logger)

    member this.Start() =
        if isRunning then
            logger.LogWarning("Timekeeper is already running")
            Task.CompletedTask
        else
            isRunning <- true
            cancellationTokenSource <- new CancellationTokenSource()
            task {
                do! synchroniser.Start(cancellationTokenSource.Token)
                do! this.RunAsync(cancellationTokenSource.Token)
            }

    member this.Stop() =
        if not isRunning then
            logger.LogWarning("Timekeeper is not running")
            Task.CompletedTask
        else
            isRunning <- false
            task {
                do! synchroniser.Stop(cancellationTokenSource.Token)
                cancellationTokenSource.Cancel()
                cancellationTokenSource.Dispose()
            }

    member private this.RunAsync(ct: CancellationToken) =
        task {
            try
                while not ct.IsCancellationRequested do
                    try
                        do! this.CheckAndTriggerJobsAsync()
                        do! Task.Delay(config.CheckInterval, ct)
                    with
                    | :? OperationCanceledException ->
                        logger.LogInformation("Timekeeper operation canceled")
                        return ()
                    | ex ->
                        logger.LogError(ex, "Error in Timekeeper main loop")
                        do! Task.Delay(TimeSpan.FromSeconds(1), ct)
            with
            | :? OperationCanceledException ->
                logger.LogInformation("Timekeeper stopped")
            | ex ->
                logger.LogError(ex, "Error in Timekeeper main loop")
        }

    member private this.CheckAndTriggerJobsAsync() =
        task {
            try
                let now = DateTimeOffset.UtcNow
                let dueJobs = this.GetDueJobs(now)
                for (job, version) in dueJobs do
                    do! this.TriggerJobAsync(job, version)
            with ex ->
                logger.LogError(ex, "Error checking and triggering jobs")
        }

    member private this.TriggerJobAsync(job: Job, version: uint64) =
        task {
            try
                let now = DateTimeOffset.UtcNow
                logger.LogInformation("Triggering job {JobId} (scheduled for {ScheduledTime})", job.Id, job.ScheduledTime)
                let event = {
                    JobId = job.Id
                    TriggerTime = now
                    Timestamp = now
                }
                let eventJson = JsonSerializer.Serialize(event)
                let eventData = EventData(
                    Uuid.NewUuid(),
                    "JobTriggered",
                    System.Text.Encoding.UTF8.GetBytes(eventJson)
                )
                let! result = esClient.AppendToStreamAsync(
                    $"ordo-job-{job.Id}",
                    StreamRevision version,
                    [| eventData |]
                )
                logger.LogInformation("Successfully triggered job {JobId}", job.Id)
            with
            | :? WrongExpectedVersionException ->
                logger.LogInformation("Job {JobId} already triggered by another instance", job.Id)
            | ex ->
                logger.LogError(ex, "Error triggering job {JobId}", job.Id)
        }

    member this.GetDueJobs(currentTime: DateTimeOffset) : (Job * uint64) list =
        let dueJobs = synchroniser.GetDueJobs(currentTime)
        logger.LogInformation("Found {DueCount} due jobs out of {TotalCount} total jobs", dueJobs.Length, synchroniser.GetMetrics().TotalJobs)
        dueJobs 