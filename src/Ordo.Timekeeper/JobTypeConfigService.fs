namespace Ordo.Timekeeper

open System
open System.Net.Http
open System.Collections.Concurrent
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Ordo.Api.DTOs
open Ordo.Core.Json
open Serilog
open Serilog.Events

type JobTypeConfigService(httpClient: HttpClient, logger: ILogger<JobTypeConfigService>) =
    let mutable configCache = Map.empty<string, JobTypeDelayConfig>
    let log = Log.ForContext<JobTypeConfigService>()
    
    member this.GetConfigForJobType(jobType: string) : Task<JobTypeDelayConfig option> =
        task {
            match Map.tryFind jobType configCache with
            | Some config ->
                log.Information("Using cached configuration for job type {JobType}: {Config}", jobType, config)
                return Some config
            | None ->
                log.Information("No cached configuration found for job type {JobType}, fetching from API", jobType)
                let! response = httpClient.GetAsync($"api/jobs/types/{jobType}/config")
                if response.IsSuccessStatusCode then
                    let! content = response.Content.ReadAsStringAsync()
                    match tryDeserialise<JobTypeDelayConfig> content with
                    | None ->
                        log.Warning("Failed to deserialize configuration for job type {JobType} from API", jobType)
                        return None
                    | Some config ->
                        log.Information("Retrieved configuration for job type {JobType} from API: {Config}", jobType, config)
                        configCache <- Map.add jobType config configCache
                        return Some config
                else
                    log.Warning("Failed to get configuration for job type {JobType} from API: {StatusCode}", jobType, response.StatusCode)
                    return None
        }

    member this.RefreshConfigs() : Task =
        task {
            log.Information("Refreshing all job type configurations from API")
            let! response = httpClient.GetAsync("api/jobs/types/config")
            if response.IsSuccessStatusCode then
                let! content = response.Content.ReadAsStringAsync()
                match tryDeserialise<Map<string, JobTypeDelayConfig>> content with
                | None ->
                    log.Warning("Failed to deserialize configurations from API")
                    configCache <- Map.empty
                | Some configs ->
                    log.Information("Retrieved {Count} job type configurations from API", configs.Count)
                    configCache <- configs
            else
                log.Warning("Failed to refresh configurations from API: {StatusCode}", response.StatusCode)
        } 