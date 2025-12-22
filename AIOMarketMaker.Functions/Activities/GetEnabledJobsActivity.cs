using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Functions.Functions;

namespace AIOMarketMaker.Functions.Activities;

public class GetEnabledJobsActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<GetEnabledJobsActivity> _logger;

    public GetEnabledJobsActivity(EtlDbContext dbContext, ILogger<GetEnabledJobsActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Activity that fetches all enabled scrape jobs from the database.
    /// </summary>
    [Function(nameof(GetEnabledJobsActivity))]
    public async Task<List<ScrapeJobInfo>> Run(
        [ActivityTrigger] object? input,
        FunctionContext context)
    {
        _logger.LogInformation("Fetching enabled scrape jobs");

        var jobs = await _dbContext.ScrapeJobs
            .Where(j => j.IsEnabled)
            .Select(j => new ScrapeJobInfo(j.Id, j.SearchTerm))
            .ToListAsync();

        _logger.LogInformation("Found {Count} enabled jobs", jobs.Count);

        return jobs;
    }
}
