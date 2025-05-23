namespace Ordo.Core.Model

open System

type JobStatus =
    | StatusUnknown
    | StatusScheduled
    | StatusTriggered
    | StatusExecuted
    | StatusCancelled

type ConfiguredScheduleType = 
    | ExpireEori

type ConfiguredSchedule = {
    Type: ConfiguredScheduleType
    From: DateTimeOffset
}

type Schedule =
    | Immediate
    | Precise of DateTimeOffset
    | Configured of ConfiguredSchedule

type JobTypeDelayConfig = {
    DefaultDelay: string
}

type Job =
    { Id: string
      Status: JobStatus
      Schedule: Schedule
      ScheduledTime: DateTimeOffset option
      Payload: string option
      LastUpdated: DateTimeOffset
      TriggerTime: DateTimeOffset option
      ExecutionTime: DateTimeOffset option
      ResultData: string option
      FailureMessage: string option
      CancellationReason: string option
    }
