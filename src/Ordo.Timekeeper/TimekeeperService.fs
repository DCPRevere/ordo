namespace Ordo.Timekeeper

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Configuration
open EventStore.Client
open Ordo.Core
open Ordo.Core.Events
open Ordo.Core.EventStore
open Ordo.Synchroniser
open Serilog
open Serilog.Events
open System.Net.Http

type TimekeeperService(
    config: IConfiguration,
    loggerFactory: ILoggerFactory) =
    inherit BackgroundService()
    
    let log = Log.ForContext<TimekeeperService>()
    let timekeeperConfig = {
        EventStoreConnectionString = config.GetValue<string>("EventStore:ConnectionString") |> Option.ofObj |> Option.defaultValue ""
        CheckInterval = TimeSpan.FromSeconds(config.GetValue<int>("Timekeeper:CheckIntervalSeconds", 60))
        ApiBaseUrl = config.GetValue<string>("Api:BaseUrl") |> Option.ofObj |> Option.defaultValue "http://localhost:5000"
    }
    let settings = EventStoreClientSettings.Create(timekeeperConfig.EventStoreConnectionString)
    let client = new EventStoreClient(settings)
    let httpClient = new HttpClient(BaseAddress = Uri(timekeeperConfig.ApiBaseUrl))
    let configService = new JobTypeConfigService(httpClient, loggerFactory.CreateLogger<JobTypeConfigService>())
    let timekeeper = new Timekeeper(client, loggerFactory, timekeeperConfig, configService)
    
    let rec waitForApiReady (ct: CancellationToken) =
        task {
            try
                log.Information("Attempting to connect to API at {ApiUrl}", timekeeperConfig.ApiBaseUrl)
                let! response = httpClient.GetAsync("api/jobs/types/config", ct)
                if response.IsSuccessStatusCode then
                    log.Information("Successfully connected to API")
                    return true
                else
                    log.Warning("API returned non-success status code: {StatusCode}", response.StatusCode)
                    return false
            with
            | :? OperationCanceledException -> 
                log.Information("API connection attempt cancelled")
                return false
            | ex ->
                log.Warning(ex, "Failed to connect to API, will retry in 5 seconds")
                do! Task.Delay(1000, ct)
                return! waitForApiReady ct
        }
    
    override this.ExecuteAsync(ct: CancellationToken) : Task =
        task {
            log.Information("Starting Timekeeper service")
            
            // Wait for API to be ready
            let! apiReady = waitForApiReady ct
            if not apiReady then
                log.Error("Failed to connect to API after multiple attempts")
                return ()
            
            log.Information("Fetching job type configurations from API")
            do! configService.RefreshConfigs()
            log.Information("Job type configurations fetched successfully")
            
            log.Information("Starting Timekeeper")
            do! timekeeper.Start()
            
            while not ct.IsCancellationRequested do
                do! Task.Delay(1000, ct)
            
            log.Information("Stopping Timekeeper service")
            do! timekeeper.Stop()
            httpClient.Dispose()
            client.Dispose()
        }

    override _.StopAsync(ct: CancellationToken) =
        log.Information("Stopping Timekeeper service")
        Task.CompletedTask 