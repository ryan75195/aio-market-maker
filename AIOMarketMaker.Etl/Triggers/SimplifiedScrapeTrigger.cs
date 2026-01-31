using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Azure.Storage.Queues;
using AngleSharp;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using ScraperWorker.Services;

namespace AIOMarketMaker.Etl.Triggers;

public class SimplifiedScrapeTrigger
{
    private readonly ILogger<SimplifiedScrapeTrigger> _logger;
    private readonly EtlDbContext _dbContext;
    private readonly IWebscraperClient _webscraperClient;
    private readonly ISearchParser _searchParser;
    private readonly QueueClient _queueClient;
    private readonly IEbayUrlBuilder _urlBuilder;

    public SimplifiedScrapeTrigger(
        ILogger<SimplifiedScrapeTrigger> logger,
        EtlDbContext dbContext,
        IWebscraperClient webscraperClient,
        ISearchParser searchParser,
        QueueServiceClient queueService)
    {
        _logger = logger;
        _dbContext = dbContext;
        _webscraperClient = webscraperClient;
        _searchParser = searchParser;
        _queueClient = queueService.GetQueueClient("scrape-work");
        _urlBuilder = new EbayUrlBuilder();
    }

    /// <summary>
    /// Run a scrape for a specific job. Creates a ScrapeRun, fetches search pages,
    /// parses listings, filters out existing ones, and enqueues work for new listings.
    /// </summary>
    /// <param name="jobId">The job ID to scrape for</param>
    /// <param name="searchTerm">The search term to use</param>
    /// <param name="triggerType">How the scrape was triggered (Manual, Nightly)</param>
    /// <returns>The count of new listings enqueued for processing</returns>
    public async Task<int> RunScrapeForJobAsync(int jobId, string searchTerm, string triggerType)
    {
        _logger.LogInformation("Starting scrape for job {JobId} with search term '{SearchTerm}'", jobId, searchTerm);

        // 1. Create a ScrapeRun
        var scrapeRun = new ScrapeRun
        {
            JobId = jobId,
            Status = "Searching",
            TriggerType = triggerType,
            StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created ScrapeRun {ScrapeRunId} for job {JobId}", scrapeRun.Id, jobId);

        // 2. Build search URL and fetch HTML
        var searchUrl = _urlBuilder.BuildSearchUrl(searchTerm, sold: false, page: 1, Condition.NULL, BuyingFormat.BUY_NOW);
        var html = await _webscraperClient.GetPageHtmlAsync(searchUrl);

        _logger.LogInformation("Fetched search page HTML ({Bytes} bytes)", html.Length);

        // 3. Parse HTML with AngleSharp
        var browsingContext = BrowsingContext.New(Configuration.Default);
        var document = await browsingContext.OpenAsync(request => request.Content(html));

        // 4. Parse search results to get listing IDs
        var products = _searchParser.ParseSearchResults(document);
        var allListingIds = products
            .Where(p => !string.IsNullOrEmpty(p.ListingId))
            .Select(p => p.ListingId!)
            .ToList();

        _logger.LogInformation("Parsed {Count} listings from search results", allListingIds.Count);

        // 5. Filter out existing listings for this job
        var existingListingIdsList = await _dbContext.Listings
            .Where(l => l.ScrapeJobId == jobId && allListingIds.Contains(l.ListingId))
            .Select(l => l.ListingId)
            .ToListAsync();
        var existingListingIds = existingListingIdsList.ToHashSet();

        var newListingIds = allListingIds
            .Where(id => !existingListingIds.Contains(id))
            .ToList();

        _logger.LogInformation("Filtered to {NewCount} new listings ({ExistingCount} already exist)",
            newListingIds.Count, existingListingIds.Count);

        // 6. Create ScrapeRunListing records for each new listing
        foreach (var listingId in newListingIds)
        {
            var scrapeRunListing = new ScrapeRunListing
            {
                ScrapeRunId = scrapeRun.Id,
                ScrapeJobId = jobId,
                ListingId = listingId,
                Status = "Pending",
                CreatedUtc = DateTime.UtcNow
            };
            _dbContext.ScrapeRunListings.Add(scrapeRunListing);
        }
        await _dbContext.SaveChangesAsync();

        // 7. Enqueue messages for each new listing
        foreach (var listingId in newListingIds)
        {
            var jobGuid = Guid.NewGuid().ToString("N");

            // Listing page message
            var listingMessage = new ScrapeQueueMessage
            {
                JobId = jobGuid,
                Url = _urlBuilder.BuildListingUrl(listingId),
                GroupId = listingId,
                FileKey = "listing",
                ScrapeRunId = scrapeRun.Id,
                ScrapeJobId = jobId,
                EnqueuedAt = DateTimeOffset.UtcNow
            };
            await _queueClient.SendMessageAsync(JsonSerializer.Serialize(listingMessage));

            // Description page message
            var descriptionMessage = new ScrapeQueueMessage
            {
                JobId = jobGuid,
                Url = _urlBuilder.BuildDescriptionUrl(listingId),
                GroupId = listingId,
                FileKey = "description",
                ScrapeRunId = scrapeRun.Id,
                ScrapeJobId = jobId,
                EnqueuedAt = DateTimeOffset.UtcNow
            };
            await _queueClient.SendMessageAsync(JsonSerializer.Serialize(descriptionMessage));
        }

        _logger.LogInformation("Enqueued {Count} listings for processing", newListingIds.Count);

        // 8. Update ScrapeRun status
        scrapeRun.Status = "Indexing";
        scrapeRun.CurrentPhase = "Indexing";
        scrapeRun.TotalListingsFound = newListingIds.Count;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Completed search phase for job {JobId}. Found {Count} new listings.",
            jobId, newListingIds.Count);

        return newListingIds.Count;
    }

    /// <summary>
    /// Timer trigger that runs nightly at 2 AM UTC to scrape all enabled jobs.
    /// </summary>
    [Function("NightlyScrape")]
    public async Task RunNightly([TimerTrigger("0 0 2 * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Nightly scrape trigger fired at {Time}", DateTime.UtcNow);

        // Get all enabled jobs
        var enabledJobs = await _dbContext.ScrapeJobs
            .Where(j => j.IsEnabled)
            .Select(j => new { j.Id, j.SearchTerm })
            .ToListAsync();

        if (enabledJobs.Count == 0)
        {
            _logger.LogInformation("No enabled jobs found for nightly scrape");
            return;
        }

        _logger.LogInformation("Found {Count} enabled jobs", enabledJobs.Count);

        // Run scrape for each enabled job
        foreach (var job in enabledJobs)
        {
            try
            {
                var listingsCount = await RunScrapeForJobAsync(job.Id, job.SearchTerm, "Nightly");
                _logger.LogInformation("Nightly scrape for job {JobId} completed. Found {Count} new listings.",
                    job.Id, listingsCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nightly scrape for job {JobId} failed: {Message}",
                    job.Id, ex.Message);
            }
        }

        _logger.LogInformation("Nightly scrape completed for {Count} jobs", enabledJobs.Count);
    }

    /// <summary>
    /// HTTP trigger for manual scrape invocation.
    /// If a jobId is provided in the request body, only that job is scraped.
    /// Otherwise, all enabled jobs are scraped.
    /// </summary>
    [Function("ManualScrape")]
    public async Task<HttpResponseData> RunManual(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "scrape/manual")] HttpRequestData req)
    {
        _logger.LogInformation("Manual scrape trigger fired at {Time}", DateTime.UtcNow);

        // Parse optional request body
        ManualScrapeRequest? scrapeRequest = null;
        var requestBody = await req.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(requestBody))
        {
            try
            {
                scrapeRequest = JsonSerializer.Deserialize<ManualScrapeRequest>(requestBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse request body, using defaults");
            }
        }

        // Determine which jobs to run
        IEnumerable<(int Id, string SearchTerm)> jobsToRun;

        if (scrapeRequest?.JobId != null)
        {
            // Run specific job
            var job = await _dbContext.ScrapeJobs
                .Where(j => j.Id == scrapeRequest.JobId)
                .Select(j => new { j.Id, j.SearchTerm })
                .FirstOrDefaultAsync();

            if (job == null)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteAsJsonAsync(new { error = $"Job {scrapeRequest.JobId} not found" });
                return notFoundResponse;
            }

            jobsToRun = new[] { (job.Id, job.SearchTerm) };
        }
        else
        {
            // Run all enabled jobs
            var enabledJobs = await _dbContext.ScrapeJobs
                .Where(j => j.IsEnabled)
                .Select(j => new { j.Id, j.SearchTerm })
                .ToListAsync();

            if (enabledJobs.Count == 0)
            {
                _logger.LogInformation("No enabled jobs found");
                var noJobsResponse = req.CreateResponse(HttpStatusCode.OK);
                await noJobsResponse.WriteAsJsonAsync(new { message = "No enabled jobs", results = Array.Empty<object>() });
                return noJobsResponse;
            }

            jobsToRun = enabledJobs.Select(j => (j.Id, j.SearchTerm));
        }

        // Run scrape for each job
        var results = new List<object>();
        foreach (var (jobId, searchTerm) in jobsToRun)
        {
            try
            {
                var listingsCount = await RunScrapeForJobAsync(jobId, searchTerm, "Manual");
                results.Add(new { jobId, searchTerm, success = true, listingsFound = listingsCount });
                _logger.LogInformation("Manual scrape for job {JobId} completed. Found {Count} new listings.",
                    jobId, listingsCount);
            }
            catch (Exception ex)
            {
                results.Add(new { jobId, searchTerm, success = false, error = ex.Message });
                _logger.LogError(ex, "Manual scrape for job {JobId} failed: {Message}",
                    jobId, ex.Message);
            }
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { results });
        return response;
    }
}

public record ManualScrapeRequest(int? JobId);
