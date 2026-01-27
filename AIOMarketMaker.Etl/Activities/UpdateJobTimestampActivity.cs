using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Etl.Activities;

public class UpdateJobTimestampActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<UpdateJobTimestampActivity> _logger;

    public UpdateJobTimestampActivity(
        EtlDbContext dbContext,
        ILogger<UpdateJobTimestampActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(UpdateJobTimestampActivity))]
    public async Task Run(
        [ActivityTrigger] int jobId,
        FunctionContext context)
    {
        _logger.LogInformation("Updating timestamp for job {JobId}", jobId);

        var job = await _dbContext.ScrapeJobs.FindAsync(jobId);
        if (job != null)
        {
            job.LastRunUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Updated job {JobId} timestamp to {Timestamp}", jobId, job.LastRunUtc);
        }
    }
}
