using System.Text.Json;
using System.Threading.Channels;
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
    Task<ScrapeRun> CreateRun(ScrapeJobConfig job, string triggerType, Guid? batchId = null);
    Task Execute(ScrapeRun run, ScrapeJobConfig job);
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
    private readonly IListingParser _listingParser;
    private readonly IEbayUrlBuilder _urlBuilder;
    private readonly IListingIndexingService _indexingService;
    private readonly DbWriteGate _dbWriteGate;

    public ScrapeJobProcessor(
        ILogger<ScrapeJobProcessor> logger,
        EtlDbContext dbContext,
        IWebscraperClient webscraperClient,
        ISearchParser searchParser,
        IListingParser listingParser,
        IEbayUrlBuilder urlBuilder,
        IListingIndexingService indexingService,
        DbWriteGate dbWriteGate)
    {
        _logger = logger;
        _dbContext = dbContext;
        _webscraperClient = webscraperClient;
        _searchParser = searchParser;
        _listingParser = listingParser;
        _urlBuilder = urlBuilder;
        _indexingService = indexingService;
        _dbWriteGate = dbWriteGate;
    }

    public async Task<ScrapeRun> CreateRun(ScrapeJobConfig job, string triggerType, Guid? batchId = null)
    {
        var scrapeRun = new ScrapeRun
        {
            JobId = job.Id,
            BatchId = batchId,
            Status = "Queued",
            CurrentPhase = "Queued",
            TriggerType = triggerType,
            StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();
        return scrapeRun;
    }

    public async Task Execute(ScrapeRun run, ScrapeJobConfig job)
    {
        _logger.LogInformation("Starting scrape: RunId={RunId}, JobId={JobId}, SearchTerm={SearchTerm}",
            run.Id, job.Id, job.SearchTerm);

        try
        {
            await ExecuteScrape(run, job.Id, job.SearchTerm);
            _logger.LogInformation("Scrape completed: RunId={RunId}", run.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scrape failed: RunId={RunId}", run.Id);
            run.Status = "Failed";
            run.ErrorMessage = ex.Message;
            run.CompletedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }
    }

    private async Task ExecuteScrape(ScrapeRun scrapeRun, int jobId, string searchTerm)
    {
        // Reset progress counters — ensures clean state on recovery restarts
        scrapeRun.ListingsProcessed = 0;
        scrapeRun.ListingsAddedActive = 0;
        scrapeRun.ListingsAddedSold = 0;
        scrapeRun.ListingsUpdated = 0;
        scrapeRun.ListingsSkipped = 0;
        scrapeRun.ListingsFailed = 0;
        scrapeRun.TotalListingsFound = 0;
        scrapeRun.ListingsFilteredPreQueue = 0;
        await _dbContext.SaveChangesAsync();

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
            await FetchAndProcessDescriptions(scrapeRun, classified.ToScrape, classified.ExistingListings, jobId);
        }

        // Always mark complete — no more "waiting for callbacks"
        await MarkCompleted(scrapeRun);
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
            if (string.IsNullOrEmpty(summary.ListingId)) { continue; }
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

        // Load existing listings globally (IX_Listings_ListingId is unique across all jobs)
        var existingListings = await _dbContext.Listings
            .Where(l => allListingIds.Contains(l.ListingId))
            .ToDictionaryAsync(l => l.ListingId);

        var toScrape = new List<IEbayProductSummary>();
        var toUpdate = new List<IEbayProductSummary>();
        var terminalCount = 0;
        var crossJobCount = 0;

        foreach (var (listingId, summary) in merged)
        {
            if (!existingListings.TryGetValue(listingId, out var existing))
            {
                toScrape.Add(summary);
            }
            else if (existing.ScrapeJobId != jobId)
            {
                crossJobCount++;
            }
            else if (TerminalStatuses.Contains(existing.ListingStatus ?? ""))
            {
                terminalCount++;
            }
            else if (summary.IsSold)
            {
                toScrape.Add(summary);
            }
            else
            {
                toUpdate.Add(summary);
            }
        }

        if (crossJobCount > 0)
        {
            _logger.LogInformation("Skipped {Count} listings already scraped by other jobs", crossJobCount);
        }

        _logger.LogInformation(
            "Classified {Total} listings: {ScrapeCount} to scrape, {UpdateCount} to update from summary, {TerminalCount} terminal",
            merged.Count, toScrape.Count, toUpdate.Count, terminalCount);

        scrapeRun.TotalListingsFound = merged.Count;
        scrapeRun.ListingsFilteredPreQueue = terminalCount + crossJobCount;

        return new ClassifiedListings(toScrape, toUpdate, existingListings, merged.Count, terminalCount + crossJobCount);
    }

    private async Task UpdateListingsFromSummary(
        ScrapeRun scrapeRun, List<IEbayProductSummary> summaries,
        Dictionary<string, Listing> existingListings)
    {
        await SetPhase(scrapeRun, "Updating from summary");
        var updatedListings = new List<Listing>();

        foreach (var summary in summaries)
        {
            if (string.IsNullOrEmpty(summary.ListingId)) { continue; }
            if (!existingListings.TryGetValue(summary.ListingId, out var listing)) { continue; }

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
            await _indexingService.Index(listing, embedContent: false);
        }

        _logger.LogInformation("Updated {Count} listings from summary data", summaries.Count);
    }

    private async Task FetchAndProcessDescriptions(
        ScrapeRun scrapeRun, List<IEbayProductSummary> summaries,
        Dictionary<string, Listing> existingListings, int jobId)
    {
        await SetPhase(scrapeRun, "Indexing", status: "Indexing");

        // Gate bulk inserts so only a few runs write to DB at once (prevents LocalDB contention)
        await _dbWriteGate.WaitAsync();
        try
        {
            await SaveListingsFromSummaries(scrapeRun, summaries, existingListings, jobId);
        }
        finally
        {
            _dbWriteGate.Release();
        }

        // Channel lets us process results as they arrive (DbContext is not thread-safe)
        var channel = Channel.CreateUnbounded<DescriptionFetchResult>();
        var concurrency = new SemaphoreSlim(15);
        // Producer: fetch descriptions concurrently, write results to channel
        var producerTask = Task.Run(async () =>
        {
            var fetchTasks = summaries
                .Where(s => !string.IsNullOrEmpty(s.ListingId))
                .Select(async summary =>
                {
                    await concurrency.WaitAsync();
                    string? html = null;
                    Exception? fetchError = null;
                    try
                    {
                        var descriptionUrl = _urlBuilder.BuildDescriptionUrl(summary.ListingId!);
                        html = await _webscraperClient.GetPageHtmlAsync(descriptionUrl);
                    }
                    catch (Exception ex)
                    {
                        fetchError = ex;
                    }
                    finally
                    {
                        concurrency.Release();
                    }

                    await channel.Writer.WriteAsync(new DescriptionFetchResult(summary, html, fetchError));
                });

            await Task.WhenAll(fetchTasks);
            channel.Writer.Complete();
        });

        // Consumer: process results sequentially as they arrive
        await foreach (var result in channel.Reader.ReadAllAsync())
        {
            await ProcessFetchedDescription(scrapeRun, result, jobId);
            scrapeRun.ListingsProcessed++;
        }

        await producerTask;
        await _dbContext.SaveChangesAsync();
    }

    private async Task SaveListingsFromSummaries(
        ScrapeRun scrapeRun, List<IEbayProductSummary> summaries,
        Dictionary<string, Listing> existingListings, int jobId)
    {
        var newListings = new List<(Listing Listing, IEbayProductSummary Summary)>();

        foreach (var summary in summaries)
        {
            if (string.IsNullOrEmpty(summary.ListingId)) { continue; }

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
                newListings.Add((listing, summary));
            }
        }
        await _dbContext.SaveChangesAsync();

        // EF Core sets listing.Id after SaveChangesAsync — no need to query them back
        foreach (var (listing, summary) in newListings)
        {
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

        _logger.LogInformation("Saved {Count} listings for job {JobId}", summaries.Count, jobId);
    }

    private async Task ProcessFetchedDescription(
        ScrapeRun scrapeRun, DescriptionFetchResult result, int jobId)
    {
        var summary = result.Summary;
        var listing = await _dbContext.Listings
            .FirstOrDefaultAsync(l => l.ListingId == summary.ListingId && l.ScrapeJobId == jobId);

        if (listing == null)
        {
            _logger.LogWarning("Listing {ListingId} not found for description processing", summary.ListingId);
            scrapeRun.ListingsFailed++;
            RecordIssue(scrapeRun, summary.ListingId!, "ListingNotFound", "DescriptionFetch",
                "Listing not found in database during description processing");
            await _dbContext.SaveChangesAsync();
            return;
        }

        if (result.Error != null)
        {
            _logger.LogWarning(result.Error, "Failed to fetch description for {ListingId}", summary.ListingId);
            listing.DescriptionStatus = "missing";
            scrapeRun.ListingsFailed++;
            RecordIssue(scrapeRun, summary.ListingId!, "DescriptionFetchFailed", "DescriptionFetch", result.Error);
            await _dbContext.SaveChangesAsync();
            return;
        }

        // Phase 1: Parse description
        string? description = null;
        try
        {
            if (result.Html != null)
            {
                var document = await ParseHtml(result.Html);
                description = _listingParser.ParseDescription(document);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse description for {ListingId}", summary.ListingId);
            listing.DescriptionStatus = "failed";
            scrapeRun.ListingsFailed++;
            RecordIssue(scrapeRun, summary.ListingId!, "ParseFailed", "Parse", ex);
            await _dbContext.SaveChangesAsync();
            return;
        }

        if (string.IsNullOrEmpty(description))
        {
            listing.DescriptionStatus = "missing";
        }
        else
        {
            listing.Description = description;
            listing.DescriptionStatus = "complete";
        }

        await _dbContext.SaveChangesAsync();

        // Phase 2: Embed and index
        if (listing.DescriptionStatus == "complete")
        {
            try
            {
                await _indexingService.Index(listing, embedContent: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index listing {ListingId}", summary.ListingId);
                scrapeRun.ListingsFailed++;
                RecordIssue(scrapeRun, summary.ListingId!, "IndexingFailed", "Indexing", ex);
                await _dbContext.SaveChangesAsync();
                return;
            }
        }

        if (summary.IsSold)
        {
            scrapeRun.ListingsAddedSold++;
        }
        else
        {
            scrapeRun.ListingsAddedActive++;
        }
    }

    private void RecordIssue(ScrapeRun scrapeRun, string listingId,
        string issueType, string phase, Exception ex)
    {
        int? httpStatus = (ex as HttpRequestException)?.StatusCode is { } code
            ? (int)code
            : null;

        _dbContext.ScrapeRunIssues.Add(new ScrapeRunIssue
        {
            ScrapeRunId = scrapeRun.Id,
            ListingId = listingId,
            IssueType = issueType,
            Phase = phase,
            ErrorMessage = ex.Message,
            StackTrace = ex.ToString(),
            HttpStatusCode = httpStatus,
            CreatedUtc = DateTime.UtcNow
        });
    }

    private void RecordIssue(ScrapeRun scrapeRun, string listingId,
        string issueType, string phase, string errorMessage)
    {
        _dbContext.ScrapeRunIssues.Add(new ScrapeRunIssue
        {
            ScrapeRunId = scrapeRun.Id,
            ListingId = listingId,
            IssueType = issueType,
            Phase = phase,
            ErrorMessage = errorMessage,
            CreatedUtc = DateTime.UtcNow
        });
    }

    private static async Task<AngleSharp.Dom.IDocument> ParseHtml(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        return await context.OpenAsync(req => req.Content(html));
    }

    private async Task SetPhase(ScrapeRun scrapeRun, string phase, string? status = null)
    {
        if (status != null)
        {
            scrapeRun.Status = status;
        }
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
            {
                break;
            }

            results.AddRange(pageResults);
            page++;
        }

        return results;
    }

    private record DescriptionFetchResult(
        IEbayProductSummary Summary, string? Html, Exception? Error);
}
