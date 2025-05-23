namespace Ordo.Host

open System
open System.IO
open System.Threading.Tasks
open System.Net.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Configuration
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open System.Text.Json
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
        
        services.AddHttpClient() |> ignore
        // services.AddSingleton<Ordo.Core.JobTypeConfigService>(fun sp ->
        //     let httpClientFactory = sp.GetRequiredService<IHttpClientFactory>()
        //     let httpClient = httpClientFactory.CreateClient() :> HttpClient
        //     let logger = sp.GetRequiredService<ILogger<Ordo.Core.JobTypeConfigService>>()
        //     new Ordo.Core.JobTypeConfigService(httpClient, logger)
        // ) |> ignore
        
        services.AddSingleton<ProjectionSynchroniser>(fun sp ->
            let client = sp.GetRequiredService<EventStoreClient>()
            let logger = sp.GetRequiredService<ILogger<ProjectionSynchroniser>>()
            // let configService = sp.GetRequiredService<Ordo.Core.JobTypeConfigService>()
            new ProjectionSynchroniser(client, logger)
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
            .AddApplicationPart(typeof<JobsController>.Assembly)
            .AddJsonOptions(fun options ->
                Ordo.Core.Json.configure options.JsonSerializerOptions
            ) |> ignore
        builder.Services.AddEndpointsApiExplorer() |> ignore
        builder.Services.AddSwaggerGen() |> ignore
        builder.Services.AddDirectoryBrowser() |> ignore
        
        // Configure JSON serialization for all components
        let jsonOptions = JsonSerializerOptions()
        Ordo.Core.Json.configure jsonOptions
        builder.Services.AddSingleton(jsonOptions) |> ignore
        
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
        app.UseDefaultFiles(DefaultFilesOptions(
            FileProvider = Microsoft.Extensions.FileProviders.PhysicalFileProvider(
                Path.Combine(AppContext.BaseDirectory, "wwwroot")
            )
        )) |> ignore
        app.UseStaticFiles(StaticFileOptions(
            FileProvider = Microsoft.Extensions.FileProviders.PhysicalFileProvider(
                Path.Combine(AppContext.BaseDirectory, "wwwroot")
            )
        )) |> ignore
        app.UseAuthorization() |> ignore
        
        app.MapControllers() |> ignore
        app.MapGet("/", fun context -> 
            task {
                context.Response.Redirect("/index.html")
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
                .WriteTo.File("logs/ordo-host-.log", 
                    rollingInterval = RollingInterval.Day,
                    outputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} [{SourceContext}]{NewLine}{Exception}")
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
                            .WriteTo.File("logs/ordo-host-.log", 
                                rollingInterval = RollingInterval.Day,
                                outputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} [{SourceContext}]{NewLine}{Exception}")
                            |> ignore
                    )
                    .ConfigureServices(fun services ->
                        services.AddControllers() |> ignore
                        services.AddEndpointsApiExplorer() |> ignore
                        services.AddSwaggerGen() |> ignore
                        
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
                        
                        services.AddSingleton<ProjectionSynchroniser>(fun sp ->
                            let client = sp.GetRequiredService<EventStoreClient>()
                            let logger = sp.GetRequiredService<ILogger<ProjectionSynchroniser>>()
                            new ProjectionSynchroniser(client, logger)
                        ) |> ignore
                        
                        services.AddSingleton<JobsController>() |> ignore
                        services.AddSingleton<TimekeeperService>() |> ignore
                        services.AddSingleton<Worker>() |> ignore
                        services.AddHttpClient() |> ignore
                    )
                    .ConfigureWebHostDefaults(fun webHostBuilder ->
                        webHostBuilder
                            .Configure(fun app ->
                                app.UseSwagger() |> ignore
                                app.UseSwaggerUI() |> ignore
                                app.UseRouting() |> ignore
                                app.UseEndpoints(fun endpoints ->
                                    endpoints.MapControllers() |> ignore
                                ) |> ignore
                            )
                            |> ignore
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
