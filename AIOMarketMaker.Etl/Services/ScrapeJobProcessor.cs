using System.Text.Json;
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
    Dictionary<string, Listing> ExistingListings,
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
    private readonly IListingIndexingService _indexingService;
    private readonly IComparablesRefreshService _comparablesRefreshService;

    public ScrapeJobProcessor(
        ILogger<ScrapeJobProcessor> logger,
        EtlDbContext dbContext,
        IWebscraperClient webscraperClient,
        ISearchParser searchParser,
        IEbayUrlBuilder urlBuilder,
        IListingIndexingService indexingService,
        IComparablesRefreshService comparablesRefreshService)
    {
        _logger = logger;
        _dbContext = dbContext;
        _webscraperClient = webscraperClient;
        _searchParser = searchParser;
        _urlBuilder = urlBuilder;
        _indexingService = indexingService;
        _comparablesRefreshService = comparablesRefreshService;
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
            await ExecuteScrape(scrapeRun, message.JobId, message.SearchTerm);

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

    private async Task ExecuteScrape(ScrapeRun scrapeRun, int jobId, string searchTerm)
    {
        const int maxPages = 100;

        var soldSummaries = await SearchSoldListings(scrapeRun, searchTerm, maxPages);
        var activeSummaries = await SearchActiveListings(scrapeRun, searchTerm, maxPages);
        var classified = await ClassifyListings(scrapeRun, activeSummaries, soldSummaries, jobId);

        if (classified.ToUpdateFromSummary.Count > 0)
        {
            await UpdateListingsFromSummary(scrapeRun, classified.ToUpdateFromSummary, classified.ExistingListings);
        }

        if (classified.ToScrape.Count > 0)
        {
            await CreateListingsAndEnqueueDescriptions(scrapeRun, classified.ToScrape, classified.ExistingListings, jobId);
        }

        await RefreshComparables(scrapeRun, jobId);

        if (classified.ToScrape.Count == 0)
        {
            await MarkCompleted(scrapeRun);
        }
    }

    private async Task<List<IEbayProductSummary>> SearchSoldListings(
        ScrapeRun scrapeRun, string searchTerm, int maxPages)
    {
        await SetPhase(scrapeRun, "Searching Sold", status: "Searching");
        var summaries = await SearchListings(searchTerm, sold: true, maxPages);
        _logger.LogInformation("Sold search complete: {Count} unique sold listings", summaries.Count);
        return summaries;
    }

    private async Task<List<IEbayProductSummary>> SearchActiveListings(
        ScrapeRun scrapeRun, string searchTerm, int maxPages)
    {
        await SetPhase(scrapeRun, "Searching Active");
        var summaries = await SearchListings(searchTerm, sold: false, maxPages);
        _logger.LogInformation("Active search complete: {Count} unique active listings", summaries.Count);
        return summaries;
    }

    private async Task<ClassifiedListings> ClassifyListings(
        ScrapeRun scrapeRun, List<IEbayProductSummary> activeSummaries,
        List<IEbayProductSummary> soldSummaries, int jobId)
    {
        await SetPhase(scrapeRun, "Classifying");

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
                toScrape.Add(summary);
            else if (TerminalStatuses.Contains(existing.ListingStatus ?? ""))
                terminalCount++;
            else if (summary.IsSold)
                toScrape.Add(summary);
            else
                toUpdate.Add(summary);
        }

        _logger.LogInformation(
            "Classified {Total} listings: {ScrapeCount} to scrape, {UpdateCount} to update from summary, {TerminalCount} terminal",
            merged.Count, toScrape.Count, toUpdate.Count, terminalCount);

        scrapeRun.TotalListingsFound = merged.Count;
        scrapeRun.ListingsFilteredPreQueue = terminalCount;

        return new ClassifiedListings(toScrape, toUpdate, existingListings, merged.Count, terminalCount);
    }

    private async Task UpdateListingsFromSummary(
        ScrapeRun scrapeRun, List<IEbayProductSummary> summaries,
        Dictionary<string, Listing> existingListings)
    {
        await SetPhase(scrapeRun, "Updating from summary");
        var updatedListings = new List<Listing>();

        foreach (var summary in summaries)
        {
            if (string.IsNullOrEmpty(summary.ListingId)) continue;
            if (!existingListings.TryGetValue(summary.ListingId, out var listing)) continue;

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
                updatedListings.Add(listing);
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

        foreach (var listing in updatedListings)
        {
            await _indexingService.Index(listing, isNew: false);
        }

        _logger.LogInformation("Updated {Count} listings from summary data", summaries.Count);
    }

    private async Task CreateListingsAndEnqueueDescriptions(
        ScrapeRun scrapeRun, List<IEbayProductSummary> summaries,
        Dictionary<string, Listing> existingListings, int jobId)
    {
        await SetPhase(scrapeRun, "Indexing", status: "Indexing");

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

            var newStatus = summary.IsSold ? "Sold" : "Active";
            var images = summary.Images != null ? JsonSerializer.Serialize(summary.Images) : null;
            var concrete = summary as EbayProductSummary;

            if (existingListings.TryGetValue(summary.ListingId, out var existing))
            {
                var oldStatus = existing.ListingStatus;
                existing.Title = summary.Title;
                existing.Price = summary.Price;
                existing.Currency = summary.Currency;
                existing.ShippingCost = summary.ShippingCost;
                existing.Url = summary.Url;
                existing.Condition = concrete?.Condition?.ToString();
                existing.ListingStatus = newStatus;
                existing.PurchaseFormat = concrete?.BuyingFormat?.ToString();
                existing.Images = images;
                existing.EndDateUtc = concrete?.EndDateUtc;
                existing.DescriptionStatus = "pending";
                existing.UpdatedUtc = DateTime.UtcNow;

                if (oldStatus != newStatus)
                {
                    _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                    {
                        ListingId = existing.Id,
                        ListingStatus = newStatus,
                        Price = summary.Price,
                        RecordedUtc = DateTime.UtcNow,
                        Source = "StatusUpdate"
                    });
                }
            }
            else
            {
                var listing = new Listing
                {
                    ListingId = summary.ListingId,
                    ScrapeJobId = jobId,
                    Title = summary.Title,
                    Price = summary.Price,
                    Currency = summary.Currency,
                    ShippingCost = summary.ShippingCost,
                    Url = summary.Url,
                    Condition = concrete?.Condition?.ToString(),
                    ListingStatus = newStatus,
                    PurchaseFormat = concrete?.BuyingFormat?.ToString(),
                    Images = images,
                    EndDateUtc = concrete?.EndDateUtc,
                    DescriptionStatus = "pending",
                    CreatedUtc = DateTime.UtcNow
                };
                _dbContext.Listings.Add(listing);
            }
        }
        await _dbContext.SaveChangesAsync();

        // Create initial status history for new listings (need IDs from SaveChanges)
        foreach (var summary in summaries)
        {
            if (string.IsNullOrEmpty(summary.ListingId)) continue;
            if (existingListings.ContainsKey(summary.ListingId)) continue;

            var listing = await _dbContext.Listings
                .FirstAsync(l => l.ListingId == summary.ListingId && l.ScrapeJobId == jobId);

            _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
            {
                ListingId = listing.Id,
                ListingStatus = summary.IsSold ? "Sold" : "Active",
                Price = summary.Price,
                RecordedUtc = DateTime.UtcNow,
                Source = "InitialScrape"
            });
        }
        await _dbContext.SaveChangesAsync();

        var workItems = summaries
            .Where(s => !string.IsNullOrEmpty(s.ListingId))
            .Select(s => new ScrapeWorkItem(
                s.ListingId!,
                _urlBuilder.BuildDescriptionUrl(s.ListingId!)));

        await _webscraperClient.EnqueueScrapeWork(workItems, scrapeRun.Id, jobId);

        _logger.LogInformation("Created {Count} listings and enqueued descriptions for job {JobId}.",
            summaries.Count, jobId);
    }

    private async Task SetPhase(ScrapeRun scrapeRun, string phase, string? status = null)
    {
        if (status != null)
            scrapeRun.Status = status;
        scrapeRun.CurrentPhase = phase;
        await _dbContext.SaveChangesAsync();
    }

    private async Task MarkCompleted(ScrapeRun scrapeRun)
    {
        scrapeRun.Status = "Completed";
        scrapeRun.CurrentPhase = "Completed";
        scrapeRun.CompletedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
    }

    private async Task RefreshComparables(ScrapeRun scrapeRun, int jobId)
    {
        await SetPhase(scrapeRun, "Refreshing comparables");

        var activeListings = await _dbContext.Listings
            .Where(l => l.ScrapeJobId == jobId && l.ListingStatus == "Active")
            .ToListAsync();

        if (activeListings.Count == 0)
        {
            _logger.LogInformation("No active listings to refresh comparables for job {JobId}", jobId);
            return;
        }

        var result = await _comparablesRefreshService.Refresh(activeListings);
        _logger.LogInformation(
            "Refreshed comparables for job {JobId}: {Processed} listings, {Found} comparables",
            jobId, result.ListingsProcessed, result.ComparablesFound);
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
