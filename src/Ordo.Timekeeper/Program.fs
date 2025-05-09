namespace Ordo.Timekeeper

open System
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Configuration
open EventStore.Client
open Serilog
open Serilog.Events
open Serilog.Extensions.Hosting

module Program =
    let validateConfig (config: IConfiguration) =
        let connectionString = 
            match config.GetValue<string>("EventStore:ConnectionString") with
            | null | "" -> raise (InvalidOperationException("EventStore:ConnectionString configuration is required"))
            | cs -> cs
        
        let apiBaseUrl = 
            match config.GetValue<string>("Api:BaseUrl") with
            | null | "" -> raise (InvalidOperationException("Api:BaseUrl configuration is required"))
            | url -> url
        
        ()  // Return unit to complete the block

    [<EntryPoint>]
    let main argv =
        try
            let host = 
                Host.CreateDefaultBuilder(argv)
                    .UseSerilog(fun context services config ->
                        config
                            .ReadFrom.Configuration(context.Configuration)
                            .ReadFrom.Services(services)
                            .Enrich.FromLogContext()
                            .WriteTo.Console(outputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} [{SourceContext}]{NewLine}{Exception}")
                            .WriteTo.File("logs/ordo-timekeeper-.log", 
                                rollingInterval = RollingInterval.Day,
                                outputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} [{SourceContext}]{NewLine}{Exception}")
                            |> ignore
                    )
                    .ConfigureServices(fun services ->
                        services.AddHostedService<TimekeeperService>() |> ignore
                        services.AddSingleton<EventStoreClient>(fun sp ->
                            let config = sp.GetRequiredService<IConfiguration>()
                            validateConfig config
                            let connectionString = 
                                match config.GetValue<string>("EventStore:ConnectionString") with
                                | null | "" -> raise (InvalidOperationException("EventStore:ConnectionString configuration is required"))
                                | cs -> cs
                            let settings = EventStoreClientSettings.Create(connectionString)
                            new EventStoreClient(settings)
                        ) |> ignore
                        services.AddHttpClient() |> ignore
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