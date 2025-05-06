namespace Ordo.Executor

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

type Worker(logger: ILogger<Worker>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                logger.LogInformation("Executor waiting for jobs at: {time}", DateTimeOffset.Now)
                // TODO: Implement subscription and job processing logic
                do! Task.Delay(TimeSpan.FromSeconds(1), stoppingToken) // Prevent tight loop when idle
        }
