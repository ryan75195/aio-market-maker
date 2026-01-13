using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Functions.Contracts;

namespace AIOMarketMaker.Functions.Activities;

public class ProcessJobActivity
{
    private readonly IJobRunner _jobRunner;
    private readonly ILogger<ProcessJobActivity> _logger;

    public ProcessJobActivity(IJobRunner jobRunner, ILogger<ProcessJobActivity> logger)
    {
        _jobRunner = jobRunner;
        _logger = logger;
    }

    /// <summary>
    /// Activity that processes a single scrape job.
    /// </summary>
    [Function(nameof(ProcessJobActivity))]
    public async Task<JobResult> Run(
        [ActivityTrigger] int jobId,
        FunctionContext context)
    {
        _logger.LogInformation("Processing job {JobId}", jobId);

        try
        {
            var result = await _jobRunner.RunJob(jobId);

            _logger.LogInformation(
                "Job {JobId} completed: Success={Success}, ListingsFound={ListingsFound}",
                jobId, result.Success, result.ListingsFound);

            return new JobResult(
                jobId,
                result.Success,
                result.ListingsFound,
                result.Error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed with exception", jobId);

            return new JobResult(
                jobId,
                Success: false,
                ListingsFound: 0,
                Error: ex.Message);
        }
    }
}
