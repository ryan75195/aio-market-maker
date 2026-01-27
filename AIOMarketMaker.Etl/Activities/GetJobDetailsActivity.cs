using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

public class GetJobDetailsActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<GetJobDetailsActivity> _logger;
    private readonly int _defaultLookbackDays;
    private readonly int? _maxListingsToFetch;

    public GetJobDetailsActivity(
        EtlDbContext dbContext,
        IConfiguration configuration,
        ILogger<GetJobDetailsActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
        _defaultLookbackDays = configuration.GetValue<int>("Scraping:DefaultLookbackDays", 90);
        _maxListingsToFetch = configuration.GetValue<int?>("Scraping:MaxListingsToFetch");
    }

    [Function(nameof(GetJobDetailsActivity))]
    public async Task<JobDetails?> Run(
        [ActivityTrigger] GetJobDetailsInput input,
        FunctionContext context)
    {
        _logger.LogInformation("Getting details for job {JobId}", input.JobId);

        var job = await _dbContext.ScrapeJobs.FindAsync(input.JobId);
        if (job == null)
        {
            _logger.LogWarning("Job {JobId} not found", input.JobId);
            return null;
        }

        // Calculate lookback days based on last run, or use runtime override
        int lookbackDays;
        if (input.LookbackDays.HasValue)
        {
            lookbackDays = input.LookbackDays.Value;
            _logger.LogInformation("Job {JobId}: Using runtime lookback override: {Days} days", input.JobId, lookbackDays);
        }
        else if (job.LastRunUtc == null)
        {
            lookbackDays = _defaultLookbackDays;
        }
        else
        {
            var daysSinceLastRun = (int)Math.Ceiling((DateTime.UtcNow - job.LastRunUtc.Value).TotalDays);
            lookbackDays = Math.Max(1, daysSinceLastRun + 1);
        }

        // Use runtime override for max listings if provided, otherwise fall back to config
        var maxListings = input.MaxListingsToFetch ?? _maxListingsToFetch;
        if (input.MaxListingsToFetch.HasValue)
        {
            _logger.LogInformation("Job {JobId}: Using runtime maxListings override: {Max}", input.JobId, maxListings);
        }

        return new JobDetails(job.Id, job.SearchTerm, lookbackDays, maxListings);
    }
}
