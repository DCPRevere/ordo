module Ordo.Core.Events

open System
open System.Text.Json
open EventStore.Client


type JobScheduled =
    { JobId: Guid
      ScheduledTime: DateTimeOffset
      Payload: string
      Timestamp: DateTimeOffset }

type JobTriggered =
    { JobId: Guid
      TriggerTime: DateTimeOffset
      Timestamp: DateTimeOffset }

type JobExecuted =
    { JobId: Guid
      ExecutionTime: DateTimeOffset
      ResultData: string
      Timestamp: DateTimeOffset }

type JobCancelled =
    { JobId: Guid
      Reason: string
      Timestamp: DateTimeOffset }

type JobFailed =
    { JobId: Guid
      FailureTime: DateTimeOffset
      ErrorMessage: string
      ExceptionDetails: string option
      Timestamp: DateTimeOffset }

type JobEvent =
   | EventScheduled of JobScheduled
   | EventTriggered of JobTriggered
   | EventExecuted of JobExecuted
   | EventCancelled of JobCancelled
   | EventFailed of JobFailed

let getJobId (event: JobEvent) : Guid =
    match event with
    | EventScheduled e -> e.JobId
    | EventTriggered e -> e.JobId
    | EventExecuted e -> e.JobId
    | EventCancelled e -> e.JobId
    | EventFailed e -> e.JobId

let private tryDeserialize<'T> (data: ReadOnlyMemory<byte>) : 'T option =
    try Some (JsonSerializer.Deserialize<'T>(data.Span))
    with _ -> None

let tryParseJobEvent (resolvedEvent: ResolvedEvent) : JobEvent option =
    match resolvedEvent.Event.EventType with
    | "JobScheduled" -> tryDeserialize<JobScheduled> resolvedEvent.Event.Data |> Option.map EventScheduled
    | "JobTriggered" -> tryDeserialize<JobTriggered> resolvedEvent.Event.Data |> Option.map EventTriggered
    | "JobExecuted" -> tryDeserialize<JobExecuted> resolvedEvent.Event.Data |> Option.map EventExecuted
    | "JobCancelled" -> tryDeserialize<JobCancelled> resolvedEvent.Event.Data |> Option.map EventCancelled
    | "JobFailed" -> tryDeserialize<JobFailed> resolvedEvent.Event.Data |> Option.map EventFailed
    | _ -> None // Ignore unknown event types 