using AIOMarketMaker.Api.Services;
using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Etl.Data.Models;
using AIOMarketMaker.Services;
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
    int ProductId,
    string ListingId,
    string OldStatus,
    string NewStatus
);

public record ProductToCheck(
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
    private readonly IBrowsingContext _browsingContext = BrowsingContext.New(Configuration.Default);

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

        // 1. Get all products with "Active" status
        var query = _dbContext.Products
            .Where(p => p.ListingStatus == "Active");

        if (jobId.HasValue)
        {
            query = query.Where(p => p.ScrapeJobId == jobId.Value);
        }

        var productsToCheck = await query
            .Select(p => new ProductToCheck(p.Id, p.ListingId, p.Url, p.ListingStatus))
            .ToListAsync(ct);

        if (productsToCheck.Count == 0)
        {
            _logger.LogInformation("No active listings to check");
            return new StatusRefreshResult(0, 0);
        }

        _logger.LogInformation("Found {Count} active listings to check", productsToCheck.Count);

        // 2. Filter to only those with valid URLs
        var productsWithUrls = productsToCheck
            .Where(p => !string.IsNullOrEmpty(p.Url))
            .ToList();

        if (productsWithUrls.Count == 0)
        {
            _logger.LogWarning("No products have URLs to check");
            return new StatusRefreshResult(productsToCheck.Count, 0);
        }

        // 3. Batch fetch HTML via WebscraperClient
        var urls = productsWithUrls.Select(p => p.Url!).ToList();
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

        foreach (var product in productsWithUrls)
        {
            if (!urlToHtml.TryGetValue(product.Url!, out var html))
            {
                _logger.LogWarning("No HTML found for product {ProductId} ({Url})", product.Id, product.Url);
                continue;
            }

            try
            {
                var doc = await LoadDocumentAsync(html);
                var parsed = _listingParser.ParseProductListing(doc, product.Url!);
                var newStatus = parsed.listingStatus?.ToString() ?? "Unknown";

                if (newStatus != product.CurrentStatus)
                {
                    _logger.LogInformation(
                        "Status change detected for {ListingId}: {OldStatus} -> {NewStatus}",
                        product.ListingId, product.CurrentStatus, newStatus);

                    // Update the product's current status
                    var dbProduct = await _dbContext.Products.FindAsync(new object[] { product.Id }, ct);
                    if (dbProduct != null)
                    {
                        dbProduct.ListingStatus = newStatus;
                        dbProduct.UpdatedUtc = DateTime.UtcNow;

                        // Update EndDateUtc if it was parsed (for sold items)
                        if (parsed.SoldDateUtc.HasValue)
                        {
                            dbProduct.EndDateUtc = parsed.SoldDateUtc;
                        }

                        // Add history record
                        _dbContext.ProductStatusHistory.Add(new ProductStatusHistory
                        {
                            ProductId = product.Id,
                            ListingStatus = newStatus,
                            Price = parsed.price,
                            SoldDateUtc = parsed.SoldDateUtc,
                            RecordedUtc = DateTime.UtcNow,
                            Source = "StatusRefresh"
                        });

                        changes.Add(new StatusChangeInfo(
                            product.Id,
                            product.ListingId,
                            product.CurrentStatus ?? "Unknown",
                            newStatus));

                        updated++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing product {ProductId} ({Url})", product.Id, product.Url);
            }
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Status refresh complete: checked {Checked}, updated {Updated}",
            productsToCheck.Count, updated);

        return new StatusRefreshResult(productsToCheck.Count, updated, changes);
    }

    private async Task<IDocument> LoadDocumentAsync(string html)
    {
        return await _browsingContext
            .OpenAsync(req => req.Content(html))
            .ConfigureAwait(false);
    }
}
