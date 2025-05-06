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

type SynchroniserHostedService(synchroniser: ProjectionSynchroniser, logger: ILogger<SynchroniserHostedService>) =
    interface IHostedService with
        member _.StartAsync(cancellationToken) =
            logger.LogInformation("Starting ProjectionSynchroniser via Hosted Service.")
            synchroniser.Start(cancellationToken)
        member _.StopAsync(cancellationToken) =
            logger.LogInformation("Stopping ProjectionSynchroniser via Hosted Service.")
            synchroniser.Stop(cancellationToken)

module Program =
    let exitCode = 0

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)

        let connectionString = 
            match builder.Configuration.GetConnectionString("EventStoreDB") with
            | null | "" -> "esdb://localhost:2113?tls=false"
            | cs -> cs
        let settings = EventStoreClientSettings.Create(connectionString)
        let client = new EventStoreClient(settings)

        builder.Services.AddSingleton(client) |> ignore
        builder.Services.AddSingleton<ProjectionSynchroniser>() |> ignore
        builder.Services.AddHostedService<SynchroniserHostedService>() |> ignore

        builder.Services.AddControllers() |> ignore
        builder.Services.AddEndpointsApiExplorer() |> ignore
        builder.Services.AddSwaggerGen() |> ignore

        let app = builder.Build()

        // Configure the HTTP request pipeline.
        if app.Environment.IsDevelopment() then
            app.UseSwagger() |> ignore
            app.UseSwaggerUI() |> ignore

        app.UseHttpsRedirection() |> ignore
        app.UseAuthorization() |> ignore

        app.MapControllers() |> ignore

        app.Run()

        exitCode
