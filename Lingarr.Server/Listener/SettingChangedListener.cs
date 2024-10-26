﻿using Hangfire;
using Lingarr.Core.Data;
using Lingarr.Server.Hubs;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Jobs;
using Lingarr.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Listener;

public class SettingChangedListener
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<SettingUpdatesHub> _hubContext;
    private readonly ILogger<SettingChangedListener> _logger;

    public SettingChangedListener(IServiceProvider serviceProvider, 
        IHubContext<SettingUpdatesHub> hubContext,
        ILogger<SettingChangedListener> logger)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _logger = logger;
    }
    
    public async void OnSettingChanged(SettingService ss, string setting)
    {
        var settingGroups = new Dictionary<string, (string actionType, string actionName, string[] keys)>
        {
            { "radarr", ("Job", "Radarr", ["radarr_api_key", "radarr_url"]) },
            { "sonarr", ("Job", "Sonarr", ["sonarr_api_key", "sonarr_url"]) },
            { "automation", ("Job", "Automation", [
                    "automation_enabled", 
                    "translation_schedule", 
                    "max_translations_per_run"
                ])
            },
            { "source_languages", ("Action", "ClearHash", ["source_languages"]) }
        };

        foreach (var group in settingGroups)
        {
            if (group.Value.keys.Contains(setting))
            {
                switch (group.Value.actionType)
                {
                    case "Job":
                        await RunJob(group.Value.actionName, group.Value.keys);
                        break;
                    case "Action":
                        await RunAction(group.Value.actionName, group.Value.keys);
                        break;
                }
                break;
            }
        }
    }
    
    /// <summary>
    /// This method retrieves the required settings from the database. If all required settings have non-empty values,
    /// it enqueues the appropriate background job based on the <paramref name="jobName"/>:
    /// /// </summary>
    /// <param name="jobName">The name of the job to run.</param>
    /// <param name="requiredKeys">An array of setting keys that must have values in the database.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task RunJob(string jobName, string[] requiredKeys)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var settingService = scope.ServiceProvider.GetRequiredService<ISettingService>();
        
        var settings = await dbContext.Settings
            .Where(s => requiredKeys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);
        
        bool allRequiredKeysHaveValues = requiredKeys.All(key => 
            settings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value));
        
        if (allRequiredKeysHaveValues)
        {
            switch (jobName)
            {
                case "Radarr":
                    _logger.LogInformation($"Settings changed for |Green|{jobName}|/Green|. All settings are complete, |Orange|indexing media...|/Orange|");
                    
                    await _hubContext.Clients.Group("SettingUpdates").SendAsync("SettingUpdate", new
                    {
                        Key = "radarr_settings_completed",
                        Value = "true"
                    });
                    
                    await settingService.SetSetting("radarr_settings_completed", "true");
                    BackgroundJob.Schedule<GetMovieJob>("movies",
                        job => job.Execute(JobCancellationToken.Null),
                        TimeSpan.FromMinutes(1));
                    break;
                case "Sonarr":
                    _logger.LogInformation($"Settings changed for |Green|{jobName}|/Green|. All settings are complete, |Orange|indexing media...|/Orange|");
                    
                    await _hubContext.Clients.Group("SettingUpdates").SendAsync("SettingUpdate", new
                    {
                        Key = "sonarr_settings_completed",
                        Value = "true"
                    });
                    
                    await settingService.SetSetting("sonarr_settings_completed", "true");
                    BackgroundJob.Schedule<GetShowJob>("shows",
                        job => job.Execute(JobCancellationToken.Null),
                        TimeSpan.FromMinutes(1));
                    break;
                case "Automation":
                    _logger.LogInformation(
                        $"Settings changed for |Green|{jobName}|/Green|. Automation has been |Orange|modified|/Orange|.");
                    if (settings["automation_enabled"] == "true")
                    {
                        var translationSchedule = await settingService.GetSetting("translation_schedule");
                        RecurringJob.AddOrUpdate<AutomatedTranslationJob>(
                            "AutomatedTranslationJob",
                            "default",
                            job => job.Execute(),
                            translationSchedule);
                    }
                    else
                    {
                        RecurringJob.RemoveIfExists("AutomatedTranslationJob");
                    }

                    break;
            }
        }
    }
    
    /// <summary>
    /// This method retrieves the required settings from the database. If all required settings have non-empty values,
    /// it performs an action based on the <paramref name="actionName"/>:
    /// /// </summary>
    /// <param name="actionName">The name of the action to run.</param>
    /// <param name="requiredKeys">An array of setting keys that must have values in the database.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task RunAction(string actionName, string[] requiredKeys)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        
        var settings = await dbContext.Settings
            .Where(s => requiredKeys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);
        
        bool allRequiredKeysHaveValues = requiredKeys.All(key => 
            settings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value));
        
        if (allRequiredKeysHaveValues)
        {
            switch (actionName)
            {
                case "ClearHash":
                    dbContext.Database.ExecuteSqlRaw("UPDATE movies SET media_hash = ''");
                    dbContext.Database.ExecuteSqlRaw("UPDATE episodes SET media_hash = ''");
                    break;
            }
        }
    }
}