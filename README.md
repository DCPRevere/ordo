# Ordo: time-based job processing

## Quick start

Each component can be run separately, with multiple instances of each, or using Ordo.Host you can run all components in a single application:

```bash
dotnet run --project src/Ordo.Host/Ordo.Host.fsproj --all
```

### Scheduling Jobs

Ordo supports three types of job schedules:

1. **Immediate** - Runs as soon as possible:
```bash
curl -k -X POST https://localhost:5001/api/jobs \
  -H "Content-Type: application/json" \
  -d '{
    "schedule": {
      "Case": "Immediate"
    },
    "payload": "your-payload-here"
  }'
```

2. **Precise** - Runs at a specific time:
```bash
curl -k -X POST https://localhost:5001/api/jobs \
  -H "Content-Type: application/json" \
  -d '{
    "schedule": {
      "Case": "Precise",
      "Fields": ["2025-05-08T15:05:00Z"]
    },
    "payload": "your-payload-here"
  }'
```

3. **Configured** - Runs based on job type configuration:
```bash
curl -k -X POST https://localhost:5001/api/jobs \
  -H "Content-Type: application/json" \
  -d '{
    "schedule": {
      "Case": "Configured",
      "Fields": [{
        "Type": {
          "Case": "ExpireEori"
        },
        "From": "2025-05-08T15:05:00Z"
      }]
    },
    "payload": "your-payload-here"
  }'
```

Response for all schedule types:
```json
{
  "id": "819a1868-3824-4a6f-b408-316c807ee661"
}
```

Note: The `-k` flag is required when using HTTPS with self-signed certificates in development.

### Job Statuses

Jobs progress through the following states:
- `StatusScheduled`: Initial state when job is created
- `StatusTriggered`: Job has been picked up for execution
- `StatusExecuted`: Job completed successfully
- `StatusCancelled`: Job was cancelled before execution
- `StatusUnknown`: Job state could not be determined

You can monitor job status through the logs:
```
[10:06:54] Scheduling new job 819a1868-3824-4a6f-b408-316c807ee661 for 05/07/2025 09:07:00 +00:00
[10:07:00] Triggering job 819a1868-3824-4a6f-b408-316c807ee661
[10:07:00] Processing job 819a1868-3824-4a6f-b408-316c807ee661
[10:07:00] Successfully executed job 819a1868-3824-4a6f-b408-316c807ee661
```

### Job Type Configuration

Configured jobs use job type-specific settings. For example, the `ExpireEori` job type has:
- Default delay: 10 seconds
- Max retries: 3
- Retry delay multiplier: 2.0

You can view and update job type configurations:
```bash
# Get configuration for a job type
curl -k -X GET https://localhost:5001/api/jobs/types/ExpireEori/config

# Update configuration
curl -k -X PUT https://localhost:5001/api/jobs/types/ExpireEori/config \
  -H "Content-Type: application/json" \
  -d '{
    "defaultDelay": "00:00:10",
    "maxRetries": 3,
    "retryDelayMultiplier": 2.0
  }'
```

## Overview

Ordo is a distributed job scheduling system built with F# and .NET 9. It uses event sourcing with KurrentDB to provide reliable, scalable job processing with strong consistency guarantees.

Key features:
- Schedule jobs to run immediately, at specific times, or based on configuration
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