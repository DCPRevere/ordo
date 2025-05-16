module Ordo.Core.Rebuilding

open System
open Ordo.Core.Events
open EventStore.Client
open Ordo.Core.Model
open System.Text.Json
open Ordo.Core
open System.Threading.Tasks

let calculateScheduledTime (sch: Schedule) (ts: DateTimeOffset option) (jtcs: JobTypeConfigService) : Task<DateTimeOffset> =
    task {
        match sch with
        | Immediate -> return (ts |> Option.defaultValue DateTimeOffset.MinValue)
        | Precise time -> return time
        | Configured configuredSch ->
            let! configOpt = jtcs.GetConfig(configuredSch.Type)
            match configOpt with
            | Some jobTypeConfig ->
                match TimeSpan.TryParse(jobTypeConfig.DefaultDelay) with
                | true, delayTimeSpan ->
                    return configuredSch.From.Add(delayTimeSpan)
                | false, _ ->
                    return (ts |> Option.defaultValue DateTimeOffset.MinValue)
            | None ->
                return (ts |> Option.defaultValue DateTimeOffset.MinValue)
    }


let initialState (evt: JobScheduledV2) (jtcs: JobTypeConfigService) : Job =
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
                | _ -> failwith $"Unknown job type: {evt.Schedule}"
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

let reconstructState (jobId: Guid) (resolvedEvents: seq<ResolvedEvent>) (jtcs: JobTypeConfigService) : Job =
    let events = resolvedEvents |> Seq.choose resolve |> Seq.toList
    match events with
    | [] -> failwith "No events found for job"
    | EventScheduled evt :: rest ->
        let startState = initialState evt jtcs
        rest |> Seq.fold applyEvent startState
    | _ -> failwith "First event must be JobScheduled"
