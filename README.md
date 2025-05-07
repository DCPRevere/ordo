# Ordo: time-based job processing

## Quick start

Each component can be run separately, with multiple instances of each, or using Ordo.Host you can run all components in a single application:

```bash
dotnet run --project src/Ordo.Host/Ordo.Host.fsproj --all
```

Schedule a job to run at a specific time:

```bash
curl -X POST -H "Content-Type: application/json" \
  -d '{"scheduledTime": "2025-05-07T09:07:00Z", "payload": "test job"}' \
  https://localhost:5001/api/jobs
```

Response:
```json
{
  "jobId": "819a1868-3824-4a6f-b408-316c807ee661"
}
```

The job will be executed at the specified time. You can monitor its progress through the logs:

```
[10:06:54] Scheduling new job 819a1868-3824-4a6f-b408-316c807ee661 for 05/07/2025 09:07:00 +00:00
[10:07:00] Triggering job 819a1868-3824-4a6f-b408-316c807ee661
[10:07:00] Processing job 819a1868-3824-4a6f-b408-316c807ee661
[10:07:00] Successfully executed job 819a1868-3824-4a6f-b408-316c807ee661
```

## Overview

Ordo is a distributed job scheduling system built with F# and .NET 9. It uses event sourcing with KurrentDB to provide reliable, scalable job processing with strong consistency guarantees.

Key features:
- Schedule jobs to run at specific times
- Distributed execution across multiple nodes
- Event-sourced job history
- High availability and scalability
- No single point of failure

## Architecture

The system consists of four main components:

1. **Ordo.Core** - Shared domain logic and types
2. **Ordo.Api** - HTTP API for job scheduling
3. **Ordo.Timekeeper** - Triggers jobs at their scheduled times
4. **Ordo.Executor** - Performs the job when it is triggered

### Event flow

1. **Job Scheduling**
   - Client sends request to `Ordo.Api`
   - API writes `JobScheduled` event to job's stream (`ordo-job-<guid>`)
   - `Ordo.Timekeeper` picks up the event and updates its in-memory projection

2. **Job Triggering**
   - `Ordo.Timekeeper` maintains a projection of all scheduled jobs
   - On each tick, it checks for due jobs in its projection
   - When a job is due, it attempts to write a `JobTriggered` event
   - Uses optimistic concurrency to ensure exactly-once triggering
   - Multiple Timekeeper instances can run safely in parallel

3. **Job Execution**
   - `Ordo.Executor` instances compete for `JobTriggered` events
   - Each event is processed by exactly one Executor
   - Executor rebuilds job state from its event stream
   - Performs the job's work
   - Writes `JobExecuted` or `JobFailed` event
   - Acknowledges the trigger event to prevent reprocessing

4. **State Updates**
   - All components maintain their own projections
   - Events are the source of truth
   - State is always rebuilt from the event stream
   - Strong consistency through event sourcing

### Scalability

- **Api**: Single instance (stateless)
- **Timekeeper**: Multiple instances with optimistic concurrency
- **Executor**: Multiple instances for parallel processing