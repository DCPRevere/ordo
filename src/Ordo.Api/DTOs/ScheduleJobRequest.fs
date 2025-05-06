namespace Ordo.Api.DTOs

open System

type ScheduleJobRequest =
    { ScheduledTime: DateTimeOffset
      Payload: string
    } 