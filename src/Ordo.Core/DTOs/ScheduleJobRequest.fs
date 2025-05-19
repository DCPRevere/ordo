namespace Ordo.Core.DTOs

open Ordo.Core.Model

type ScheduleJobRequest =
    {
      Schedule: Schedule
      Payload: string
    } 