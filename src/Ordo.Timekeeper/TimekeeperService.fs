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
        EventStoreConnectionString = config.GetValue<string>("EventStore:ConnectionString")
        CheckInterval = TimeSpan.FromSeconds(
            config.GetValue<int>("Timekeeper:CheckIntervalSeconds", 10)
        )
    }
    let timekeeperLogger = loggerFactory.CreateLogger<Timekeeper>()
    let timekeeper = new Timekeeper(esClient, timekeeperLogger, timekeeperConfig)

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            try
                logger.LogInformation("Starting Timekeeper service...")
                let! _ = timekeeper.Start()
                
                while not ct.IsCancellationRequested do
                    do! Task.Delay(1000, ct)
                    
                logger.LogInformation("Timekeeper service stopping...")
            finally
                timekeeper.Stop() |> ignore
        }

    override _.StopAsync(ct: CancellationToken) =
        logger.LogInformation("Stopping Timekeeper service...")
        base.StopAsync(ct) 