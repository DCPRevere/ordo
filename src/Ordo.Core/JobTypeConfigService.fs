namespace Ordo.Core

open System
open System.Net.Http
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Ordo.Core.Model

type JobTypeConfigService(httpClient: HttpClient, logger: ILogger<JobTypeConfigService>) =
    let mutable configs: Map<string, JobTypeDelayConfig> = Map.empty

    member _.GetConfigForJobType(jobType: string) : Task<JobTypeDelayConfig option> =
        task {
            return
                match configs.TryFind(jobType) with
                | Some config -> Some config
                | None -> None
        }

    member _.RefreshConfigs() : Task =
        task {
            try
                let! response = httpClient.GetAsync("api/jobs/types/config")
                if response.IsSuccessStatusCode then
                    let! content = response.Content.ReadAsStringAsync()
                    let newConfigs = Ordo.Core.Json.deserialise<Map<string, JobTypeDelayConfig>> content
                    configs <- newConfigs
                    logger.LogInformation("Refreshed job type configurations: {Configs}", configs)
                else
                    logger.LogWarning("Failed to refresh job type configurations: {StatusCode}", response.StatusCode)
            with ex ->
                logger.LogError(ex, "Error refreshing job type configurations")
        } 