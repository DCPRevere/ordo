module Ordo.Core.EventStore

open System
open System.Text.Json
open System.Threading.Tasks
open EventStore.Client
open Ordo.Core.Events

let private connectionString =
    "esdb://admin:changeit@localhost:2113?tls=false&tlsVerifyCert=false"

let private settings = EventStoreClientSettings.Create(connectionString)

let client = new EventStoreClient(settings)

let private jobStreamName (jobId: Guid) : string =
    sprintf "ordo-job-%s" (jobId.ToString("D"))

let private appendEvent<'T> (streamName: string) (eventType: string) (eventData: 'T) (expectedState: StreamState) : Task =
    task {
        let jsonData = JsonSerializer.SerializeToUtf8Bytes(eventData)
        let eventToWrite = EventData( Uuid.NewUuid(), eventType, jsonData)
        let! _writeResult = client.AppendToStreamAsync( streamName, expectedState, [| eventToWrite |])
        ()
    } :> Task

let scheduleJob (event: JobScheduled) : Task =
    let stream = jobStreamName event.JobId
    appendEvent stream "JobScheduled" event StreamState.NoStream

let triggerJob (event: JobTriggered) : Task =
    let stream = jobStreamName event.JobId
    appendEvent stream "JobTriggered" event StreamState.Any

let executeJob (event: JobExecuted) : Task =
    let stream = jobStreamName event.JobId
    appendEvent stream "JobExecuted" event StreamState.Any

let cancelJob (event: JobCancelled) : Task =
    let stream = jobStreamName event.JobId
    appendEvent stream "JobCancelled" event StreamState.Any

let failJob (event: JobFailed) : Task =
    let stream = jobStreamName event.JobId
    appendEvent stream "JobFailed" event StreamState.Any