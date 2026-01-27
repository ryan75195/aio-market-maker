using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Models;
using ScraperWorker.Services;

namespace AIOMarketMaker.Etl.Activities;

/// <summary>
/// Fetches the scraped HTML from blob storage for a completed job.
/// Should only be called after CheckScrapeJobStatusActivity returns IsComplete=true.
/// </summary>
public class GetScrapedHtmlActivity
{
    private readonly IWebscraperClient _webScraper;
    private readonly IJobRepository _jobRepository;
    private readonly ILogger<GetScrapedHtmlActivity> _logger;

    public GetScrapedHtmlActivity(
        IWebscraperClient webScraper,
        IJobRepository jobRepository,
        ILogger<GetScrapedHtmlActivity> logger)
    {
        _webScraper = webScraper;
        _jobRepository = jobRepository;
        _logger = logger;
    }

    [Function(nameof(GetScrapedHtmlActivity))]
    public async Task<string?> Run(
        [ActivityTrigger] GetScrapedHtmlInput input,
        FunctionContext context)
    {
        _logger.LogInformation("Fetching HTML for job {JobId}", input.JobId);

        // Get results to find the URL (needed for blob lookup)
        var results = await _webScraper.GetResultsAsync(input.JobId);
        var firstResult = results.FirstOrDefault();

        if (firstResult == null || string.IsNullOrEmpty(firstResult.Url))
        {
            _logger.LogWarning("No results found for job {JobId}", input.JobId);
            return null;
        }

        // Fetch HTML from blob storage
        var html = await _jobRepository.GetFileContentsAsync(input.JobId, firstResult.Url, CancellationToken.None);

        if (string.IsNullOrEmpty(html))
        {
            _logger.LogWarning("Empty HTML content for job {JobId}", input.JobId);
            return null;
        }

        _logger.LogInformation("Retrieved {Length} chars of HTML for job {JobId}", html.Length, input.JobId);

        // Detect potential bot detection - different thresholds for different URL types:
        // - Description pages (itm.ebaydesc.com): legitimately small, 4-20KB typical
        // - Listing/search pages: typically 500KB+, consent/CAPTCHA pages under 100KB
        var isDescriptionUrl = firstResult.Url.Contains("ebaydesc.com", StringComparison.OrdinalIgnoreCase);
        var threshold = isDescriptionUrl ? 1_000 : 100_000; // 1KB for descriptions, 100KB for other pages

        if (html.Length < threshold)
        {
            _logger.LogWarning("Suspiciously small HTML ({Length} chars) for job {JobId} - possible bot detection",
                html.Length, input.JobId);
        }

        return html;
    }
}
