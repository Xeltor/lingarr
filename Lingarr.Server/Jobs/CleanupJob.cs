﻿using Hangfire;
using Lingarr.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Jobs;

public class CleanupJob
{
    private readonly LingarrDbContext _dbContext;
    private readonly ILogger<CleanupJob> _logger;

    public CleanupJob(LingarrDbContext dbContext, 
        ILogger<CleanupJob> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task Execute()
    {
        var oneWeekAgo = DateTime.UtcNow.AddDays(-7);
        var oldJobs = await _dbContext.TranslationRequests
            .Where(pg => pg.CreatedAt < oneWeekAgo)
            .ToListAsync();

        foreach (var job in oldJobs)
        {
            _dbContext.TranslationRequests.Remove(job);
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation($"Removed {oldJobs.Count} translation requests that are older than a week.");
    }
}