using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
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

/// <summary>
/// Queue-triggered function that processes scrape jobs.
/// Each job runs independently - failures don't affect other jobs.
/// </summary>
public class ScrapeJobQueueTrigger
{
    private readonly ILogger<ScrapeJobQueueTrigger> _logger;
    private readonly EtlDbContext _dbContext;
    private readonly IWebscraperClient _webscraperClient;
    private readonly ISearchParser _searchParser;
    private readonly QueueClient _workQueueClient;
    private readonly IEbayUrlBuilder _urlBuilder;

    public ScrapeJobQueueTrigger(
        ILogger<ScrapeJobQueueTrigger> logger,
        EtlDbContext dbContext,
        IWebscraperClient webscraperClient,
        ISearchParser searchParser,
        QueueServiceClient queueService)
    {
        _logger = logger;
        _dbContext = dbContext;
        _webscraperClient = webscraperClient;
        _searchParser = searchParser;
        _workQueueClient = queueService.GetQueueClient("scrape-work");
        _workQueueClient.CreateIfNotExists();
        _urlBuilder = new EbayUrlBuilder();
    }

    [Function("ProcessScrapeJob")]
    public async Task ProcessJob(
        [QueueTrigger("scrape-jobs", Connection = "AzureWebJobsStorage")] string messageJson)
    {
        var message = JsonSerializer.Deserialize<ScrapeJobMessage>(messageJson);
        if (message == null)
        {
            _logger.LogError("Failed to deserialize queue message: {Message}", messageJson);
            return;
        }

        _logger.LogInformation("Processing scrape job: RunId={RunId}, JobId={JobId}, SearchTerm={SearchTerm}",
            message.ScrapeRunId, message.JobId, message.SearchTerm);

        var scrapeRun = await _dbContext.ScrapeRuns.FindAsync(message.ScrapeRunId);
        if (scrapeRun == null)
        {
            _logger.LogError("ScrapeRun {RunId} not found", message.ScrapeRunId);
            return;
        }

        try
        {
            scrapeRun.Status = "Searching";
            scrapeRun.CurrentPhase = "Searching Sold";
            await _dbContext.SaveChangesAsync();

            // Run the scrape logic
            await RunScrapeAsync(scrapeRun, message.JobId, message.SearchTerm);

            _logger.LogInformation("Scrape job completed: RunId={RunId}", message.ScrapeRunId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scrape job failed: RunId={RunId}", message.ScrapeRunId);
            scrapeRun.Status = "Failed";
            scrapeRun.ErrorMessage = ex.Message;
            scrapeRun.CompletedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            throw;
        }
    }

    private async Task RunScrapeAsync(ScrapeRun scrapeRun, int jobId, string searchTerm)
    {
        const int maxPages = 100;

        // Phase 1: Search Sold Listings
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

        // Phase 3: Detect Active->Sold transitions
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

        // Include sold listings in the processing queue (both new sold listings and Active->Sold transitions)
        foreach (var id in soldListingIds)
            allListingIds.Add(id);

        // Filter out listings with terminal statuses (Sold, Ended, OutOfStock)
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

        // Update ScrapeRun status BEFORE enqueuing (so UI shows correct state)
        // TotalListingsFound includes ALL listings found before filtering
        // ListingsFilteredPreQueue counts terminal status listings that won't be re-scraped
        // (ListingsSkipped is reserved for runtime skips like PRODUCT_PAGE, invalid transitions)
        scrapeRun.TotalListingsFound = allListingIds.Count;
        scrapeRun.ListingsFilteredPreQueue = existingListingIds.Count;
        if (newListingIds.Count == 0)
        {
            scrapeRun.Status = "Completed";
            scrapeRun.CurrentPhase = "Completed";
            scrapeRun.CompletedUtc = DateTime.UtcNow;
            _logger.LogInformation("No new listings found for job {JobId} - marking as completed", jobId);
            await _dbContext.SaveChangesAsync();
            return;
        }
        scrapeRun.Status = "Indexing";
        scrapeRun.CurrentPhase = "Indexing";
        await _dbContext.SaveChangesAsync();

        // Create ScrapeRunListing records for each new listing
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

        // Enqueue messages for each new listing
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
            await _workQueueClient.SendMessageAsync(listingBase64);

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
            await _workQueueClient.SendMessageAsync(descriptionBase64);
        }

        _logger.LogInformation("Enqueued {Count} listings for processing. Search phase complete for job {JobId}.",
            newListingIds.Count, jobId);
    }
}
