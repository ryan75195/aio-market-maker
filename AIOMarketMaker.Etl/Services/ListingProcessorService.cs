using System.Text.Json;
using AngleSharp;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Endpoints;

namespace AIOMarketMaker.Etl.Services;

public interface IListingProcessorService
{
    Task<ProcessListingResponse> Process(ProcessListingRequest request);
}

public record UpsertResult(Listing Listing, string Status, bool IsUpdate, string? OldStatus, decimal? OldPrice);

public class ListingProcessorService : IListingProcessorService
{
    private readonly BlobServiceClient _blobService;
    private readonly EtlDbContext _dbContext;
    private readonly IListingParser _listingParser;
    private readonly IScrapeRunCounterService _counterService;
    private readonly IListingIndexingService _indexingService;
    private readonly ILogger<ListingProcessorService> _logger;

    public ListingProcessorService(
        BlobServiceClient blobService,
        EtlDbContext dbContext,
        IListingParser listingParser,
        IScrapeRunCounterService counterService,
        IListingIndexingService indexingService,
        ILogger<ListingProcessorService> logger)
    {
        _blobService = blobService;
        _dbContext = dbContext;
        _listingParser = listingParser;
        _counterService = counterService;
        _indexingService = indexingService;
        _logger = logger;
    }

    public async Task<ProcessListingResponse> Process(ProcessListingRequest request)
    {
        var scrapeRunListing = await _dbContext.ScrapeRunListings
            .FirstOrDefaultAsync(srl => srl.ScrapeRunId == request.ScrapeRunId
                                     && srl.ListingId == request.ListingId);

        if (scrapeRunListing?.Status == "Complete")
        {
            _logger.LogInformation("Listing {ListingId} already processed, skipping", request.ListingId);
            return new ProcessListingResponse(true, "skipped", null);
        }

        var html = await FetchHtml(request.BlobPath);
        if (html == null)
        {
            return new ProcessListingResponse(false, "failed", "Blob not found");
        }

        var document = await ParseHtml(html);

        if (_listingParser.IsProductCatalogPage(document))
        {
            return await HandleProductCatalogPage(request, scrapeRunListing);
        }

        var parsedListing = _listingParser.ParseProductListing(document);

        if (string.IsNullOrEmpty(parsedListing.title))
        {
            return await HandleParseFailed(request, scrapeRunListing, html.Length);
        }

        return await UpsertAndRecordHistory(request, scrapeRunListing, parsedListing);
    }

    private async Task<ProcessListingResponse> UpsertAndRecordHistory(
        ProcessListingRequest request, ScrapeRunListing? scrapeRunListing, ExtractedEbayListing parsedListing)
    {
        var existingListing = await _dbContext.Listings
            .FirstOrDefaultAsync(l => l.ListingId == request.ListingId
                                   && l.ScrapeJobId == request.ScrapeJobId);

        var newStatus = parsedListing.listingStatus?.ToString();

        if (existingListing != null && !ListingStatusHelper.CanUpdateStatus(existingListing.ListingStatus, newStatus))
        {
            return await HandleInvalidTransition(request, scrapeRunListing, existingListing.ListingStatus, newStatus);
        }

        var result = UpsertListing(existingListing, parsedListing, request);
        await _dbContext.SaveChangesAsync();

        await CreateStatusHistory(result, parsedListing);
        await _indexingService.Index(result.Listing, isNew: !result.IsUpdate);

        MarkScrapeRunListingComplete(scrapeRunListing);
        await _dbContext.SaveChangesAsync();

        await _counterService.Increment(request.ScrapeRunId, result.Status, newStatus);

        _logger.LogInformation("Processed listing {ListingId} with status {Status}", request.ListingId, result.Status);
        return new ProcessListingResponse(true, result.Status, null);
    }

    private async Task<string?> FetchHtml(string blobPath)
    {
        var containerClient = _blobService.GetBlobContainerClient("html");
        var blobClient = containerClient.GetBlobClient(blobPath);

        var existsResponse = await blobClient.ExistsAsync();
        if (!existsResponse.Value)
        {
            _logger.LogWarning("Blob not found: {BlobPath}", blobPath);
            return null;
        }

        var downloadResult = await blobClient.DownloadContentAsync();
        return downloadResult.Value.Content.ToString();
    }

    private async Task<ProcessListingResponse> HandleProductCatalogPage(
        ProcessListingRequest request, ScrapeRunListing? scrapeRunListing)
    {
        _logger.LogInformation("Listing {ListingId} redirected to product catalog page, skipping",
            request.ListingId);

        if (scrapeRunListing != null)
        {
            scrapeRunListing.Status = "Skipped";
            scrapeRunListing.FailureReason = "PRODUCT_PAGE";
            scrapeRunListing.CompletedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }

        await _counterService.Increment(request.ScrapeRunId, "skipped");
        return new ProcessListingResponse(true, "skipped", null);
    }

    private async Task<ProcessListingResponse> HandleParseFailed(
        ProcessListingRequest request, ScrapeRunListing? scrapeRunListing, int htmlLength)
    {
        _logger.LogWarning("Failed to parse listing {ListingId}: no title extracted (possible error page, HTML size: {Size} bytes)",
            request.ListingId, htmlLength);

        if (scrapeRunListing != null)
        {
            scrapeRunListing.Status = "Failed";
            scrapeRunListing.FailureReason = "PARSE_FAILED";
            scrapeRunListing.CompletedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }

        await _counterService.Increment(request.ScrapeRunId, "failed");
        return new ProcessListingResponse(false, "failed", "Failed to parse listing - no title extracted");
    }

    private async Task<ProcessListingResponse> HandleInvalidTransition(
        ProcessListingRequest request, ScrapeRunListing? scrapeRunListing,
        string? oldStatus, string? newStatus)
    {
        _logger.LogWarning("Invalid status transition for {ListingId}: {OldStatus} -> {NewStatus}",
            request.ListingId, oldStatus, newStatus);

        await _counterService.Increment(request.ScrapeRunId, "skipped");

        if (scrapeRunListing != null)
        {
            scrapeRunListing.Status = "Skipped";
            scrapeRunListing.CompletedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }

        return new ProcessListingResponse(true, "skipped",
            $"Invalid status transition: {oldStatus} -> {newStatus}");
    }

    private UpsertResult UpsertListing(
        Listing? existing, ExtractedEbayListing parsed, ProcessListingRequest request)
    {
        var oldStatus = existing?.ListingStatus;
        var oldPrice = existing?.Price;

        if (existing != null)
        {
            existing.Title = parsed.title;
            existing.Price = parsed.price;
            existing.Currency = parsed.currency;
            existing.ShippingCost = parsed.shippingCost;
            existing.Condition = parsed.Condition?.ToString();
            existing.ListingStatus = parsed.listingStatus?.ToString();
            existing.PurchaseFormat = parsed.purchaseFormat?.ToString();
            existing.ItemSpecifics = parsed.ItemSpecifics;
            existing.Images = parsed.images != null ? JsonSerializer.Serialize(parsed.images) : null;
            existing.Location = parsed.Location;
            existing.Url = parsed.Url;
            existing.UpdatedUtc = DateTime.UtcNow;
            return new UpsertResult(existing, "updated", IsUpdate: true, oldStatus, oldPrice);
        }

        var newListing = new Listing
        {
            ListingId = request.ListingId,
            ScrapeJobId = request.ScrapeJobId,
            Title = parsed.title,
            Price = parsed.price,
            Currency = parsed.currency,
            ShippingCost = parsed.shippingCost,
            Condition = parsed.Condition?.ToString(),
            ListingStatus = parsed.listingStatus?.ToString(),
            PurchaseFormat = parsed.purchaseFormat?.ToString(),
            ItemSpecifics = parsed.ItemSpecifics,
            Images = parsed.images != null ? JsonSerializer.Serialize(parsed.images) : null,
            Location = parsed.Location,
            Url = parsed.Url,
            CreatedUtc = DateTime.UtcNow
        };
        _dbContext.Listings.Add(newListing);
        return new UpsertResult(newListing, "added", IsUpdate: false, null, null);
    }

    private void MarkScrapeRunListingComplete(ScrapeRunListing? scrapeRunListing)
    {
        if (scrapeRunListing == null)
        {
            return;
        }

        scrapeRunListing.Status = "Complete";
        scrapeRunListing.CompletedUtc = DateTime.UtcNow;
    }

    private static async Task<AngleSharp.Dom.IDocument> ParseHtml(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        return await context.OpenAsync(req => req.Content(html));
    }

    private async Task CreateStatusHistory(UpsertResult result, ExtractedEbayListing parsed)
    {
        var isUpdate = result.IsUpdate;
        var newStatus = parsed.listingStatus?.ToString();
        var statusChanged = isUpdate && result.OldStatus != newStatus;
        var priceChanged = isUpdate && result.OldPrice != parsed.price;

        if (!isUpdate || statusChanged || priceChanged)
        {
            var source = !isUpdate
                ? "InitialScrape"
                : (statusChanged ? "StatusUpdate" : "PriceUpdate");

            _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
            {
                ListingId = result.Listing.Id,
                ListingStatus = newStatus ?? "Unknown",
                Price = parsed.price,
                SoldDateUtc = parsed.SoldDateUtc,
                RecordedUtc = DateTime.UtcNow,
                Source = source
            });
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Created ListingStatusHistory for {ListingId}: {Source} ({Status}, {Price})",
                result.Listing.ListingId, source, newStatus, parsed.price);
        }
    }
}
