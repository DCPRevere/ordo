module Ordo.Core.JobState

open System
open Ordo.Core.Events
open EventStore.Client
open System.Text.Json

type JobStatus =
    | StatusUnknown
    | StatusScheduled
    | StatusTriggered
    | StatusExecuting
    | StatusExecuted
    | StatusCancelled
    | StatusFailed

type Job =
    { Id: Guid
      Status: JobStatus
      ScheduledTime: DateTimeOffset option
      Payload: string option
      LastUpdated: DateTimeOffset
      TriggerTime: DateTimeOffset option
      ExecutionTime: DateTimeOffset option
      ResultData: string option
      FailureMessage: string option
      CancellationReason: string option
    }

let initialState (jobId: Guid) : Job =
    { Id = jobId
      Status = JobStatus.StatusUnknown
      ScheduledTime = None
      Payload = None
      LastUpdated = DateTimeOffset.MinValue
      TriggerTime = None
      ExecutionTime = None
      ResultData = None
      FailureMessage = None
      CancellationReason = None
    }

let applyEvent (currentState: Job) (event: JobEvent) : Job =
    match event with
    | EventScheduled evt ->
        if currentState.Status = JobStatus.StatusUnknown && currentState.Id = evt.JobId then
            { currentState with
                Id = evt.JobId
                Status = JobStatus.StatusScheduled
                ScheduledTime = Some evt.ScheduledTime
                Payload = Some evt.Payload
                LastUpdated = evt.Timestamp }
        else
            currentState

    | EventTriggered evt ->
        if currentState.Status = JobStatus.StatusScheduled && currentState.Id = evt.JobId then
             { currentState with
                 Status = JobStatus.StatusTriggered
                 TriggerTime = Some evt.TriggerTime
                 LastUpdated = evt.Timestamp }
        else
            currentState

    | EventExecuted evt ->
        if currentState.Status = JobStatus.StatusTriggered && currentState.Id = evt.JobId then
            { currentState with
                Status = JobStatus.StatusExecuted
                ExecutionTime = Some evt.ExecutionTime
                ResultData = Some evt.ResultData
                LastUpdated = evt.Timestamp }
        else
            currentState

    | EventCancelled evt ->
        if (currentState.Status = JobStatus.StatusScheduled || currentState.Status = JobStatus.StatusTriggered) && currentState.Id = evt.JobId then
            { currentState with
                Status = JobStatus.StatusCancelled
                CancellationReason = Some evt.Reason
                LastUpdated = evt.Timestamp }
        else
            currentState

    | EventFailed evt ->
        if currentState.Status = JobStatus.StatusTriggered && currentState.Id = evt.JobId then
             { currentState with
                 Status = JobStatus.StatusFailed
                 FailureMessage = Some evt.ErrorMessage
                 LastUpdated = evt.Timestamp }
        else
            currentState

let reconstructState (jobId: Guid) (resolvedEvents: seq<ResolvedEvent>) : Job =
    let startState = initialState jobId
    resolvedEvents
    |> Seq.choose tryParseJobEvent
    |> Seq.fold applyEvent startState
