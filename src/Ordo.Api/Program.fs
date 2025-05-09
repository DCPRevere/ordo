namespace Ordo.Api

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Configuration
open EventStore.Client
open Ordo.Synchroniser
open Ordo.Core.Json
open System.Text.Json.Serialization
open FSharp.SystemTextJson
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Diagnostics
open System.IO
open Microsoft.AspNetCore.Http.Features

type SynchroniserHostedService(synchroniser: ProjectionSynchroniser, logger: ILogger<SynchroniserHostedService>) =
    interface IHostedService with
        member _.StartAsync(cancellationToken) =
            logger.LogInformation("Starting ProjectionSynchroniser via Hosted Service.")
            synchroniser.Start(cancellationToken)
        member _.StopAsync(cancellationToken) =
            logger.LogInformation("Stopping ProjectionSynchroniser via Hosted Service.")
            synchroniser.Stop(cancellationToken)

type Program() = class end

module Program =
    let configureServices (services: IServiceCollection) =
        let connectionString = 
            match services.BuildServiceProvider().GetRequiredService<IConfiguration>().GetConnectionString("EventStoreDB") with
            | null | "" -> "esdb://localhost:2113?tls=false"
            | cs -> cs
        let settings = EventStoreClientSettings.Create(connectionString)
        let client = new EventStoreClient(settings)

        services.AddSingleton(client) |> ignore
        services.AddControllers()
            .AddJsonOptions(fun options ->
                options.JsonSerializerOptions.PropertyNameCaseInsensitive <- true
                options.JsonSerializerOptions.Converters.Add(
                    JsonFSharpConverter(
                        JsonUnionEncoding.ExternalTag ||| 
                        JsonUnionEncoding.UnwrapFieldlessTags ||| 
                        JsonUnionEncoding.UnwrapOption |||
                        JsonUnionEncoding.NamedFields |||
                        JsonUnionEncoding.Untagged |||
                        JsonUnionEncoding.AdjacentTag))
                options.JsonSerializerOptions.Converters.Add(JsonStringEnumConverter())
            )
            |> ignore

        services.AddEndpointsApiExplorer() |> ignore
        services.AddSwaggerGen() |> ignore
        services.AddSingleton<ProjectionSynchroniser>() |> ignore
        services.AddHostedService<SynchroniserHostedService>() |> ignore

        // Add CORS
        services.AddCors(fun options ->
            options.AddDefaultPolicy(fun policy ->
                policy
                    .WithOrigins(services.BuildServiceProvider().GetRequiredService<IConfiguration>().GetSection("Cors:AllowedOrigins").Get<string[]>())
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                |> ignore
            )
        ) |> ignore

        // Add static files
        services.AddDirectoryBrowser() |> ignore

    let configureApp (app: WebApplication) =
        if app.Environment.IsDevelopment() then
            app.UseSwagger() |> ignore
            app.UseSwaggerUI() |> ignore

        app.UseHttpsRedirection() |> ignore
        app.UseStaticFiles() |> ignore
        app.UseCors() |> ignore
        app.UseAuthorization() |> ignore
        
        // Add request logging
        app.Use(fun (context: HttpContext) (next: RequestDelegate) ->
            task {
                let logger = context.RequestServices.GetRequiredService<ILogger<Program>>()
                let! requestBody = 
                    if context.Request.ContentType = "application/json" then
                        task {
                            use reader = new StreamReader(context.Request.Body)
                            let! body = reader.ReadToEndAsync()
                            // Reset the request body stream position so it can be read again
                            context.Request.Body <- new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body))
                            return Some body
                        }
                    else
                        task { return None }

                logger.LogInformation(
                    "Request: {Method} {Path}{Query} {Body}",
                    context.Request.Method,
                    context.Request.Path,
                    context.Request.QueryString,
                    requestBody |> Option.defaultValue "N/A"
                )

                do! next.Invoke(context)
            } :> Task
        ) |> ignore

        // Add detailed error handling
        app.UseExceptionHandler(fun errorApp ->
            errorApp.Run(fun context ->
                task {
                    let logger = context.RequestServices.GetRequiredService<ILogger<Program>>()
                    let error = context.Features.Get<IExceptionHandlerFeature>()
                    match error with
                    | null -> ()
                    | error -> 
                        logger.LogError(error.Error, "Unhandled exception: {Message}", error.Error.Message)
                    context.Response.StatusCode <- StatusCodes.Status500InternalServerError
                    do! context.Response.WriteAsJsonAsync({| 
                        title = "An error occurred"
                        status = StatusCodes.Status500InternalServerError
                        detail = error.Error.Message
                    |})
                } :> Task
            )
        ) |> ignore

        app.MapControllers() |> ignore

    [<EntryPoint>]
    let main argv =
        try
            let builder = WebApplication.CreateBuilder(argv)
            
            builder.Services
                |> configureServices

            let app = builder.Build()
            app |> configureApp
            app.Run()
            0
        with
        | ex ->
            printfn "Unhandled Error: %s" ex.Message
            1
