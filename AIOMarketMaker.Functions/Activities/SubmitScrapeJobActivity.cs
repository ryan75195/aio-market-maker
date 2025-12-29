using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Functions.Activities;

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
        [ActivityTrigger] string url,
        FunctionContext context)
    {
        _logger.LogInformation("Submitting scrape job for URL: {Url}", url);

        var response = await _webScraper.NewJobAsync(new[] { url });

        _logger.LogInformation("Scrape job submitted: {JobId}", response.JobId);

        return response.JobId;
    }
}
