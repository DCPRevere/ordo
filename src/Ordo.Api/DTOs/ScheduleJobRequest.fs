namespace Ordo.Api.DTOs

open System
open Ordo.Core.Job
open Ordo.Core.Model

type ScheduleJobRequest =
    {
      Schedule: Schedule
      Payload: string
    } 