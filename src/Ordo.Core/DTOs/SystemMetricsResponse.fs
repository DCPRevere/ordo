namespace Ordo.Core.DTOs

open System

type SystemMetricsResponse = {
    TotalJobs: int64
    ActiveJobs: int64
    EventsProcessed: int64
    LastEventProcessedAt: DateTimeOffset option
    SubscriptionDrops: int64
    LastSubscriptionDropAt: DateTimeOffset option
    ProcessingErrors: int64
    HealthStatus: string
} 