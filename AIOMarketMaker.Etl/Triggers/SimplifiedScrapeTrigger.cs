using System.Net;
using System.Text;
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
using AIOMarketMaker.Etl.Models;
using ScraperWorker.Services;

namespace AIOMarketMaker.Etl.Triggers;

public class SimplifiedScrapeTrigger
{
    private readonly ILogger<SimplifiedScrapeTrigger> _logger;
    private readonly EtlDbContext _dbContext;
    private readonly IWebscraperClient _webscraperClient;
    private readonly ISearchParser _searchParser;
    private readonly QueueClient _queueClient;
    private readonly QueueClient _jobQueueClient;
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
        _jobQueueClient = queueService.GetQueueClient("scrape-jobs");
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

        const int maxPages = 100;

        // Phase 1: Search Sold Listings
        scrapeRun.CurrentPhase = "Searching Sold";
        await _dbContext.SaveChangesAsync();

        var soldListingIds = new HashSet<string>();
        var page = 1;

        while (page <= maxPages)
        {
            var soldUrl = _urlBuilder.BuildSearchUrl(searchTerm, sold: true, page: page, Condition.NULL, BuyingFormat.BUY_NOW);
            var html = await _webscraperClient.GetPageHtmlAsync(soldUrl);

            _logger.LogInformation("Fetched sold page {Page} ({Bytes} bytes)", page, html.Length);

            var browsingContext = BrowsingContext.New(Configuration.Default);
            var document = await browsingContext.OpenAsync(request => request.Content(html));

            var products = _searchParser.ParseSearchResults(document);
            var pageListingIds = products
                .Where(p => !string.IsNullOrEmpty(p.ListingId))
                .Select(p => p.ListingId!)
                .ToList();

            if (pageListingIds.Count == 0)
                break;

            foreach (var id in pageListingIds)
                soldListingIds.Add(id);

            page++;
        }

        _logger.LogInformation("Sold search complete: {PageCount} pages, {Count} unique sold listings", page - 1, soldListingIds.Count);

        // Phase 2: Search Active Listings
        scrapeRun.CurrentPhase = "Searching Active";
        await _dbContext.SaveChangesAsync();

        var allListingIds = new HashSet<string>();
        page = 1;

        while (page <= maxPages)
        {
            var searchUrl = _urlBuilder.BuildSearchUrl(searchTerm, sold: false, page: page, Condition.NULL, BuyingFormat.BUY_NOW);
            var html = await _webscraperClient.GetPageHtmlAsync(searchUrl);

            _logger.LogInformation("Fetched active page {Page} ({Bytes} bytes)", page, html.Length);

            var browsingContext = BrowsingContext.New(Configuration.Default);
            var document = await browsingContext.OpenAsync(request => request.Content(html));

            var products = _searchParser.ParseSearchResults(document);
            var pageListingIds = products
                .Where(p => !string.IsNullOrEmpty(p.ListingId))
                .Select(p => p.ListingId!)
                .ToList();

            if (pageListingIds.Count == 0)
                break; // No more results

            foreach (var id in pageListingIds)
                allListingIds.Add(id);

            page++;
        }

        _logger.LogInformation("Active search complete: {PageCount} pages, {Count} unique active listings", page - 1, allListingIds.Count);

        // Phase 3: Detect Active→Sold transitions
        scrapeRun.CurrentPhase = "Detecting Transitions";
        await _dbContext.SaveChangesAsync();

        // Find listings that are marked Active in DB but appeared in sold search
        var activeToSoldListings = await _dbContext.Listings
            .Where(l => l.ScrapeJobId == jobId
                     && soldListingIds.Contains(l.ListingId)
                     && l.ListingStatus == "Active")
            .Select(l => l.ListingId)
            .ToListAsync();

        _logger.LogInformation("Found {Count} listings that transitioned from Active to Sold", activeToSoldListings.Count);

        // Include sold listings in the processing queue (both new sold listings and Active→Sold transitions)
        foreach (var id in soldListingIds)
            allListingIds.Add(id);

        // 5. Filter out listings with terminal statuses (Sold, Ended, OutOfStock)
        // Active listings should be re-scraped to capture price/status changes
        var terminalStatuses = new HashSet<string> { "Sold", "Ended", "OutOfStock" };
        var terminalListingIdsList = await _dbContext.Listings
            .Where(l => l.ScrapeJobId == jobId
                     && allListingIds.Contains(l.ListingId)
                     && l.ListingStatus != null
                     && terminalStatuses.Contains(l.ListingStatus))
            .Select(l => l.ListingId)
            .ToListAsync();
        var existingListingIds = terminalListingIdsList.ToHashSet();

        var newListingIds = allListingIds
            .Where(id => !existingListingIds.Contains(id))
            .ToList();

        _logger.LogInformation("Filtered to {NewCount} listings to process ({TerminalCount} have terminal status)",
            newListingIds.Count, existingListingIds.Count);

        // 6. Update ScrapeRun status BEFORE enqueuing (so UI shows correct state)
        scrapeRun.TotalListingsFound = newListingIds.Count;
        if (newListingIds.Count == 0)
        {
            scrapeRun.Status = "Completed";
            scrapeRun.CurrentPhase = "Completed";
            scrapeRun.CompletedUtc = DateTime.UtcNow;
            _logger.LogInformation("No new listings found for job {JobId} - marking as completed", jobId);
            await _dbContext.SaveChangesAsync();
            return 0;
        }
        scrapeRun.Status = "Indexing";
        scrapeRun.CurrentPhase = "Indexing";
        await _dbContext.SaveChangesAsync();

        // 7. Create ScrapeRunListing records for each new listing
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

        // 8. Enqueue messages for each new listing
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
            // Base64 encode for compatibility with AzureStorageQueueService.DequeueAsync
            var listingJson = JsonSerializer.Serialize(listingMessage);
            var listingBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(listingJson));
            await _queueClient.SendMessageAsync(listingBase64);

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
            // Base64 encode for compatibility with AzureStorageQueueService.DequeueAsync
            var descriptionJson = JsonSerializer.Serialize(descriptionMessage);
            var descriptionBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(descriptionJson));
            await _queueClient.SendMessageAsync(descriptionBase64);
        }

        _logger.LogInformation("Enqueued {Count} listings for processing. Search phase complete for job {JobId}.",
            newListingIds.Count, jobId);

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
    /// Creates ScrapeRun records and enqueues jobs for background processing.
    /// Returns immediately with run IDs.
    /// </summary>
    [Function("ManualScrape")]
    public async Task<HttpResponseData> RunManual(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "scrape/start")] HttpRequestData req)
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

        // Create ScrapeRuns and enqueue jobs (fire-and-forget)
        var results = new List<object>();
        int? firstRunId = null;
        string? firstInstanceId = null;

        foreach (var (jobId, searchTerm) in jobsToRun)
        {
            // Create ScrapeRun with Queued status
            var scrapeRun = new ScrapeRun
            {
                JobId = jobId,
                Status = "Queued",
                CurrentPhase = "Queued",
                TriggerType = "Manual",
                StartedUtc = DateTime.UtcNow,
                InstanceId = Guid.NewGuid().ToString()
            };
            _dbContext.ScrapeRuns.Add(scrapeRun);
            await _dbContext.SaveChangesAsync();

            // Enqueue job message
            var message = new ScrapeJobMessage(scrapeRun.Id, jobId, searchTerm, "Manual");
            var messageJson = JsonSerializer.Serialize(message);
            await _jobQueueClient.SendMessageAsync(messageJson);

            _logger.LogInformation("Enqueued scrape job for {SearchTerm} (RunId: {RunId})", searchTerm, scrapeRun.Id);

            firstRunId ??= scrapeRun.Id;
            firstInstanceId ??= scrapeRun.InstanceId;

            results.Add(new { jobId, searchTerm, runId = scrapeRun.Id, status = "Queued" });
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            instanceId = firstInstanceId ?? Guid.NewGuid().ToString(),
            runId = firstRunId ?? 0,
            results
        });
        return response;
    }
}

public record ManualScrapeRequest(int? JobId);
