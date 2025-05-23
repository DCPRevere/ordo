module Ordo.Core.JobTypeConfig

open System
open System.Collections.Concurrent
open Microsoft.Extensions.Configuration
open Serilog

type JobTypeDelayConfig = {
    DefaultDelay: TimeSpan
    MaxRetries: int
    RetryDelayMultiplier: float
}

type JobTypeConfiguration(config: IConfiguration) =
    let delayConfigs = ConcurrentDictionary<string, JobTypeDelayConfig>()
    let log = Log.ForContext<JobTypeConfiguration>()
    
    do
        // Load initial configurations from appsettings.json
        config.GetSection("JobTypes").GetChildren()
        |> Seq.iter (fun section ->
            let jobType = section.Key
            let config = {
                DefaultDelay = TimeSpan.Parse(section.GetValue<string>("DefaultDelay"))
                MaxRetries = section.GetValue<int>("MaxRetries", 3)
                RetryDelayMultiplier = section.GetValue<float>("RetryDelayMultiplier", 2.0)
            }
            log.Information("Loaded config from appsettings for job type {JobType}: {Config}", jobType, config)
            delayConfigs.TryAdd(jobType, config) |> ignore
        )
    
    member _.GetDelayConfig(jobType: string) : JobTypeDelayConfig option =
        match delayConfigs.TryGetValue(jobType) with
        | true, config -> Some config
        | false, _ -> None
    
    member _.UpdateDelayConfig(jobType: string, config: JobTypeDelayConfig) =
        delayConfigs.AddOrUpdate(jobType, config, fun _ _ -> config)
    
    member _.CalculateNextExecutionTime(jobType: string, currentTime: DateTimeOffset, retryCount: int) : DateTimeOffset =
        match delayConfigs.TryGetValue(jobType) with
        | true, config ->
            let delay = config.DefaultDelay * (config.RetryDelayMultiplier ** float retryCount)
            currentTime.Add(delay)
        | false, _ ->
            // Default to 1 hour if no configuration exists
            currentTime.Add(TimeSpan.FromHours(1.0)) 