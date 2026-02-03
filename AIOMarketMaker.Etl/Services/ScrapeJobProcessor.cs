using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AngleSharp;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Models.Ebay;

namespace AIOMarketMaker.Etl.Services;

public interface IScrapeJobProcessor
{
    Task Process(ScrapeJobMessage message);
}

public record ClassifiedListings(
    List<IEbayProductSummary> ToScrape,
    List<IEbayProductSummary> ToUpdateFromSummary,
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

        var soldSummaries = await SearchListings(searchTerm, sold: true, maxPages);
        _logger.LogInformation("Sold search complete: {Count} unique sold listings", soldSummaries.Count);

        scrapeRun.CurrentPhase = "Searching Active";
        await _dbContext.SaveChangesAsync();

        var activeSummaries = await SearchListings(searchTerm, sold: false, maxPages);
        _logger.LogInformation("Active search complete: {Count} unique active listings", activeSummaries.Count);

        scrapeRun.CurrentPhase = "Classifying";
        await _dbContext.SaveChangesAsync();

        var classified = await ClassifyListings(activeSummaries, soldSummaries, jobId);

        scrapeRun.TotalListingsFound = classified.TotalFound;
        scrapeRun.ListingsFilteredPreQueue = classified.TerminalCount;

        if (classified.ToScrape.Count == 0 && classified.ToUpdateFromSummary.Count == 0)
        {
            scrapeRun.Status = "Completed";
            scrapeRun.CurrentPhase = "Completed";
            scrapeRun.CompletedUtc = DateTime.UtcNow;
            _logger.LogInformation("No new or changed listings found for job {JobId} - marking as completed", jobId);
            await _dbContext.SaveChangesAsync();
            return;
        }

        if (classified.ToUpdateFromSummary.Count > 0)
        {
            scrapeRun.CurrentPhase = "Updating from summary";
            await _dbContext.SaveChangesAsync();

            await UpdateListingsFromSummary(classified.ToUpdateFromSummary, scrapeRun, jobId);
            _logger.LogInformation("Updated {Count} listings from summary data", classified.ToUpdateFromSummary.Count);
        }

        if (classified.ToScrape.Count > 0)
        {
            scrapeRun.Status = "Indexing";
            scrapeRun.CurrentPhase = "Indexing";
            await _dbContext.SaveChangesAsync();

            await CreateAndEnqueueListings(classified.ToScrape, scrapeRun, jobId);
        }
        else
        {
            scrapeRun.Status = "Completed";
            scrapeRun.CurrentPhase = "Completed";
            scrapeRun.CompletedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }
    }

    private async Task<ClassifiedListings> ClassifyListings(
        List<IEbayProductSummary> activeSummaries, List<IEbayProductSummary> soldSummaries, int jobId)
    {
        // Merge — sold wins if listing appears in both
        var merged = new Dictionary<string, IEbayProductSummary>();
        foreach (var summary in soldSummaries.Concat(activeSummaries))
        {
            if (string.IsNullOrEmpty(summary.ListingId)) continue;
            merged.TryAdd(summary.ListingId, summary);
        }

        var allListingIds = merged.Keys.ToList();

        // Transition detection (logging only)
        var soldIds = soldSummaries
            .Where(s => !string.IsNullOrEmpty(s.ListingId))
            .Select(s => s.ListingId!)
            .ToHashSet();

        var transitionCount = await _dbContext.Listings
            .Where(l => l.ScrapeJobId == jobId
                     && soldIds.Contains(l.ListingId)
                     && l.ListingStatus == "Active")
            .CountAsync();

        _logger.LogInformation("Found {Count} listings that transitioned from Active to Sold", transitionCount);

        // Load existing listings
        var existingListings = await _dbContext.Listings
            .Where(l => l.ScrapeJobId == jobId && allListingIds.Contains(l.ListingId))
            .ToDictionaryAsync(l => l.ListingId);

        var toScrape = new List<IEbayProductSummary>();
        var toUpdate = new List<IEbayProductSummary>();
        var terminalCount = 0;

        foreach (var (listingId, summary) in merged)
        {
            if (!existingListings.TryGetValue(listingId, out var existing))
            {
                toScrape.Add(summary);  // New
            }
            else if (TerminalStatuses.Contains(existing.ListingStatus ?? ""))
            {
                terminalCount++;  // Terminal — skip
            }
            else if (summary.IsSold)
            {
                toScrape.Add(summary);  // Sold transition — full scrape
            }
            else
            {
                toUpdate.Add(summary);  // Existing active — summary update
            }
        }

        _logger.LogInformation(
            "Classified {Total} listings: {ScrapeCount} to scrape, {UpdateCount} to update from summary, {TerminalCount} terminal",
            merged.Count, toScrape.Count, toUpdate.Count, terminalCount);

        return new ClassifiedListings(toScrape, toUpdate, merged.Count, terminalCount);
    }

    private async Task UpdateListingsFromSummary(
        List<IEbayProductSummary> summaries, ScrapeRun scrapeRun, int jobId)
    {
        foreach (var summary in summaries)
        {
            var listing = await _dbContext.Listings
                .FirstOrDefaultAsync(l => l.ListingId == summary.ListingId && l.ScrapeJobId == jobId);

            if (listing == null) continue;

            var priceChanged = listing.Price != summary.Price;
            var shippingChanged = listing.ShippingCost != summary.ShippingCost;

            if (priceChanged || shippingChanged)
            {
                listing.Price = summary.Price;
                listing.ShippingCost = summary.ShippingCost;
                listing.UpdatedUtc = DateTime.UtcNow;

                if (priceChanged)
                {
                    _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                    {
                        ListingId = listing.Id,
                        ListingStatus = listing.ListingStatus ?? "Active",
                        Price = summary.Price,
                        RecordedUtc = DateTime.UtcNow,
                        Source = "SummaryUpdate"
                    });
                }

                scrapeRun.ListingsUpdated++;
                _logger.LogInformation("Updated listing {ListingId} from summary (price: {Price}, shipping: {Shipping})",
                    summary.ListingId, summary.Price, summary.ShippingCost);
            }
            else
            {
                scrapeRun.ListingsSkipped++;
            }

            scrapeRun.ListingsProcessed++;
        }

        await _dbContext.SaveChangesAsync();
    }

    private async Task CreateAndEnqueueListings(
        List<IEbayProductSummary> summaries, ScrapeRun scrapeRun, int jobId)
    {
        foreach (var summary in summaries)
        {
            if (string.IsNullOrEmpty(summary.ListingId)) continue;

            _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
            {
                ScrapeRunId = scrapeRun.Id,
                ScrapeJobId = jobId,
                ListingId = summary.ListingId,
                Status = "Pending",
                CreatedUtc = DateTime.UtcNow
            });
        }
        await _dbContext.SaveChangesAsync();

        var workItems = summaries
            .Where(s => !string.IsNullOrEmpty(s.ListingId))
            .Select(s => new ScrapeWorkItem(
                s.ListingId!,
                _urlBuilder.BuildListingUrl(s.ListingId!),
                _urlBuilder.BuildDescriptionUrl(s.ListingId!)));

        await _webscraperClient.EnqueueScrapeWork(workItems, scrapeRun.Id, jobId);

        _logger.LogInformation("Enqueued {Count} listings for processing. Search phase complete for job {JobId}.",
            summaries.Count, jobId);
    }

    private async Task<List<IEbayProductSummary>> SearchListings(string searchTerm, bool sold, int maxPages)
    {
        var results = new List<IEbayProductSummary>();
        var seenIds = new HashSet<string>();
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
            var pageResults = products
                .Where(p => !string.IsNullOrEmpty(p.ListingId) && seenIds.Add(p.ListingId!))
                .ToList();

            if (pageResults.Count == 0)
                break;

            results.AddRange(pageResults);
            page++;
        }

        return results;
    }
}
