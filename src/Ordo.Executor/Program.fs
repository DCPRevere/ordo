namespace Ordo.Executor

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open EventStore.Client
open EventStore.Client.PersistentSubscriptions

module Program =

    [<EntryPoint>]
    let main args =
        let builder = Host.CreateApplicationBuilder(args)
        
        // Configure EventStore clients
        let settings = EventStoreClientSettings.Create("esdb://localhost:2113")
        builder.Services.AddSingleton<EventStoreClient>(fun _ -> new EventStoreClient(settings)) |> ignore
        builder.Services.AddSingleton<EventStorePersistentSubscriptionsClient>(fun _ -> new EventStorePersistentSubscriptionsClient(settings)) |> ignore
        
        builder.Services.AddHostedService<Worker>() |> ignore

        builder.Build().Run()

        0