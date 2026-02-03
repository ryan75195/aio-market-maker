using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScraperWorker.Services;

namespace AIOMarketMaker.Etl.Services;

public record StatusRefreshResult(
    int Checked,
    int Updated,
    List<StatusChangeInfo>? Changes = null
);

public record StatusChangeInfo(
    int ListingId,
    string EbayListingId,
    string OldStatus,
    string NewStatus
);

public record ListingToCheck(
    int Id,
    string ListingId,
    string? Url,
    string? CurrentStatus
);

public interface IStatusRefreshRunner
{
    Task<StatusRefreshResult> RefreshActiveListingsAsync(int? jobId = null, CancellationToken ct = default);
}

public class StatusRefreshRunner : IStatusRefreshRunner
{
    private readonly EtlDbContext _dbContext;
    private readonly IWebscraperClient _webscraperClient;
    private readonly IListingParser _listingParser;
    private readonly IJobRepository _jobRepository;
    private readonly ILogger<StatusRefreshRunner> _logger;
    private readonly IBrowsingContext _browsingContext = BrowsingContext.New(AngleSharp.Configuration.Default);

    public StatusRefreshRunner(
        EtlDbContext dbContext,
        IWebscraperClient webscraperClient,
        IListingParser listingParser,
        IJobRepository jobRepository,
        ILogger<StatusRefreshRunner> logger)
    {
        _dbContext = dbContext;
        _webscraperClient = webscraperClient;
        _listingParser = listingParser;
        _jobRepository = jobRepository;
        _logger = logger;
    }

    public async Task<StatusRefreshResult> RefreshActiveListingsAsync(int? jobId = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting status refresh for {Scope}",
            jobId.HasValue ? $"job {jobId}" : "all active listings");

        // 1. Get all listings with "Active" status
        var query = _dbContext.Listings
            .Where(l => l.ListingStatus == "Active");

        if (jobId.HasValue)
        {
            query = query.Where(l => l.ScrapeJobId == jobId.Value);
        }

        var listingsToCheck = await query
            .Select(l => new ListingToCheck(l.Id, l.ListingId, l.Url, l.ListingStatus))
            .ToListAsync(ct);

        if (listingsToCheck.Count == 0)
        {
            _logger.LogInformation("No active listings to check");
            return new StatusRefreshResult(0, 0);
        }

        _logger.LogInformation("Found {Count} active listings to check", listingsToCheck.Count);

        // 2. Filter to only those with valid URLs
        var listingsWithUrls = listingsToCheck
            .Where(l => !string.IsNullOrEmpty(l.Url))
            .ToList();

        if (listingsWithUrls.Count == 0)
        {
            _logger.LogWarning("No listings have URLs to check");
            return new StatusRefreshResult(listingsToCheck.Count, 0);
        }

        // 3. Batch fetch HTML via WebscraperClient
        var urls = listingsWithUrls.Select(l => l.Url!).ToList();
        _logger.LogInformation("Fetching {Count} listing pages...", urls.Count);

        var jobResults = await _webscraperClient.RunJobAsync(urls);
        var resultsList = jobResults.ToList();

        _logger.LogInformation("Received {Count} page results", resultsList.Count);

        // 4. Build URL to HTML mapping
        var urlToHtml = new Dictionary<string, string>();
        foreach (var result in resultsList)
        {
            if (!string.IsNullOrEmpty(result.Url))
            {
                var html = await _jobRepository.GetFileContentsAsync(
                    result.PartitionKey, result.Url, ct);
                urlToHtml[result.Url] = html;
            }
        }

        // 5. Parse results and check for status changes
        int updated = 0;
        var changes = new List<StatusChangeInfo>();

        foreach (var listing in listingsWithUrls)
        {
            if (!urlToHtml.TryGetValue(listing.Url!, out var html))
            {
                _logger.LogWarning("No HTML found for listing {ListingId} ({Url})", listing.Id, listing.Url);
                continue;
            }

            try
            {
                var doc = await LoadDocumentAsync(html);
                var parsed = _listingParser.ParseProductListing(doc);
                var newStatus = parsed.listingStatus?.ToString() ?? "Unknown";

                if (newStatus != listing.CurrentStatus)
                {
                    _logger.LogInformation(
                        "Status change detected for {ListingId}: {OldStatus} -> {NewStatus}",
                        listing.ListingId, listing.CurrentStatus, newStatus);

                    // Update the listing's current status
                    var dbListing = await _dbContext.Listings.FindAsync(new object[] { listing.Id }, ct);
                    if (dbListing != null)
                    {
                        dbListing.ListingStatus = newStatus;
                        dbListing.UpdatedUtc = DateTime.UtcNow;

                        // Update EndDateUtc if it was parsed (for sold items)
                        if (parsed.SoldDateUtc.HasValue)
                        {
                            dbListing.EndDateUtc = parsed.SoldDateUtc;
                        }

                        // Add history record
                        _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                        {
                            ListingId = listing.Id,
                            ListingStatus = newStatus,
                            Price = parsed.price,
                            SoldDateUtc = parsed.SoldDateUtc,
                            RecordedUtc = DateTime.UtcNow,
                            Source = "StatusRefresh"
                        });

                        // NOTE: Products are now cluster-level aggregates, not per-listing records.
                        // Product statistics should be recalculated separately if needed.

                        changes.Add(new StatusChangeInfo(
                            listing.Id,
                            listing.ListingId,
                            listing.CurrentStatus ?? "Unknown",
                            newStatus));

                        updated++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing listing {ListingId} ({Url})", listing.Id, listing.Url);
            }
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Status refresh complete: checked {Checked}, updated {Updated}",
            listingsToCheck.Count, updated);

        return new StatusRefreshResult(listingsToCheck.Count, updated, changes);
    }

    private async Task<IDocument> LoadDocumentAsync(string html)
    {
        return await _browsingContext
            .OpenAsync(req => req.Content(html))
            .ConfigureAwait(false);
    }
}
