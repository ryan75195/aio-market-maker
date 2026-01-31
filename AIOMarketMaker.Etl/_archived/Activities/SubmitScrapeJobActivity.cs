using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

/// <summary>
/// Submits a URL to the scraper API and returns the job ID immediately.
/// This activity does NOT wait for the scrape to complete.
/// </summary>
public class SubmitScrapeJobActivity
{
    private readonly IWebscraperClient _webScraper;
    private readonly ILogger<SubmitScrapeJobActivity> _logger;

    public SubmitScrapeJobActivity(
        IWebscraperClient webScraper,
        ILogger<SubmitScrapeJobActivity> logger)
    {
        _webScraper = webScraper;
        _logger = logger;
    }

    [Function(nameof(SubmitScrapeJobActivity))]
    public async Task<string> Run(
        [ActivityTrigger] SubmitScrapeJobInput input,
        FunctionContext context)
    {
        _logger.LogInformation("SubmitScrapeJobActivity: Starting for URL: {Url} (GroupId={GroupId}, FileKey={FileKey})",
            input.Url, input.GroupId, input.FileKey);

        try
        {
            var response = await _webScraper.NewJobAsync(
                new[] { input.Url },
                groupId: input.GroupId,
                fileKey: input.FileKey);
            _logger.LogInformation("SubmitScrapeJobActivity: Job {JobId} created for URL: {Url}", response.JobId, input.Url);
            return response.JobId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubmitScrapeJobActivity: Failed to submit job for URL: {Url}", input.Url);
            throw;
        }
    }
}
