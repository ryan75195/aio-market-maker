using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AngleSharp;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Services;

public interface IScrapeJobProcessor
{
    Task Process(ScrapeJobMessage message);
}

public record FilterResult(
    List<string> NewListingIds,
    int TotalFound,
    int TerminalCount);

public class ScrapeJobProcessor : IScrapeJobProcessor
{
    private static readonly HashSet<string> TerminalStatuses = new() { "Sold", "Ended", "OutOfStock" };

    private readonly ILogger<ScrapeJobProcessor> _logger;
    private readonly EtlDbContext _dbContext;
    private readonly IWebscraperClient _webscraperClient;
    private readonly ISearchParser _searchParser;
    private readonly IEbayUrlBuilder _urlBuilder;

    public ScrapeJobProcessor(
        ILogger<ScrapeJobProcessor> logger,
        EtlDbContext dbContext,
        IWebscraperClient webscraperClient,
        ISearchParser searchParser,
        IEbayUrlBuilder urlBuilder)
    {
        _logger = logger;
        _dbContext = dbContext;
        _webscraperClient = webscraperClient;
        _searchParser = searchParser;
        _urlBuilder = urlBuilder;
    }

    public async Task Process(ScrapeJobMessage message)
    {
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

            await RunScrape(scrapeRun, message.JobId, message.SearchTerm);

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

    private async Task RunScrape(ScrapeRun scrapeRun, int jobId, string searchTerm)
    {
        const int maxPages = 100;

        var soldListingIds = await SearchListings(searchTerm, sold: true, maxPages);
        _logger.LogInformation("Sold search complete: {Count} unique sold listings", soldListingIds.Count);

        scrapeRun.CurrentPhase = "Searching Active";
        await _dbContext.SaveChangesAsync();

        var activeListingIds = await SearchListings(searchTerm, sold: false, maxPages);
        _logger.LogInformation("Active search complete: {Count} unique active listings", activeListingIds.Count);

        scrapeRun.CurrentPhase = "Detecting Transitions";
        await _dbContext.SaveChangesAsync();

        var filterResult = await FilterNewListings(activeListingIds, soldListingIds, jobId);

        scrapeRun.TotalListingsFound = filterResult.TotalFound;
        scrapeRun.ListingsFilteredPreQueue = filterResult.TerminalCount;

        if (filterResult.NewListingIds.Count == 0)
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

        await CreateAndEnqueueListings(filterResult.NewListingIds, scrapeRun, jobId);
    }

    private async Task<FilterResult> FilterNewListings(
        HashSet<string> activeListingIds, HashSet<string> soldListingIds, int jobId)
    {
        var transitionCount = await _dbContext.Listings
            .Where(l => l.ScrapeJobId == jobId
                     && soldListingIds.Contains(l.ListingId)
                     && l.ListingStatus == "Active")
            .CountAsync();

        _logger.LogInformation("Found {Count} listings that transitioned from Active to Sold", transitionCount);

        var allListingIds = new HashSet<string>(activeListingIds);
        foreach (var id in soldListingIds)
            allListingIds.Add(id);

        var terminalListingIds = (await _dbContext.Listings
            .Where(l => l.ScrapeJobId == jobId
                     && allListingIds.Contains(l.ListingId)
                     && l.ListingStatus != null
                     && TerminalStatuses.Contains(l.ListingStatus))
            .Select(l => l.ListingId)
            .ToListAsync())
            .ToHashSet();

        var newListingIds = allListingIds
            .Where(id => !terminalListingIds.Contains(id))
            .ToList();

        _logger.LogInformation("Filtered to {NewCount} listings to process ({TerminalCount} have terminal status)",
            newListingIds.Count, terminalListingIds.Count);

        return new FilterResult(newListingIds, allListingIds.Count, terminalListingIds.Count);
    }

    private async Task CreateAndEnqueueListings(
        List<string> newListingIds, ScrapeRun scrapeRun, int jobId)
    {
        foreach (var listingId in newListingIds)
        {
            _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
            {
                ScrapeRunId = scrapeRun.Id,
                ScrapeJobId = jobId,
                ListingId = listingId,
                Status = "Pending",
                CreatedUtc = DateTime.UtcNow
            });
        }
        await _dbContext.SaveChangesAsync();

        var workItems = newListingIds.Select(id => new ScrapeWorkItem(
            id,
            _urlBuilder.BuildListingUrl(id),
            _urlBuilder.BuildDescriptionUrl(id)));

        await _webscraperClient.EnqueueScrapeWork(workItems, scrapeRun.Id, jobId);

        _logger.LogInformation("Enqueued {Count} listings for processing. Search phase complete for job {JobId}.",
            newListingIds.Count, jobId);
    }

    private async Task<HashSet<string>> SearchListings(string searchTerm, bool sold, int maxPages)
    {
        var listingIds = new HashSet<string>();
        var page = 1;

        while (page <= maxPages)
        {
            var url = _urlBuilder.BuildSearchUrl(searchTerm, sold: sold, page: page, Condition.NULL, BuyingFormat.BUY_NOW);
            var html = await _webscraperClient.GetPageHtmlAsync(url);

            _logger.LogInformation("Fetched {Type} page {Page} ({Bytes} bytes)",
                sold ? "sold" : "active", page, html.Length);

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
                listingIds.Add(id);

            page++;
        }

        return listingIds;
    }
}
