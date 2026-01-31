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

    public GetJobDetailsActivity(
        EtlDbContext dbContext,
        IConfiguration configuration,
        ILogger<GetJobDetailsActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
        _defaultLookbackDays = configuration.GetValue<int>("Scraping:DefaultLookbackDays", 90);
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

        // Calculate lookback days: if > 0 use runtime override, otherwise calculate dynamically
        int lookbackDays;
        if (input.LookbackDays > 0)
        {
            lookbackDays = input.LookbackDays.Value;
            _logger.LogInformation("Job {JobId}: Using lookback override: {Days} days", input.JobId, lookbackDays);
        }
        else if (job.LastRunUtc == null)
        {
            // First run ever - use config default
            lookbackDays = _defaultLookbackDays;
            _logger.LogInformation("Job {JobId}: First run, using default lookback: {Days} days", input.JobId, lookbackDays);
        }
        else
        {
            // Dynamic: days since last run + 1 buffer
            var daysSinceLastRun = (int)Math.Ceiling((DateTime.UtcNow - job.LastRunUtc.Value).TotalDays);
            lookbackDays = Math.Max(1, daysSinceLastRun + 1);
            _logger.LogInformation("Job {JobId}: Dynamic lookback: {Days} days (last run: {LastRun})",
                input.JobId, lookbackDays, job.LastRunUtc.Value);
        }

        // Use runtime value if provided and > 0, otherwise no limit (0 means unlimited)
        var maxSold = input.MaxSoldListings > 0 ? input.MaxSoldListings : null;
        var maxActive = input.MaxActiveListings > 0 ? input.MaxActiveListings : null;
        if (maxSold.HasValue)
        {
            _logger.LogInformation("Job {JobId}: Limiting sold listings to {Max}", input.JobId, maxSold);
        }
        if (maxActive.HasValue)
        {
            _logger.LogInformation("Job {JobId}: Limiting active listings to {Max}", input.JobId, maxActive);
        }

        return new JobDetails(job.Id, job.SearchTerm, lookbackDays, maxSold, maxActive);
    }
}
