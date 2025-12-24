using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Functions.Functions;

namespace AIOMarketMaker.Functions.Activities;

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
        [ActivityTrigger] int jobId,
        FunctionContext context)
    {
        _logger.LogInformation("Getting details for job {JobId}", jobId);

        var job = await _dbContext.ScrapeJobs.FindAsync(jobId);
        if (job == null)
        {
            _logger.LogWarning("Job {JobId} not found", jobId);
            return null;
        }

        // Calculate lookback days based on last run
        int lookbackDays;
        if (job.LastRunUtc == null)
        {
            lookbackDays = _defaultLookbackDays;
        }
        else
        {
            var daysSinceLastRun = (int)Math.Ceiling((DateTime.UtcNow - job.LastRunUtc.Value).TotalDays);
            lookbackDays = Math.Max(1, daysSinceLastRun + 1);
        }

        return new JobDetails(job.Id, job.SearchTerm, lookbackDays);
    }
}
