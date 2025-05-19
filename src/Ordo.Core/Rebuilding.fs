module Ordo.Core.Rebuilding

open System
open Ordo.Core.Events
open EventStore.Client
open Ordo.Core.Model
open System.Text.Json
open Ordo.Core
open System.Threading.Tasks

let calculateScheduledTime (sch: Schedule) (ts: DateTimeOffset option) : Task<DateTimeOffset> =
    task {
        match sch with
        | Immediate -> return (ts |> Option.defaultValue DateTimeOffset.MinValue)
        | Precise time -> return time
        | Configured configuredSch ->
            return configuredSch.From.Add(TimeSpan.FromSeconds(10.0))
    }

let initialState (evt: JobScheduledV2) : Job =
    { Id = evt.Id
      Status = JobStatus.StatusScheduled
      Schedule = evt.Schedule
      ScheduledTime = None
      Payload = Some evt.Payload
      LastUpdated = evt.Metadata.Timestamp |> Option.defaultValue DateTimeOffset.MinValue
      TriggerTime = None
      ExecutionTime = None
      ResultData = None
      FailureMessage = None
      CancellationReason = None
    }

let applyEvent (currentState: Job) (event: JobEvent) : Job =
    match event with
    | EventScheduled evt ->
        if currentState.Status = JobStatus.StatusUnknown && currentState.Id = evt.Id then
            let schedule = 
                match evt.Schedule with
                | Schedule.Immediate -> Immediate
                | Schedule.Precise time -> Precise time
                | Schedule.Configured config -> Configured { Type = config.Type; From = config.From }
            { currentState with
                Id = evt.Id
                Status = JobStatus.StatusScheduled
                Schedule = schedule
                Payload = Some evt.Payload
                LastUpdated = evt.Metadata.Timestamp |> Option.defaultValue currentState.LastUpdated }
        else
            currentState

    | EventTriggered evt ->
        if currentState.Status = JobStatus.StatusScheduled && currentState.Id = evt.Id then
             { currentState with
                 Status = JobStatus.StatusTriggered
                 TriggerTime = Some evt.TriggerTime
                 LastUpdated = evt.Metadata.Timestamp |> Option.defaultValue currentState.LastUpdated }
        else
            currentState

    | EventExecuted evt ->
        if currentState.Status = JobStatus.StatusTriggered && currentState.Id = evt.Id then
            { currentState with
                Status = JobStatus.StatusExecuted
                ExecutionTime = Some evt.ExecutionTime
                ResultData = Some evt.ResultData
                LastUpdated = evt.Metadata.Timestamp |> Option.defaultValue currentState.LastUpdated }
        else
            currentState

    | EventCancelled evt ->
        if (currentState.Status = JobStatus.StatusScheduled || currentState.Status = JobStatus.StatusTriggered) && currentState.Id = evt.Id then
            { currentState with
                Status = JobStatus.StatusCancelled
                LastUpdated = evt.Metadata.Timestamp |> Option.defaultValue currentState.LastUpdated }
        else
            currentState

    | EventRescheduled evt ->
        if currentState.Status = JobStatus.StatusScheduled && currentState.Id = evt.Id then
            { currentState with
                Schedule = Precise evt.NewScheduledTime
                LastUpdated = evt.Timestamp }
        else
            currentState

let reconstructState (jobId: Guid) (resolvedEvents: seq<ResolvedEvent>) : Job =
    let events = resolvedEvents |> Seq.choose resolve |> Seq.toList
    match events with
    | [] -> failwith "No events found for job"
    | EventScheduled evt :: rest ->
        let startState = initialState evt
        rest |> Seq.fold applyEvent startState
    | _ -> failwith "First event must be JobScheduled"
