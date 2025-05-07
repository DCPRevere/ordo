namespace Ordo.Api.Controllers

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Ordo.Core.Events
open Ordo.Core.EventStore
open Ordo.Api.DTOs
open Ordo.Synchroniser
open Ordo.Core.JobState

[<ApiController>]
[<Route("api/jobs")>]
type JobsController(synchroniser: ProjectionSynchroniser, logger: ILogger<JobsController>) =
    inherit ControllerBase()

    // POST /api/jobs
    [<HttpPost>]
    member this.ScheduleJob([<FromBody>] request: ScheduleJobRequest) : Task<IResult> =
        task {
            let jobId = Guid.NewGuid()
            let now = DateTimeOffset.UtcNow

            logger.LogInformation("Scheduling new job {JobId} for {ScheduledTime} with payload: {Payload}", 
                jobId, request.ScheduledTime, request.Payload)

            let scheduledData: JobScheduled = 
                { JobId = jobId
                  ScheduledTime = request.ScheduledTime
                  Payload = request.Payload
                  Timestamp = now }
            let jobScheduledEvent: JobEvent = EventScheduled scheduledData

            do! scheduleJob scheduledData

            let responseDto = { JobId = jobId }
            let routeValues = {| jobId = jobId |}

            logger.LogInformation("Job {JobId} scheduled successfully. Will execute at {ScheduledTime}", 
                jobId, request.ScheduledTime)

            return Results.AcceptedAtRoute("GetJobStatus", routeValues, responseDto)
        }

    // GET /api/jobs/{jobId}
    [<HttpGet("{jobId}", Name = "GetJobStatus")>]
    member this.GetJobStatus(jobId: Guid) : Task<ActionResult<Job>> =
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
    member this.CancelJob(jobId: Guid) : Task<ActionResult> =
        task {
            logger.LogInformation("Attempting to cancel job {JobId}", jobId)
            match synchroniser.GetJobState(jobId) with
            | Some (job, version) ->
                match job.Status with
                | JobStatus.StatusScheduled | JobStatus.StatusTriggered ->
                    let now = DateTimeOffset.UtcNow
                    let cancelData = {
                        JobId = jobId
                        Reason = "Cancelled by user request"
                        Timestamp = now
                    }
                    do! cancelJob cancelData
                    logger.LogInformation("Job {JobId} cancelled successfully", jobId)
                    return this.Ok() :> ActionResult
                | _ ->
                    logger.LogWarning("Cannot cancel job {JobId} in status {Status}", jobId, job.Status)
                    return this.BadRequest("Job cannot be cancelled in its current state") :> ActionResult
            | None ->
                logger.LogWarning("Attempted to cancel non-existent job {JobId}", jobId)
                return this.NotFound() :> ActionResult
        }

    // GET /api/jobs/metrics
    [<HttpGet("metrics")>]
    member this.GetSystemMetrics() : ActionResult<SystemMetricsResponse> =
        logger.LogInformation("Retrieving system metrics")
        let metrics = synchroniser.GetMetrics()
        let activeJobs = synchroniser.GetDueJobs(DateTimeOffset.UtcNow).Length |> int64

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