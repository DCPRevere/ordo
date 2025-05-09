module Ordo.Core.Events

open System
open System.Text.Json
open EventStore.Client
open System.Text.RegularExpressions
open Ordo.Core.Json
open Ordo.Core.Model

type Schema = {
    Type: string
    Version: string
} with
    member this.toString = $"urn:schema:{this.Type}:{this.Version}"
    static member fromString (schema: string) : Schema option =
        if schema.StartsWith("$") then
            Some { Type = "system"; Version = "1.0" }
        else
            let regex = Regex.Match(schema, @"urn:schema:([^:]+):([^:]+)")
            if regex.Success then
                Some { Type = regex.Groups[1].Value; Version = regex.Groups[2].Value }
            else
                None

type Metadata = {
    CorrelationId: string
    CausationId: string option
    UserId: string option
    Timestamp: DateTimeOffset option
}

type IEvent =
    abstract member Schema: Schema
    abstract member Metadata: Metadata

type JobScheduledV2 = { 
    Metadata: Metadata
    Id: string
    Schedule: Schedule
    Payload: string 
    } with 
      static member Schema = { Type = "ordo.job.scheduled"; Version = "2.0" }
      interface IEvent with
          member _.Schema = JobScheduledV2.Schema
          member this.Metadata = this.Metadata

type JobTriggeredV2 = { 
    Metadata: Metadata
    Id: string
    TriggerTime: DateTimeOffset
    Timestamp: DateTimeOffset 
    } with
        static member Schema = { Type = "ordo.job.triggered"; Version = "2.0" }
        interface IEvent with
            member _.Schema = JobTriggeredV2.Schema
            member this.Metadata = this.Metadata

type JobExecutedV2 =
    { Metadata: Metadata
      Id: string
      ExecutionTime: DateTimeOffset
      ResultData: string
      Timestamp: DateTimeOffset }
      with
      static member Schema = { Type = "ordo.job.executed"; Version = "2.0" }
      interface IEvent with
          member _.Schema = JobExecutedV2.Schema
          member this.Metadata = this.Metadata

type JobCancelledV2 =
    { Metadata: Metadata
      Id: string
      Timestamp: DateTimeOffset }
      with
      static member Schema = { Type = "ordo.job.cancelled"; Version = "2.0" }
      interface IEvent with
          member _.Schema = JobCancelledV2.Schema
          member this.Metadata = this.Metadata

type JobRescheduledV2 =
    { Metadata: Metadata
      Id: string
      NewScheduledTime: DateTimeOffset
      Timestamp: DateTimeOffset }
      with
      static member Schema = { Type = "ordo.job.rescheduled"; Version = "2.0" }
      interface IEvent with
          member _.Schema = JobRescheduledV2.Schema
          member this.Metadata = this.Metadata

type JobEvent =
   | EventScheduled of JobScheduledV2
   | EventTriggered of JobTriggeredV2
   | EventExecuted of JobExecutedV2
   | EventCancelled of JobCancelledV2
   | EventRescheduled of JobRescheduledV2

let getJobId (event: JobEvent) : string =
    match event with
    | EventScheduled e -> e.Id
    | EventTriggered e -> e.Id
    | EventExecuted e -> e.Id
    | EventCancelled e -> e.Id
    | EventRescheduled e -> e.Id

let private tryDeserialize<'T> (data: ReadOnlyMemory<byte>) : 'T option =
    try 
        let json = System.Text.Encoding.UTF8.GetString data.Span
        tryDeserialise<'T> json
    with _ -> None

type Resolver = ResolvedEvent -> JobEvent option

let resolve : Resolver = fun evt ->
    let data = System.Text.Encoding.UTF8.GetString evt.Event.Data.Span
    match Schema.fromString evt.Event.EventType with
    | None -> None  // Skip events with invalid schema
    | Some schema when schema.Type = "system" -> None  // Skip system events
    | Some schema when schema = JobScheduledV2.Schema -> 
        tryDeserialise<JobScheduledV2> data |> Option.map EventScheduled
    | Some schema when schema = JobTriggeredV2.Schema -> 
        tryDeserialise<JobTriggeredV2> data |> Option.map EventTriggered
    | Some schema when schema = JobExecutedV2.Schema -> 
        tryDeserialise<JobExecutedV2> data |> Option.map EventExecuted
    | Some schema when schema = JobCancelledV2.Schema -> 
        tryDeserialise<JobCancelledV2> data |> Option.map EventCancelled
    | Some schema when schema = JobRescheduledV2.Schema ->
        tryDeserialise<JobRescheduledV2> data |> Option.map EventRescheduled
    | Some _ -> None  // Skip unknown event types instead of failing