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

let private jobStreamName (jobId: string) : string =
    sprintf "ordo-job-%s" jobId

let private appendEvent<'T> (streamName: string) (eventType: string) (eventData: 'T) (expectedState: StreamState) : Task =
    task {
        let jsonData = System.Text.Encoding.UTF8.GetBytes(Ordo.Core.Json.serialise eventData)
        let eventToWrite = EventData( Uuid.NewUuid(), eventType, jsonData)
        let! _writeResult = client.AppendToStreamAsync( streamName, expectedState, [| eventToWrite |])
        ()
    } :> Task

let scheduleJob (event: JobScheduledV2) : Task =
    let stream = jobStreamName event.Id
    appendEvent stream JobScheduledV2.Schema.toString event StreamState.NoStream

let triggerJob (event: JobTriggeredV2) : Task =
    let stream = jobStreamName event.Id
    appendEvent stream JobTriggeredV2.Schema.toString event StreamState.Any

let executeJob (event: JobExecutedV2) : Task =
    let stream = jobStreamName event.Id
    appendEvent stream JobExecutedV2.Schema.toString event StreamState.Any

let cancelJob (event: JobCancelledV2) : Task =
    let stream = jobStreamName event.Id
    appendEvent stream JobCancelledV2.Schema.toString event StreamState.Any

let rescheduleJob (event: JobRescheduledV2) : Task =
    let stream = jobStreamName event.Id
    appendEvent stream JobRescheduledV2.Schema.toString event StreamState.Any