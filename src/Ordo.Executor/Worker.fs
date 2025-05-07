namespace Ordo.Executor

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Ordo.Core.JobState
open Ordo.Core.Events
open Ordo.Synchroniser
open EventStore.Client
open System.Text.Json

// The Worker now requires EventStorePersistentSubscriptionsClient for persistent subscription operations
type Worker(logger: ILogger<Worker>, 
            synchroniser: ProjectionSynchroniser, 
            client: EventStoreClient,
            persistentSubClient: EventStorePersistentSubscriptionsClient
           ) =
    inherit BackgroundService()
    let mutable tickCount = 0
    let subscriptionGroup = "ordo-executor"
    let userCredentials = UserCredentials("admin", "changeit") // Define once

    let writeJobExecutedEvent (jobId: Guid) (resultData: string) =
        task {
            let eventPayload = 
                { JobId = jobId
                  ExecutionTime = DateTimeOffset.UtcNow
                  ResultData = resultData
                  Timestamp = DateTimeOffset.UtcNow }
            
            let effectiveStreamId = $"ordo-job-{jobId}"
            let eventDataBytes = JsonSerializer.SerializeToUtf8Bytes(eventPayload)
            let eventType = "JobExecuted"
            
            let eventData = EventData(
                Uuid.NewUuid(),
                eventType,
                eventDataBytes,
                ReadOnlyMemory<byte>.Empty)
            
            let! _ = client.AppendToStreamAsync(
                effectiveStreamId,
                StreamState.Any,
                [| eventData |],
                ?userCredentials = Some userCredentials, 
                cancellationToken = CancellationToken.None) 
            
            logger.LogDebug("Wrote JobExecuted event for job {JobId}", jobId)
        }

    let handleJobTriggeredEvent (triggered: JobTriggered) (subscription: PersistentSubscription) (resolvedEvent: ResolvedEvent) =
        task {
            logger.LogInformation("Processing job {JobId}", triggered.JobId)
            try
                do! writeJobExecutedEvent triggered.JobId "Job executed successfully (via persistent subscription)"
                do! subscription.Ack([| resolvedEvent |])
                logger.LogInformation("Successfully executed job {JobId}", triggered.JobId)
            with ex ->
                logger.LogError(ex, "Error executing job {JobId}", triggered.JobId)
                do! subscription.Nack(PersistentSubscriptionNakEventAction.Park, $"Failed to execute: {ex.Message}", [| resolvedEvent |])
        }

    let handleResolvedEvent (resolvedEvt: ResolvedEvent) (subscription: PersistentSubscription) (cancellationToken: CancellationToken) : Task =
        task {
            try
                match resolvedEvt.Event with
                | null -> 
                    logger.LogWarning("Received resolved event with null Event property. Link: {IsLinkPresent}, OriginalStreamId: {OriginalStreamId}", 
                                      (resolvedEvt.Link <> null), resolvedEvt.OriginalStreamId)
                    do! subscription.Nack(PersistentSubscriptionNakEventAction.Park, "Null Event property in ResolvedEvent", [| resolvedEvt |])
                    return ()

                | actualEvent -> 
                    let eventType = actualEvent.EventType
                    let sourceStreamId = actualEvent.EventStreamId 

                    if String.IsNullOrEmpty(eventType) then
                        logger.LogWarning("Received event with null or empty event type from stream {SourceStreamId}", sourceStreamId)
                        do! subscription.Nack(PersistentSubscriptionNakEventAction.Park, "Null or empty event type", [| resolvedEvt |])
                        return ()
                    
                    if String.IsNullOrEmpty(sourceStreamId) then
                        logger.LogWarning("Received event with null or empty source stream ID. EventType: {EventType}", eventType)
                        do! subscription.Nack(PersistentSubscriptionNakEventAction.Park, "Null or empty source stream ID", [| resolvedEvt |])
                        return ()

                    if not (sourceStreamId.StartsWith("ordo-job-")) then
                        logger.LogDebug("Ignoring event {EventType} from non-job stream {SourceStreamId}", eventType, sourceStreamId)
                        do! subscription.Ack([| resolvedEvt |])
                        return ()
                    
                    logger.LogDebug("Received event {EventType} from stream {SourceStreamId} via persistent subscription", eventType, sourceStreamId)
                    
                    match tryParseJobEvent resolvedEvt with 
                    | Some jobEvent ->
                        match jobEvent with
                        | EventTriggered triggered ->
                            do! handleJobTriggeredEvent triggered subscription resolvedEvt
                        | _ -> 
                            logger.LogDebug("Ignoring non-triggered event type {EventType} for job {JobId}", 
                                jobEvent.GetType().Name, getJobId jobEvent) 
                            do! subscription.Ack([| resolvedEvt |])
                    | None -> 
                        logger.LogWarning("Could not parse event {EventType} from stream {SourceStreamId}. NACKing event.", 
                            eventType, sourceStreamId)
                        do! subscription.Nack(PersistentSubscriptionNakEventAction.Park, "Could not parse event data", [| resolvedEvt |])
            with ex ->
                logger.LogError(ex, "Critical error in handleResolvedEvent. Attempting to NACK event.")
                try
                    do! subscription.Nack(PersistentSubscriptionNakEventAction.Park, $"Unhandled exception in handler: {ex.Message}", [| resolvedEvt |])
                with nackEx ->
                    logger.LogError(nackEx, "Failed to NACK event after critical error in handleResolvedEvent.")
        }

    let createSubscriptionSettings() =
        PersistentSubscriptionSettings(
            resolveLinkTos = true, 
            startFrom = Position.Start,
            extraStatistics = true,
            messageTimeout = TimeSpan.FromSeconds(60),
            maxRetryCount = 5,
            checkPointAfter = TimeSpan.FromSeconds(5),
            checkPointLowerBound = 5,
            checkPointUpperBound = 500,
            liveBufferSize = 1000,
            readBatchSize = 50,
            historyBufferSize = 1000,
            consumerStrategyName = SystemConsumerStrategies.RoundRobin 
        )

    let createOrUpdateSubscriptionGroup (cancellationToken: CancellationToken) =
        task {
            let settings = createSubscriptionSettings()
            try
                logger.LogInformation("Attempting to create persistent subscription group {GroupName} for $all with JobTriggered filter", subscriptionGroup)
                let eventFilter = EventTypeFilter.Prefix("JobTriggered")
                logger.LogDebug("Using event filter: {Filter}", eventFilter)
                do! persistentSubClient.CreateToAllAsync(
                    subscriptionGroup,
                    eventFilter,
                    settings,
                    ?userCredentials = Some userCredentials,
                    cancellationToken = cancellationToken)
                logger.LogInformation("Persistent subscription group {GroupName} for $all created with JobTriggered filter.", subscriptionGroup)
            with
            | :? Grpc.Core.RpcException as rpcEx ->
                if rpcEx.StatusCode = Grpc.Core.StatusCode.AlreadyExists then
                    logger.LogWarning("Persistent subscription group {GroupName} for $all already exists (Caught RpcException with AlreadyExists). Attempting to update.", subscriptionGroup)
                    try 
                        logger.LogDebug("Updating subscription group with settings: {Settings}", settings)
                        do! persistentSubClient.UpdateToAllAsync(
                            subscriptionGroup,
                            settings, 
                            ?userCredentials = Some userCredentials,
                            cancellationToken = cancellationToken)
                        logger.LogInformation("Persistent subscription group {GroupName} for $all updated.", subscriptionGroup)
                    with updateEx ->
                        logger.LogError(updateEx, "Failed to update persistent subscription group {GroupName} for $all after finding it exists.", subscriptionGroup)
                        raise updateEx // Re-raise the update exception
                else
                    // It's an RpcException but not AlreadyExists, so re-raise
                    logger.LogError(rpcEx, "Failed to create persistent subscription group {GroupName} for $all due to an unexpected RpcException.", subscriptionGroup)
                    raise rpcEx 
            | ex -> // Catch other non-RpcException types during creation
                logger.LogError(ex, "Failed to create persistent subscription group {GroupName} for $all (and it wasn't an RpcException).", subscriptionGroup)
                raise ex // Re-raise other creation exceptions
        }

    let handleSubscriptionMessage (message: PersistentSubscriptionMessage) (subscription: PersistentSubscription) (stoppingToken: CancellationToken) =
        task {
            match message with
            | :? PersistentSubscriptionMessage.SubscriptionConfirmation as subConfirmation ->
                logger.LogInformation("Persistent subscription {SubscriptionId} confirmed by server.", subConfirmation.SubscriptionId)
            | :? PersistentSubscriptionMessage.Event as eventMsg ->
                let resolvedEvent = eventMsg.ResolvedEvent
                let retryCount = eventMsg.RetryCount
                let retryCountValue = if retryCount.HasValue then retryCount.Value else 0
                let eventTypeStr = 
                    match resolvedEvent.OriginalEvent with
                    | null -> "N/A"
                    | event -> event.EventType
                let eventStreamIdStr = resolvedEvent.OriginalStreamId
                
                if retryCountValue > 0 then
                    logger.LogWarning("Received event (Retry {RetryCount}): {EventType} from {StreamId}", 
                                      retryCountValue, eventTypeStr, eventStreamIdStr)
                else
                    logger.LogDebug("Received event: {EventType} from {StreamId}", 
                                    eventTypeStr, eventStreamIdStr)
                
                try
                    do! handleResolvedEvent resolvedEvent subscription stoppingToken
                with ex ->
                    logger.LogError(ex, "Failed to handle event {EventType} from {StreamId} (Retry {RetryCount})", 
                                    eventTypeStr, eventStreamIdStr, retryCountValue)
                    if retryCountValue >= 5 then
                        logger.LogError("Max retries reached for event {EventType} from {StreamId}. Parking event.", 
                                        eventTypeStr, eventStreamIdStr)
                        do! subscription.Nack(PersistentSubscriptionNakEventAction.Park, 
                                            $"Max retries reached: {ex.Message}", [| resolvedEvent |])
                    else
                        do! subscription.Nack(PersistentSubscriptionNakEventAction.Retry, 
                                            $"Retry attempt {retryCountValue + 1}: {ex.Message}", [| resolvedEvent |])
            | _ -> 
                logger.LogWarning("Received unknown message type: {MessageType}", message.GetType().Name)
        }

    let runPollingLoop (stoppingToken: CancellationToken) =
        task {
            logger.LogInformation("Entering post-subscription polling loop.")
            while not stoppingToken.IsCancellationRequested do
                let now = DateTimeOffset.UtcNow
                let metrics = synchroniser.GetMetrics() 
                let triggeredJobs = 
                    synchroniser.GetDueJobs(now)
                    |> List.filter (fun (job, _) -> job.Status = JobStatus.StatusTriggered)
                
                let executedJobs = 
                    synchroniser.GetDueJobs(now)
                    |> List.filter (fun (job, _) -> job.Status = JobStatus.StatusExecuted)

                tickCount <- tickCount + 1
                if tickCount % 10 = 0 then
                    logger.LogInformation("Post-subscription Executor status at {Time}: {Triggered} triggered jobs, {Executed} executed jobs out of {Total} total jobs", 
                        now, triggeredJobs.Length, executedJobs.Length, metrics.TotalJobs)
                else
                    logger.LogDebug("Post-subscription Executor status at {Time}: {Triggered} triggered jobs, {Executed} executed jobs out of {Total} total jobs", 
                        now, triggeredJobs.Length, executedJobs.Length, metrics.TotalJobs)

                try
                    do! Task.Delay(TimeSpan.FromSeconds(1), stoppingToken) 
                with :? OperationCanceledException ->
                    logger.LogInformation("Post-subscription polling loop cancelled.")
                    return ()
        }

    override this.StartAsync(cancellationToken: CancellationToken) =
        logger.LogInformation("Initializing Worker service")
        base.StartAsync(cancellationToken)

    override this.StopAsync(cancellationToken: CancellationToken) =
        logger.LogInformation("Stopping Worker service")
        base.StopAsync(cancellationToken)

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            try
                logger.LogInformation("Starting Worker service")
                try
                    do! createOrUpdateSubscriptionGroup stoppingToken
                with ex ->
                    logger.LogCritical(ex, "Failed to initialize persistent subscription group")
                    return () 

                logger.LogInformation("Connecting to persistent subscription group {GroupName}", subscriptionGroup)

                try
                    let bufferSize = 50
                    
                    let eventAppeared = Func<PersistentSubscription, ResolvedEvent, Nullable<int>, CancellationToken, Task>(fun sub evt retryCount ct ->
                        task {
                            try
                                match tryParseJobEvent evt with
                                | Some (EventTriggered triggered) ->
                                    logger.LogInformation("Processing JobTriggered event for job {JobId}", triggered.JobId)
                                    do! handleJobTriggeredEvent triggered sub evt
                                | _ -> 
                                    do! sub.Ack([| evt |])
                            with ex ->
                                logger.LogError(ex, "Error processing event: {Error}", ex.Message)
                                do! sub.Nack(PersistentSubscriptionNakEventAction.Retry, $"Error in handler: {ex.Message}", [| evt |])
                        } :> Task)

                    let subscriptionDropped = Action<PersistentSubscription, SubscriptionDroppedReason, exn>(fun sub reason ex ->
                        logger.LogError(ex, "Subscription dropped. Reason: {Reason}", reason))

                    let! subscription = persistentSubClient.SubscribeToAllAsync(
                        subscriptionGroup,
                        eventAppeared,
                        subscriptionDropped,
                        bufferSize = bufferSize,
                        ?userCredentials = Some userCredentials,
                        cancellationToken = stoppingToken)

                    logger.LogInformation("Worker service ready and waiting for JobTriggered events")

                    let mutable lastStatusTime = DateTimeOffset.UtcNow
                    while not stoppingToken.IsCancellationRequested do
                        let now = DateTimeOffset.UtcNow
                        if (now - lastStatusTime).TotalSeconds >= 10.0 then
                            logger.LogDebug("Worker service active and waiting for events")
                            lastStatusTime <- now
                        do! Task.Delay(TimeSpan.FromSeconds(1), stoppingToken)

                with
                | :? OperationCanceledException when stoppingToken.IsCancellationRequested ->
                    logger.LogInformation("Worker service stopping")
                | ex -> 
                    logger.LogError(ex, "Worker service error: {Error}", ex.Message)
                
                do! runPollingLoop stoppingToken
            with ex ->
                logger.LogError(ex, "Worker service failed: {Error}", ex.Message)
                return ()
        }
