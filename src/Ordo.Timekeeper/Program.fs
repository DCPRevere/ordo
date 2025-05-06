namespace Ordo.Timekeeper

open System
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Configuration
open EventStore.Client

module Program =
    let validateConfig (config: IConfiguration) =
        let connectionString = config.GetValue<string>("EventStore:ConnectionString")
        if String.IsNullOrEmpty(connectionString) then
            raise (InvalidOperationException("EventStore:ConnectionString configuration is required"))

    [<EntryPoint>]
    let main argv =
        try
            let host = 
                Host.CreateDefaultBuilder(argv)
                    .ConfigureServices(fun services ->
                        services.AddHostedService<TimekeeperService>() |> ignore
                        services.AddSingleton<EventStoreClient>(fun sp ->
                            let config = sp.GetRequiredService<IConfiguration>()
                            validateConfig config
                            let connectionString = config.GetValue<string>("EventStore:ConnectionString")
                            let settings = EventStoreClientSettings.Create(connectionString)
                            new EventStoreClient(settings)
                        ) |> ignore
                        services.AddLogging(fun logging ->
                            logging.ClearProviders() |> ignore
                            logging.AddConsole() |> ignore
                            logging.SetMinimumLevel(LogLevel.Information) |> ignore
                        ) |> ignore
                    )
                    .Build()

            host.Run()
            0
        with 
        | :? InvalidOperationException as ex ->
            printfn "Configuration Error: %s" ex.Message
            1
        | ex ->
            printfn "Unhandled Error: %s" ex.Message
            1