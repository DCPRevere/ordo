namespace Ordo.Timekeeper

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Configuration
open EventStore.Client

type TimekeeperService(esClient: EventStoreClient, loggerFactory: ILoggerFactory, config: IConfiguration) =
    inherit BackgroundService()
    
    let logger = loggerFactory.CreateLogger<TimekeeperService>()
    let timekeeperConfig = {
        EventStoreConnectionString = config.GetValue<string>("EventStore:ConnectionString") |> Option.ofObj |> Option.defaultValue ""
        CheckInterval = TimeSpan.FromSeconds(
            config.GetValue<int>("Timekeeper:CheckIntervalSeconds", 10)
        )
    }
    let timekeeperLogger = loggerFactory.CreateLogger<Timekeeper>()
    let timekeeper = new Timekeeper(esClient, timekeeperLogger, timekeeperConfig)

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            logger.LogInformation("Starting Timekeeper service")
            let! _ = timekeeper.Start()
            
            try
                while not ct.IsCancellationRequested do
                    do! Task.Delay(1000, ct)
            with
            | :? OperationCanceledException ->
                logger.LogInformation("Timekeeper service stopping")
            | ex ->
                logger.LogError(ex, "Timekeeper service error")
            
            let! _ = timekeeper.Stop()
            return ()
        }

    override _.StopAsync(ct: CancellationToken) =
        logger.LogInformation("Stopping Timekeeper service")
        Task.CompletedTask 