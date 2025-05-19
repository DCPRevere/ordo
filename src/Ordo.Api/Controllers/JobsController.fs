namespace Ordo.Api.Controllers

open EventStore.Client
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Ordo.Core.DTOs
open Ordo.Core.EventStore
open Ordo.Core.Events
open Ordo.Core.Rebuilding
open Ordo.Core.Model
open Ordo.Synchroniser
open System
open System.Threading.Tasks

type JobStatusResponse = {
    Jobs: Job list
    Status: JobStatusCounts
    TotalJobs: int64
    LastUpdated: DateTimeOffset
}

type RescheduleJobRequest = {
    ScheduleType: string
    ScheduledTime: DateTimeOffset option
    JobType: string option
    Reason: string
}

[<ApiController>]
[<Route("api/jobs")>]
type JobsController(
    synchroniser: ProjectionSynchroniser, 
    config: IConfiguration,
    logger: ILogger<JobsController>) =
    inherit ControllerBase()

    // POST /api/jobs
    [<HttpPost>]
    member this.ScheduleJob([<FromBody>] request: ScheduleJobRequest) : Task<IResult> =
        task {
            logger.LogInformation("Received schedule job request: {Request}", Ordo.Core.Json.serialise request)

            match this.ModelState.IsValid with
            | false ->
                let errors = 
                    this.ModelState.Values
                    |> Seq.collect (fun v -> v.Errors)
                    |> Seq.map (fun e -> e.ErrorMessage)
                    |> Seq.toList
                logger.LogWarning("Invalid model state: {Errors}", Ordo.Core.Json.serialise errors)
                return Results.BadRequest(errors)
            | true ->
                let jobId = Guid.NewGuid().ToString()
                let now = DateTimeOffset.UtcNow

                let calculateScheduledTime () =
                    match request.Schedule with
                    | Immediate -> Ok now
                    | Precise time -> 
                        Ok time
                    | Configured c ->
                        let configSection = config.GetSection($"JobTypes:{c.Type}")
                        if configSection.Exists() then
                            let delaySeconds = configSection.GetValue<int>("DelaySeconds")
                            Ok (now.AddSeconds(float delaySeconds))
                        else
                            logger.LogWarning("No configuration found for job type {JobType}, using default 1 hour delay", request.Schedule.ToString())
                            Ok (now.AddHours(1.0))

                match calculateScheduledTime() with
                | Error msg ->
                    logger.LogWarning("Failed to calculate scheduled time: {Message}", msg)
                    return Results.BadRequest(msg)
                | Ok scheduledTime ->
                    logger.LogInformation("Scheduling new job {JobId} of type {JobType} for {ScheduledTime} with payload: {Payload}", 
                        jobId, request.Schedule.ToString(), scheduledTime, request.Payload)

                    let scheduledData: JobScheduledV2 = 
                        { Metadata = { CorrelationId = jobId; CausationId = None; UserId = None; Timestamp = None }
                          Id = jobId
                          Schedule = request.Schedule
                          Payload = request.Payload }

                    let eventData = EventData(
                        Uuid.NewUuid(),
                        JobScheduledV2.Schema.toString,
                        System.Text.Encoding.UTF8.GetBytes(Ordo.Core.Json.serialise scheduledData)
                    )

                    do! scheduleJob scheduledData

                    let responseDto = { Id = jobId }
                    let routeValues = {| jobId = jobId |}

                    logger.LogInformation("Job {JobId} scheduled successfully. Will execute at {ScheduledTime}", 
                        jobId, scheduledTime)

                    return Results.AcceptedAtRoute("GetJobStatus", routeValues, responseDto)
        }

    // GET /api/jobs/{jobId}
    [<HttpGet("{jobId}", Name = "GetJobStatus")>]
    member this.GetJobStatus(jobId: string) : Task<ActionResult<Job>> =
        task {
            logger.LogInformation("Checking status for job {JobId}", jobId)
            match synchroniser.GetJobState(jobId) with
            | Some (job, _) -> 
                logger.LogInformation("Found job {JobId} with status {Status}", jobId, job.Status)
                return ActionResult<Job>(job)
            | None -> 
                logger.LogWarning("Job {JobId} not found", jobId)
                return ActionResult<Job>(this.NotFound())
        }

    // DELETE /api/jobs/{jobId}
    [<HttpDelete("{jobId}")>]
    member this.CancelJob(jobId: string) : Task<ActionResult> =
        task {
            logger.LogInformation("Attempting to cancel job {JobId}", jobId)
            match synchroniser.GetJobState(jobId) with
            | Some (job, _) ->
                match job.Status with
                | JobStatus.StatusScheduled | JobStatus.StatusTriggered ->
                    let now = DateTimeOffset.UtcNow
                    let cancelData: JobCancelledV2 = {
                        Metadata = { CorrelationId = jobId; CausationId = None; UserId = None; Timestamp = None }
                        Id = jobId
                        Timestamp = now
                    }
                    do! Ordo.Core.EventStore.cancelJob cancelData
                    logger.LogInformation("Job {JobId} cancelled successfully", jobId)
                    return this.Ok() :> ActionResult
                | _ ->
                    logger.LogWarning("Cannot cancel job {JobId} in status {Status}", jobId, job.Status)
                    return this.BadRequest("Job cannot be cancelled in its current state") :> ActionResult
            | None ->
                logger.LogWarning("Attempted to cancel non-existent job {JobId}", jobId)
                return this.NotFound() :> ActionResult
        }

    // GET /api/jobs/types/config
    [<HttpGet("types/config")>]
    member this.GetAllJobTypeConfigs() : ActionResult<Map<string, JobTypeDelayConfig>> =
        logger.LogInformation("Retrieving all job type configurations")
        let configs = 
            config.GetSection("JobTypes").GetChildren()
            |> Seq.map (fun section ->
                let delaySeconds = section.GetValue<int>("DelaySeconds")
                section.Key, { 
                    DefaultDelay = TimeSpan.FromSeconds(float delaySeconds).ToString("hh\:mm\:ss")
                    MaxRetries = 3
                    RetryDelayMultiplier = 2.0 
                })
            |> Map.ofSeq
        logger.LogInformation("Returning {Count} job type configurations", configs.Count)
        ActionResult<Map<string, JobTypeDelayConfig>>(configs)

    // GET /api/jobs/types/{jobType}/config
    [<HttpGet("types/{jobType}/config")>]
    member this.GetJobTypeConfig(jobType: string) : ActionResult<JobTypeDelayConfig> =
        logger.LogInformation("Retrieving configuration for job type {JobType}", jobType)
        let configSection = config.GetSection($"JobTypes:{jobType}")
        if configSection.Exists() then
            let delaySeconds = configSection.GetValue<int>("DelaySeconds")
            let config = { 
                DefaultDelay = TimeSpan.FromSeconds(float delaySeconds).ToString("hh\:mm\:ss")
                MaxRetries = 3
                RetryDelayMultiplier = 2.0 
            }
            logger.LogInformation("Found configuration for job type {JobType}: {Config}", jobType, config)
            ActionResult<JobTypeDelayConfig>(config)
        else
            logger.LogWarning("No configuration found for job type {JobType}", jobType)
            ActionResult<JobTypeDelayConfig>(this.NotFound())

    // PUT /api/jobs/types/{jobType}/config
    [<HttpPut("types/{jobType}/config")>]
    member this.UpdateJobTypeConfig(jobType: string, [<FromBody>] config: JobTypeDelayConfig) : ActionResult =
        logger.LogInformation("Updating configuration for job type {JobType}: {Config}", jobType, config)
        // TODO: Save to configuration store
        this.Ok() :> ActionResult

    // GET /api/jobs/metrics
    [<HttpGet("metrics")>]
    member this.GetSystemMetrics() : ActionResult<SystemMetricsResponse> =
        logger.LogInformation("Retrieving system metrics")
        let metrics = synchroniser.GetMetrics()
        let _, status = synchroniser.GetStatus(DateTimeOffset.UtcNow)
        let activeJobs = status.DueScheduled + status.Triggered |> int64

        let healthStatus =
            match metrics.LastSubscriptionDrop with
            | Some lastDrop when lastDrop > DateTimeOffset.UtcNow.AddMinutes(-5.0) -> "Degraded"
            | _ when metrics.ProcessingErrors > 0L -> "Warning"
            | _ -> "Healthy"

        let response = {
            TotalJobs = metrics.TotalJobs
            ActiveJobs = activeJobs
            EventsProcessed = metrics.EventsProcessed
            LastEventProcessedAt = if metrics.LastEventProcessed = DateTimeOffset.MinValue then None else Some metrics.LastEventProcessed
            SubscriptionDrops = metrics.SubscriptionDrops
            LastSubscriptionDropAt = metrics.LastSubscriptionDrop
            ProcessingErrors = metrics.ProcessingErrors
            HealthStatus = healthStatus
        }

        logger.LogInformation("System metrics: {TotalJobs} total jobs, {ActiveJobs} active jobs, health: {HealthStatus}", 
            metrics.TotalJobs, activeJobs, healthStatus)

        ActionResult<SystemMetricsResponse>(response)

    // GET /api/jobs/all
    [<HttpGet("all")>]
    member this.GetAllJobs() : Task<ActionResult<JobStatusResponse>> =
        task {
            logger.LogInformation("Retrieving all jobs")
            let now = DateTimeOffset.UtcNow
            let _, status = synchroniser.GetStatus(now)
            let jobs = synchroniser.GetAllJobs() |> List.map fst

            let response = {
                Jobs = jobs
                Status = status
                TotalJobs = int64 jobs.Length
                LastUpdated = now
            }

            logger.LogInformation("Returning {Count} jobs with status: {FutureScheduled} future, {DueScheduled} due, {Triggered} triggered, {Executed} executed, {Cancelled} cancelled", 
                jobs.Length, 
                status.FutureScheduled, 
                status.DueScheduled, 
                status.Triggered, 
                status.Executed, 
                status.Cancelled)
            return ActionResult<JobStatusResponse>(response)
        }

    // PUT /api/jobs/{jobId}/reschedule
    [<HttpPut("{jobId}/reschedule")>]
    member this.RescheduleJob(jobId: string, [<FromBody>] request: RescheduleJobRequest) : Task<ActionResult> =
        task {
            logger.LogInformation("Attempting to reschedule job {JobId}", jobId)
            match synchroniser.GetJobState(jobId) with
            | Some (job, _) ->
                match job.Status with
                | JobStatus.StatusScheduled ->
                    let now = DateTimeOffset.UtcNow
                    let calculateScheduledTime () =
                        match request.ScheduleType.ToLower() with
                        | "immediate" -> Ok now
                        | "precise" -> 
                            match request.ScheduledTime with
                            | Some time -> Ok time
                            | None -> Error "Precise schedule type requires ScheduledTime"
                        | "configured" ->
                            match request.JobType with
                            | Some jobType ->
                                let configSection = config.GetSection($"JobTypes:{jobType}")
                                if configSection.Exists() then
                                    let delaySeconds = configSection.GetValue<int>("DelaySeconds")
                                    Ok (now.AddSeconds(float delaySeconds))
                                else
                                    logger.LogWarning("No configuration found for job type {JobType}, using default 1 hour delay", jobType)
                                    Ok (now.AddHours(1.0))
                            | None -> Error "Configured schedule type requires JobType"
                        | _ -> Error "Invalid schedule type"

                    match calculateScheduledTime() with
                    | Error msg ->
                        logger.LogWarning("Failed to calculate scheduled time: {Message}", msg)
                        return this.BadRequest(msg) :> ActionResult
                    | Ok scheduledTime ->
                        let rescheduleData: JobRescheduledV2 = {
                            Metadata = { CorrelationId = jobId; CausationId = None; UserId = None; Timestamp = None }
                            Id = jobId
                            NewScheduledTime = scheduledTime
                            Timestamp = now
                        }
                        do! Ordo.Core.EventStore.rescheduleJob rescheduleData
                        logger.LogInformation("Job {JobId} rescheduled successfully to {NewTime}", jobId, scheduledTime)
                        return this.Ok() :> ActionResult
                | _ ->
                    logger.LogWarning("Cannot reschedule job {JobId} in status {Status}", jobId, job.Status)
                    return this.BadRequest("Job can only be rescheduled when in Scheduled status") :> ActionResult
            | None ->
                logger.LogWarning("Attempted to reschedule non-existent job {JobId}", jobId)
                return this.NotFound() :> ActionResult
        }