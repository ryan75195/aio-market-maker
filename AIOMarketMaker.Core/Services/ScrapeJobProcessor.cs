using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AngleSharp;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Models.Ebay;

namespace AIOMarketMaker.Core.Services;

public interface IScrapeJobProcessor
{
    Task<ScrapeRun> CreateRun(ScrapeJobConfig job, string triggerType, Guid? batchId = null);
    Task Execute(ScrapeRun run, ScrapeJobConfig job);
    Task SearchAndPersist(ScrapeRun run, ScrapeJobConfig job, CancellationToken ct = default);
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
    private static readonly IBrowsingContext SharedBrowsingContext = BrowsingContext.New(Configuration.Default);

    private readonly ILogger<ScrapeJobProcessor> _logger;
    private readonly EtlDbContext _dbContext;
    private readonly IWebscraperClient _webscraperClient;
    private readonly ISearchParser _searchParser;
    private readonly IListingParser _listingParser;
    private readonly IEbayUrlBuilder _urlBuilder;
    private readonly IListingIndexingService _indexingService;
    private readonly DbWriteGate _dbWriteGate;
    private readonly IEnumerable<IPostJobStage> _postJobStages;
    private readonly ScrapingConfig _scrapingConfig;

    public ScrapeJobProcessor(
        ILogger<ScrapeJobProcessor> logger,
        EtlDbContext dbContext,
        IWebscraperClient webscraperClient,
        ISearchParser searchParser,
        IListingParser listingParser,
        IEbayUrlBuilder urlBuilder,
        IListingIndexingService indexingService,
        DbWriteGate dbWriteGate,
        IEnumerable<IPostJobStage> postJobStages,
        ScrapingConfig scrapingConfig)
    {
        _logger = logger;
        _dbContext = dbContext;
        _webscraperClient = webscraperClient;
        _searchParser = searchParser;
        _listingParser = listingParser;
        _urlBuilder = urlBuilder;
        _indexingService = indexingService;
        _dbWriteGate = dbWriteGate;
        _postJobStages = postJobStages;
        _scrapingConfig = scrapingConfig;
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
            await ExecuteScrape(run, job);
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

    public async Task SearchAndPersist(ScrapeRun run, ScrapeJobConfig job, CancellationToken ct = default)
    {
        if (run.SearchResultsJson != null)
        {
            _logger.LogInformation("Search results already persisted for RunId={RunId}, skipping", run.Id);
            return;
        }

        const int maxPages = 100;
        var maxSoldPages = CalculateMaxSoldPages(job.LastRunUtc, maxPages);
        _logger.LogInformation("SearchAndPersist: RunId={RunId}, JobId={JobId}, maxSoldPages={MaxSoldPages}",
            run.Id, job.Id, maxSoldPages);

        var soldSummaries = await SearchSoldListings(run, job.SearchTerm, maxSoldPages);
        ct.ThrowIfCancellationRequested();
        var activeSummaries = await SearchActiveListings(run, job.SearchTerm, maxPages);
        ct.ThrowIfCancellationRequested();

        // Merge — sold wins if listing appears in both (same logic as ClassifyListings)
        var merged = new Dictionary<string, EbayProductSummary>();
        foreach (var summary in soldSummaries.Concat(activeSummaries))
        {
            if (string.IsNullOrEmpty(summary.ListingId))
            {
                continue;
            }

            merged.TryAdd(summary.ListingId, (EbayProductSummary)summary);
        }

        run.SearchResultsJson = JsonSerializer.Serialize(merged.Values.ToList());
        run.TotalListingsFound = merged.Count;
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "SearchAndPersist complete: RunId={RunId}, {SoldCount} sold + {ActiveCount} active = {MergedCount} merged",
            run.Id, soldSummaries.Count, activeSummaries.Count, merged.Count);
    }

    private async Task ExecuteScrape(ScrapeRun scrapeRun, ScrapeJobConfig job)
    {
        // Mark as running so the UI can track progress
        scrapeRun.Status = "Running";
        // Reset processing counters — ensures clean state on recovery restarts.
        // TotalListingsFound is preserved if already set by SearchAndPersist phase.
        scrapeRun.ListingsProcessed = 0;
        scrapeRun.ListingsAddedActive = 0;
        scrapeRun.ListingsAddedSold = 0;
        scrapeRun.ListingsUpdated = 0;
        scrapeRun.ListingsSkipped = 0;
        scrapeRun.ListingsFailed = 0;
        scrapeRun.ListingsFilteredPreQueue = 0;
        if (scrapeRun.SearchResultsJson == null)
        {
            // Only reset TotalListingsFound for inline search path —
            // phased pipeline already set this in SearchAndPersist
            scrapeRun.TotalListingsFound = 0;
        }
        await _dbContext.SaveChangesAsync();

        List<IEbayProductSummary> soldSummaries;
        List<IEbayProductSummary> activeSummaries;

        if (scrapeRun.SearchResultsJson != null)
        {
            // Consume persisted search results from SearchAndPersist phase
            var persisted = JsonSerializer.Deserialize<List<EbayProductSummary>>(scrapeRun.SearchResultsJson) ?? [];
            soldSummaries = persisted.Where(s => s.IsSold).Cast<IEbayProductSummary>().ToList();
            activeSummaries = persisted.Where(s => !s.IsSold).Cast<IEbayProductSummary>().ToList();

            // Clear JSON after consumption to free memory
            scrapeRun.SearchResultsJson = null;
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation(
                "Loaded persisted search results: {SoldCount} sold, {ActiveCount} active",
                soldSummaries.Count, activeSummaries.Count);
        }
        else
        {
            // Fallback: inline search (backward-compatible for recovery/standalone use)
            const int maxPages = 100;
            var maxSoldPages = CalculateMaxSoldPages(job.LastRunUtc, maxPages);
            _logger.LogInformation("Smart lookback: LastRunUtc={LastRun}, maxSoldPages={MaxSoldPages}",
                job.LastRunUtc, maxSoldPages);

            soldSummaries = await SearchSoldListings(scrapeRun, job.SearchTerm, maxSoldPages);
            activeSummaries = await SearchActiveListings(scrapeRun, job.SearchTerm, maxPages);
        }

        var classified = await ClassifyListings(scrapeRun, activeSummaries, soldSummaries, job.Id);

        if (classified.ToUpdateFromSummary.Count > 0)
        {
            await UpdateListingsFromSummary(scrapeRun, classified.ToUpdateFromSummary, classified.ExistingListings);
        }

        if (classified.ToScrape.Count > 0)
        {
            await FetchAndProcessDescriptions(scrapeRun, classified.ToScrape, classified.ExistingListings, job.Id);
        }

        await RunPostJobStages(scrapeRun, job);

        // Always mark complete — no more "waiting for callbacks"
        await MarkCompleted(scrapeRun, job.Id);
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
        // AsNoTracking: we re-attach only the ones we modify later
        var existingListings = await _dbContext.Listings
            .AsNoTracking()
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

        // Re-attach or locate tracked entities loaded with AsNoTracking
        foreach (var summary in summaries)
        {
            if (string.IsNullOrEmpty(summary.ListingId)) { continue; }
            if (!existingListings.TryGetValue(summary.ListingId, out var listing)) { continue; }

            var tracked = _dbContext.Listings.Local.FirstOrDefault(l => l.Id == listing.Id);
            if (tracked != null)
            {
                // Entity already tracked (e.g., test context or prior operation) — use the tracked instance
                existingListings[summary.ListingId] = tracked;
            }
            else
            {
                _dbContext.Listings.Attach(listing);
            }
        }

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
            }
            else
            {
                scrapeRun.ListingsSkipped++;
            }

            scrapeRun.ListingsProcessed++;
        }

        await _dbContext.SaveChangesAsync();

        // Detach updated listings — no longer needed downstream.
        // Keep scrapeRun attached since it's updated throughout the pipeline.
        var scrapeRunEntry = _dbContext.Entry(scrapeRun);
        _dbContext.ChangeTracker.Clear();
        scrapeRunEntry.State = EntityState.Unchanged;

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

        // Bounded channel prevents memory growth if consumer lags behind producer.
        // Capacity = 2× concurrency gives producer headroom without unbounded queuing.
        var fetchConcurrency = _scrapingConfig.MaxConcurrentDescriptionFetches;
        var channel = Channel.CreateBounded<DescriptionFetchResult>(
            new BoundedChannelOptions(fetchConcurrency * 2) { FullMode = BoundedChannelFullMode.Wait });
        var concurrency = new SemaphoreSlim(fetchConcurrency);
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
                        html = await _webscraperClient.GetPageHtmlAsync(descriptionUrl, requiresBrowser: false);
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

        // Consumer: process results with batched DB saves (1 round-trip per batch instead of per item)
        const int saveBatchSize = 50;
        var pendingIndex = new List<(Listing Listing, IEbayProductSummary Summary)>();
        var processed = 0;

        await foreach (var result in channel.Reader.ReadAllAsync())
        {
            var listingToIndex = await ProcessFetchedDescription(scrapeRun, result, existingListings);
            if (listingToIndex != null)
            {
                pendingIndex.Add((listingToIndex, result.Summary));
            }
            scrapeRun.ListingsProcessed++;
            processed++;

            if (processed % saveBatchSize == 0)
            {
                await SaveAndIndex(scrapeRun, pendingIndex);
                pendingIndex.Clear();
            }
        }

        await producerTask;
        await SaveAndIndex(scrapeRun, pendingIndex);
    }

    private async Task SaveListingsFromSummaries(
        ScrapeRun scrapeRun, List<IEbayProductSummary> summaries,
        Dictionary<string, Listing> existingListings, int jobId)
    {
        // Re-attach or locate tracked entities loaded with AsNoTracking
        foreach (var summary in summaries)
        {
            if (string.IsNullOrEmpty(summary.ListingId)) { continue; }
            if (!existingListings.TryGetValue(summary.ListingId, out var existing)) { continue; }

            var tracked = _dbContext.Listings.Local.FirstOrDefault(l => l.Id == existing.Id);
            if (tracked != null)
            {
                existingListings[summary.ListingId] = tracked;
            }
            else if (_dbContext.Entry(existing).State == EntityState.Detached)
            {
                _dbContext.Listings.Attach(existing);
            }
        }

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
                existing.PurchaseFormat = concrete?.BuyingFormat?.ToString();
                existing.Images = images;
                existing.EndDateUtc = concrete?.EndDateUtc;
                existing.DescriptionStatus = "pending";
                existing.UpdatedUtc = DateTime.UtcNow;

                if (ListingStatusHelper.CanUpdateStatus(existing.ListingStatus, newStatus))
                {
                    existing.ListingStatus = newStatus;
                }

                if (oldStatus != existing.ListingStatus)
                {
                    _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                    {
                        ListingId = existing.Id,
                        ListingStatus = existing.ListingStatus ?? oldStatus ?? "Unknown",
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

        // Add newly created listings to dictionary for downstream lookup (avoids per-item DB queries)
        foreach (var (listing, _) in newListings)
        {
            existingListings[listing.ListingId] = listing;
        }

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

        // Detach all saved entities to prevent unbounded tracker growth.
        // Keep scrapeRun attached since it's updated throughout the pipeline.
        // Listings in existingListings dictionary remain usable as POCOs for
        // downstream lookups — they'll be re-attached as needed in ProcessFetchedDescription.
        var scrapeRunEntry = _dbContext.Entry(scrapeRun);
        _dbContext.ChangeTracker.Clear();
        scrapeRunEntry.State = EntityState.Unchanged;

        _logger.LogInformation("Saved {Count} listings for job {JobId}", summaries.Count, jobId);
    }

    private async Task<Listing?> ProcessFetchedDescription(
        ScrapeRun scrapeRun, DescriptionFetchResult result,
        Dictionary<string, Listing> allListings)
    {
        var summary = result.Summary;

        if (string.IsNullOrEmpty(summary.ListingId) ||
            !allListings.TryGetValue(summary.ListingId, out var listing))
        {
            _logger.LogWarning("Listing {ListingId} not found for description processing", summary.ListingId);
            scrapeRun.ListingsFailed++;
            RecordIssue(scrapeRun, summary.ListingId ?? "unknown", "ListingNotFound", "DescriptionFetch",
                "Listing not found in database during description processing");
            return null;
        }

        // Re-attach listing if detached (cleared by ChangeTracker.Clear after SaveListingsFromSummaries)
        var tracked = _dbContext.Listings.Local.FirstOrDefault(l => l.Id == listing.Id);
        if (tracked != null && tracked != listing)
        {
            listing = tracked;
            allListings[summary.ListingId] = tracked;
        }
        else if (_dbContext.Entry(listing).State == EntityState.Detached)
        {
            _dbContext.Listings.Attach(listing);
        }

        if (result.Error != null)
        {
            _logger.LogWarning(result.Error, "Failed to fetch description for {ListingId}", summary.ListingId);
            listing.DescriptionStatus = "missing";
            scrapeRun.ListingsFailed++;
            RecordIssue(scrapeRun, summary.ListingId!, "DescriptionFetchFailed", "DescriptionFetch", result.Error);
            return null;
        }

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
            return null;
        }

        if (string.IsNullOrEmpty(description))
        {
            listing.DescriptionStatus = "missing";
            IncrementAddedCounter(scrapeRun, summary);
            return null;
        }

        listing.Description = description;
        listing.DescriptionStatus = "complete";
        return listing;
    }

    private async Task SaveAndIndex(ScrapeRun scrapeRun,
        List<(Listing Listing, IEbayProductSummary Summary)> toIndex)
    {
        await _dbContext.SaveChangesAsync();

        // Detach saved entities to prevent unbounded change tracker growth.
        // Keep scrapeRun attached since it's updated throughout the pipeline.
        var scrapeRunEntry = _dbContext.Entry(scrapeRun);
        _dbContext.ChangeTracker.Clear();
        scrapeRunEntry.State = EntityState.Unchanged;

        if (toIndex.Count == 0)
        {
            return;
        }

        // Batch embedding: single API call per batch instead of one per listing
        var listings = toIndex.Select(x => x.Listing).ToList();
        var results = (await _indexingService.IndexBatch(listings, embedContent: true)).ToList();

        var hadErrors = false;
        for (var i = 0; i < toIndex.Count; i++)
        {
            var (_, summary) = toIndex[i];
            var result = i < results.Count ? results[i] : new IndexingResult(IndexingAction.Failed, "No result returned");

            if (result.Action == IndexingAction.Failed)
            {
                _logger.LogWarning("Failed to index listing {ListingId}: {Error}", summary.ListingId, result.Error);
                scrapeRun.ListingsFailed++;
                RecordIssue(scrapeRun, summary.ListingId!, "IndexingFailed", "Indexing",
                    result.Error ?? "Embedding failed");
                hadErrors = true;
            }
            else
            {
                IncrementAddedCounter(scrapeRun, summary);
            }
        }

        if (hadErrors)
        {
            await _dbContext.SaveChangesAsync();
        }
    }

    private static void IncrementAddedCounter(ScrapeRun scrapeRun, IEbayProductSummary summary)
    {
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

    private void RecordIssue(ScrapeRun scrapeRun,
        string issueType, string phase, Exception ex)
    {
        RecordIssue(scrapeRun, string.Empty, issueType, phase, ex);
    }

    private static async Task<AngleSharp.Dom.IDocument> ParseHtml(string html)
    {
        return await SharedBrowsingContext.OpenAsync(req => req.Content(html));
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

    private async Task RunPostJobStages(ScrapeRun scrapeRun, ScrapeJobConfig job)
    {
        var context = new PostJobContext(scrapeRun.Id, job.Id, job.SearchTerm);

        foreach (var stage in _postJobStages)
        {
            try
            {
                await SetPhase(scrapeRun, stage.Name);
                await stage.Execute(context);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-job stage '{StageName}' failed for RunId={RunId}", stage.Name, scrapeRun.Id);
                RecordIssue(scrapeRun, "PostJobStageFailed", stage.Name, ex);
                await _dbContext.SaveChangesAsync();
            }
        }
    }

    private async Task MarkCompleted(ScrapeRun scrapeRun, int jobId)
    {
        scrapeRun.Status = "Completed";
        scrapeRun.CurrentPhase = "Completed";
        scrapeRun.CompletedUtc = DateTime.UtcNow;

        // Force EF to persist changes — ChangeTracker.Clear() earlier in the pipeline
        // can leave the entity in a state where automatic change detection misses updates.
        _dbContext.Entry(scrapeRun).State = EntityState.Modified;

        var job = await _dbContext.ScrapeJobs.FindAsync(jobId);
        if (job != null)
        {
            job.LastRunUtc = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync();
    }

    public static int CalculateMaxSoldPages(DateTime? lastRunUtc, int defaultMaxPages = 100)
    {
        if (lastRunUtc == null)
        {
            return defaultMaxPages;
        }

        var daysSinceLastRun = (int)Math.Ceiling((DateTime.UtcNow - lastRunUtc.Value).TotalDays);
        var lookbackDays = Math.Max(1, daysSinceLastRun + 1); // +1 buffer

        // Heuristic: ~2 pages per day of sold results for typical searches
        // Minimum 5 pages to handle high-volume searches
        var calculatedPages = Math.Max(5, lookbackDays * 2);
        return Math.Min(calculatedPages, defaultMaxPages);
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

            var document = await SharedBrowsingContext.OpenAsync(request => request.Content(html));

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
