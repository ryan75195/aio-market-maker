using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

/// <summary>
/// Checks the status of a scrape job once and returns immediately.
/// This activity does NOT poll or wait.
/// </summary>
public class CheckScrapeJobStatusActivity
{
    private readonly IWebscraperClient _webScraper;
    private readonly ILogger<CheckScrapeJobStatusActivity> _logger;

    public CheckScrapeJobStatusActivity(
        IWebscraperClient webScraper,
        ILogger<CheckScrapeJobStatusActivity> logger)
    {
        _webScraper = webScraper;
        _logger = logger;
    }

    [Function(nameof(CheckScrapeJobStatusActivity))]
    public async Task<ScrapeJobStatusResult> Run(
        [ActivityTrigger] string jobId,
        FunctionContext context)
    {
        var status = await _webScraper.GetStatusAsync(jobId);

        if (status == null)
        {
            _logger.LogWarning("Job {JobId} not found", jobId);
            return new ScrapeJobStatusResult(jobId, "NotFound", false);
        }

        var statusStr = status.Status.ToString();
        var isComplete = statusStr == "Success" || statusStr == "Failure";

        _logger.LogDebug("Job {JobId} status: {Status}", jobId, statusStr);

        return new ScrapeJobStatusResult(jobId, statusStr, isComplete);
    }
}
