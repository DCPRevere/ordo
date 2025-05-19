namespace Ordo.Timekeeper

open EventStore.Client
open Microsoft.Extensions.Logging
open Ordo.Core.DTOs
open Ordo.Core.Events
open Ordo.Core.Rebuilding
open Ordo.Core.Model
open Ordo.Synchroniser
open Serilog
open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks

type TimekeeperConfig = {
    EventStoreConnectionString: string
    CheckInterval: TimeSpan
    ApiBaseUrl: string
}

type Timekeeper(esClient: EventStoreClient, loggerFactory: ILoggerFactory, config: TimekeeperConfig, configService: JobTypeConfigService) =
    let mutable isRunning = false
    let mutable cancellationTokenSource = new CancellationTokenSource()
    let synchroniser = ProjectionSynchroniser(esClient, loggerFactory.CreateLogger<ProjectionSynchroniser>(), configService)
    let httpClient = new HttpClient(BaseAddress = Uri(config.ApiBaseUrl))
    let logger = Log.ForContext<Timekeeper>()

    member this.Start() =
        if isRunning then
            logger.Warning("Timekeeper is already running")
            Task.CompletedTask
        else
            isRunning <- true
            cancellationTokenSource <- new CancellationTokenSource()
            task {
                do! synchroniser.Start(cancellationTokenSource.Token)
                let now = DateTimeOffset.UtcNow
                let! (_, status) = synchroniser.GetStatus(now)
                logger.Information("Timekeeper starting with job status: {ImmediateScheduled} immediate, {DueScheduled} due, {FutureScheduled} future, {Triggered} triggered, {Executed} executed, {Cancelled} cancelled, {Failed} failed, {Unknown} unknown", 
                    status.ImmediateScheduled, status.DueScheduled, status.FutureScheduled, 
                    status.Triggered, status.Executed, 
                    status.Cancelled, status.Failed, status.Unknown)
                do! this.RunAsync(cancellationTokenSource.Token)
            }

    member this.Stop() =
        if not isRunning then
            logger.Warning("Timekeeper is not running")
            Task.CompletedTask
        else
            isRunning <- false
            task {
                do! synchroniser.Stop(cancellationTokenSource.Token)
                cancellationTokenSource.Cancel()
                cancellationTokenSource.Dispose()
                httpClient.Dispose()
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
                        logger.Information("Timekeeper operation canceled")
                        return ()
                    | ex ->
                        logger.Error(ex, "Error in Timekeeper main loop")
                        do! Task.Delay(TimeSpan.FromSeconds(1), ct)
            with
            | :? OperationCanceledException ->
                logger.Information("Timekeeper stopped")
            | ex ->
                logger.Error(ex, "Error in Timekeeper main loop")
        }

    member private this.CheckAndTriggerJobsAsync() =
        task {
            try
                let now = DateTimeOffset.UtcNow
                let! (dueJobs, status) = synchroniser.GetStatus(now)
                logger.Information("Job status: {ImmediateScheduled} immediate, {DueScheduled} due, {FutureScheduled} future, {Triggered} triggered, {Executed} executed, {Cancelled} cancelled, {Failed} failed, {Unknown} unknown", 
                    status.ImmediateScheduled, status.DueScheduled, status.FutureScheduled, 
                    status.Triggered, status.Executed, 
                    status.Cancelled, status.Failed, status.Unknown)
                for (job, version) in dueJobs do
                    do! this.TriggerJobAsync(job, version)
            with ex ->
                logger.Error(ex, "Error checking and triggering jobs")
        }

    member private this.GetScheduledTime(job: Job) : Task<DateTimeOffset> =
        task {
            match job.Schedule with
            | Schedule.Immediate -> return DateTimeOffset.UtcNow
            | Schedule.Precise time -> return time
            | Schedule.Configured config ->
        }

    member private this.TriggerJobAsync(job: Job, version: uint64) =
        task {
            try
                let now = DateTimeOffset.UtcNow
                let! scheduledTime = this.GetScheduledTime(job)
                
                logger.Information("Triggering job {JobId} (scheduled for {ScheduledTime})", job.Id, scheduledTime)
                let event = {
                    Metadata = { CorrelationId = job.Id; CausationId = None; UserId = None; Timestamp = None }
                    Id = job.Id
                    TriggerTime = now
                    Timestamp = now
                }
                let jsonData = System.Text.Encoding.UTF8.GetBytes(Ordo.Core.Json.serialise event)
                let eventData = EventData(
                    Uuid.NewUuid(),
                    JobTriggeredV2.Schema.toString,
                    jsonData
                )
                let! result = esClient.AppendToStreamAsync(
                    $"ordo-job-{job.Id}",
                    StreamRevision version,
                    [| eventData |]
                )
                logger.Information("Successfully triggered job {JobId}", job.Id)
            with
            | :? WrongExpectedVersionException ->
                logger.Information("Job {JobId} already triggered by another instance", job.Id)
            | ex ->
                logger.Error(ex, "Error triggering job {JobId}", job.Id)
        }

    member this.GetDueJobs(currentTime: DateTimeOffset) : Task<(Job * uint64) list> =
        task {
            let! (dueJobs, status) = synchroniser.GetStatus(currentTime)
            logger.Information("Found {DueCount} due jobs out of {TotalCount} total jobs", dueJobs.Length, synchroniser.GetMetrics().TotalJobs)
            return dueJobs
        } 