open System
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Configuration
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.AspNetCore.Http
open EventStore.Client
open Ordo.Api
open Ordo.Api.Controllers
open Ordo.Timekeeper
open Ordo.Executor
open Ordo.Synchroniser
open Serilog
open Serilog.Extensions.Hosting

module Host =
    type Component =
        | Api
        | Timekeeper
        | Executor
        | All

    let createEventStoreClient (config: IConfiguration) =
        let connectionString = 
            match config.GetValue<string>("EventStore:ConnectionString") with
            | null | "" -> "esdb://localhost:2113?tls=false"
            | cs -> cs
        let settings = EventStoreClientSettings.Create(connectionString)
        new EventStoreClient(settings)

    let configureServices (services: IServiceCollection) (config: IConfiguration) (comp: Component) =
        let connectionString = 
            match config.GetValue<string>("EventStore:ConnectionString") with
            | null | "" -> "esdb://localhost:2113?tls=false"
            | cs -> cs
        let settings = EventStoreClientSettings.Create(connectionString)
        let client = new EventStoreClient(settings)
        let persistentSubClient = new EventStorePersistentSubscriptionsClient(settings)
        
        services.AddSingleton(client) |> ignore
        services.AddSingleton(persistentSubClient) |> ignore
        
        services.AddSingleton<ProjectionSynchroniser>(fun sp ->
            let client = sp.GetRequiredService<EventStoreClient>()
            let logger = sp.GetRequiredService<ILogger<ProjectionSynchroniser>>()
            ProjectionSynchroniser(client, logger)
        ) |> ignore
        
        services.AddHostedService<SynchroniserHostedService>() |> ignore
        
        match comp with
        | All ->
            services.AddSingleton<Ordo.Executor.Worker>() |> ignore
            services.AddHostedService<TimekeeperService>() |> ignore
            services.AddHostedService<Ordo.Executor.Worker>() |> ignore
        | _ -> ()

    let configureWebApp (builder: WebApplicationBuilder) (comp: Component) (srcDir: string) (env: string) (args: string[]) =
        builder.Configuration.AddJsonFile("appsettings.json", optional = true, reloadOnChange = true) |> ignore
        builder.Configuration.AddJsonFile($"appsettings.{env}.json", optional = true, reloadOnChange = true) |> ignore
        
        let apiConfigPath = Path.Combine(srcDir, "Ordo.Api/appsettings.json")
        let apiEnvConfigPath = Path.Combine(srcDir, $"Ordo.Api/appsettings.{env}.json")
        builder.Configuration.AddJsonFile(apiConfigPath, optional = false, reloadOnChange = true) |> ignore
        builder.Configuration.AddJsonFile(apiEnvConfigPath, optional = true, reloadOnChange = true) |> ignore
        
        if comp = All then
            let timekeeperConfigPath = Path.Combine(srcDir, "Ordo.Timekeeper/appsettings.json")
            let timekeeperEnvConfigPath = Path.Combine(srcDir, $"Ordo.Timekeeper/appsettings.{env}.json")
            builder.Configuration.AddJsonFile(timekeeperConfigPath, optional = false, reloadOnChange = true) |> ignore
            builder.Configuration.AddJsonFile(timekeeperEnvConfigPath, optional = true, reloadOnChange = true) |> ignore
            
            let executorConfigPath = Path.Combine(srcDir, "Ordo.Executor/appsettings.json")
            let executorEnvConfigPath = Path.Combine(srcDir, $"Ordo.Executor/appsettings.{env}.json")
            builder.Configuration.AddJsonFile(executorConfigPath, optional = false, reloadOnChange = true) |> ignore
            builder.Configuration.AddJsonFile(executorEnvConfigPath, optional = true, reloadOnChange = true) |> ignore
        
        builder.Configuration.AddEnvironmentVariables() |> ignore
        builder.Configuration.AddCommandLine(args) |> ignore
        
        configureServices builder.Services builder.Configuration comp
        
        builder.Services.AddControllers()
            .AddApplicationPart(typeof<JobsController>.Assembly) |> ignore
        builder.Services.AddEndpointsApiExplorer() |> ignore
        builder.Services.AddSwaggerGen() |> ignore
        
        builder.WebHost
            .UseKestrel(fun options ->
                options.ListenLocalhost(5000) |> ignore
                options.ListenLocalhost(5001, fun listenOptions ->
                    listenOptions.UseHttps() |> ignore
                ) |> ignore
            ) |> ignore
        
        let app = builder.Build()
        
        if app.Environment.IsDevelopment() then
            app.UseSwagger() |> ignore
            app.UseSwaggerUI() |> ignore

        app.UseRouting() |> ignore
        app.UseHttpsRedirection() |> ignore
        app.UseAuthorization() |> ignore
        
        app.MapControllers() |> ignore
        app.MapGet("/", fun context -> 
            task {
                do! context.Response.WriteAsync("Ordo API is running")
            } :> Task) |> ignore
        
        app

    let configureHost (builder: IHostBuilder) (comp: Component) (srcDir: string) (env: string) (args: string[]) =
        builder
            .ConfigureAppConfiguration(fun context config ->
                config.AddJsonFile("appsettings.json", optional = true, reloadOnChange = true) |> ignore
                config.AddJsonFile($"appsettings.{env}.json", optional = true, reloadOnChange = true) |> ignore
                
                match comp with
                | Timekeeper ->
                    let timekeeperConfigPath = Path.Combine(srcDir, "Ordo.Timekeeper/appsettings.json")
                    let timekeeperEnvConfigPath = Path.Combine(srcDir, $"Ordo.Timekeeper/appsettings.{env}.json")
                    config.AddJsonFile(timekeeperConfigPath, optional = false, reloadOnChange = true) |> ignore
                    config.AddJsonFile(timekeeperEnvConfigPath, optional = true, reloadOnChange = true) |> ignore
                | Executor ->
                    let executorConfigPath = Path.Combine(srcDir, "Ordo.Executor/appsettings.json")
                    let executorEnvConfigPath = Path.Combine(srcDir, $"Ordo.Executor/appsettings.{env}.json")
                    config.AddJsonFile(executorConfigPath, optional = false, reloadOnChange = true) |> ignore
                    config.AddJsonFile(executorEnvConfigPath, optional = true, reloadOnChange = true) |> ignore
                | _ -> ()
                
                config.AddEnvironmentVariables() |> ignore
                config.AddCommandLine(args) |> ignore
            )
            .ConfigureServices(fun context services ->
                let config = context.Configuration
                configureServices services config comp
            )
            .Build()

    let createHostBuilder (args: string[]) (comp: Component) =
        let env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") |> Option.ofObj |> Option.defaultValue "Production"
        let baseDir = AppContext.BaseDirectory
        let srcDir = Path.GetFullPath(Path.Combine(baseDir, "../../../.."))
        
        let configureSerilog (context: HostBuilderContext) (services: IServiceProvider) (loggerConfiguration: Serilog.LoggerConfiguration) =
            loggerConfiguration
                .ReadFrom.Configuration(context.Configuration) // Allows configuration from appsettings.json
                .ReadFrom.Services(services) // For DI-based enrichers, sinks, etc.
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} [{SourceContext}]{NewLine}{Exception}")
            |> ignore

        match comp with
        | Api | All ->
            let builder = WebApplication.CreateBuilder(args)
            builder.Logging.ClearProviders() |> ignore
            builder.Host.UseSerilog(configureSerilog) |> ignore
            configureWebApp builder comp srcDir env args :> obj
        | _ ->
            let builder = Host.CreateDefaultBuilder(args)
            builder.ConfigureLogging(fun logBuilder -> logBuilder.ClearProviders() |> ignore) |> ignore
            builder.UseSerilog(configureSerilog) |> ignore
            configureHost builder comp srcDir env args :> obj

    let runComponent (comp: Component) (args: string[]) =
        let app = createHostBuilder args comp
        match app with
        | :? WebApplication as webApp -> webApp.Run()
        | :? IHost as host -> host.Run()
        | _ -> failwith "Unexpected application type"

    [<EntryPoint>]
    let main argv =
        Log.Logger <- Serilog.LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] [Bootstrap] {Message:lj}{NewLine}{Exception}")
            .CreateBootstrapLogger()
        
        let result =
            try
                Log.Information("Starting Ordo Host...")
                let comp =
                    match argv with
                    | [| "--api" |] -> Api
                    | [| "--timekeeper" |] -> Timekeeper
                    | [| "--executor" |] -> Executor
                    | [| "--all" |] -> All
                    | _ -> 
                        printfn "Usage: dotnet run [--api|--timekeeper|--executor|--all]"
                        printfn "  --api        Run only the API component"
                        printfn "  --timekeeper Run only the Timekeeper component"
                        printfn "  --executor   Run only the Executor component"
                        printfn "  --all        Run all components together"
                        Log.Error("Invalid command-line arguments provided for Ordo Host.")
                        Api // Return a valid Component type, but we'll exit before using it
                
                if comp = Api || comp = All || comp = Timekeeper || comp = Executor then
                    runComponent comp argv
                    Log.Information("Ordo Host shut down successfully.")
                    0 // Success exit code
                else
                    Log.Error("Component could not be determined due to invalid arguments.")
                    1 // Error code

            with
            | ex ->
                Log.Fatal(ex, "Ordo Host terminated unexpectedly!")
                1 // Failure exit code
        
        Log.CloseAndFlush()
        result // Return the exit code
