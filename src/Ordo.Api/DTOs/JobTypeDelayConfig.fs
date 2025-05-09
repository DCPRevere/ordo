namespace Ordo.Api.DTOs

open System

type JobTypeDelayConfig =
    { DefaultDelay: string  // TimeSpan as string (e.g., "01:00:00" for 1 hour)
      MaxRetries: int
      RetryDelayMultiplier: float
    } 