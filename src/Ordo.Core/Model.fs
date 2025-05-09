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