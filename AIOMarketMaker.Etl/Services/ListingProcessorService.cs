using AngleSharp;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Endpoints;

namespace AIOMarketMaker.Etl.Services;

public interface IListingProcessorService
{
    Task<ProcessListingResponse> Process(ProcessListingRequest request);
}

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
            await _counterService.Increment(request.ScrapeRunId, request.ScrapeJobId, "skipped");
            return new ProcessListingResponse(true, "skipped", null);
        }

        var listing = await _dbContext.Listings
            .FirstOrDefaultAsync(l => l.ListingId == request.ListingId
                                   && l.ScrapeJobId == request.ScrapeJobId);

        if (listing == null)
        {
            _logger.LogWarning("Listing {ListingId} not found for job {JobId}", request.ListingId, request.ScrapeJobId);
            await _counterService.Increment(request.ScrapeRunId, request.ScrapeJobId, "failed");
            return new ProcessListingResponse(false, "failed", "Listing not found");
        }

        var html = await FetchHtml(request.BlobPath);
        if (html == null)
        {
            listing.DescriptionStatus = "missing";
            MarkScrapeRunListingComplete(scrapeRunListing);
            await _dbContext.SaveChangesAsync();
            await _counterService.Increment(request.ScrapeRunId, request.ScrapeJobId, "added", listing.ListingStatus);
            return new ProcessListingResponse(true, "complete", null);
        }

        try
        {
            var document = await ParseHtml(html);
            var description = _listingParser.ParseDescription(document);

            if (string.IsNullOrEmpty(description))
            {
                listing.DescriptionStatus = "missing";
            }
            else
            {
                listing.Description = description;
                listing.DescriptionStatus = "complete";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse description for listing {ListingId}", request.ListingId);
            listing.DescriptionStatus = "failed";
        }

        MarkScrapeRunListingComplete(scrapeRunListing);
        await _dbContext.SaveChangesAsync();

        if (listing.DescriptionStatus == "complete")
        {
            await _indexingService.Index(listing, embedContent: true);
        }

        await _counterService.Increment(request.ScrapeRunId, request.ScrapeJobId, "added", listing.ListingStatus);

        _logger.LogInformation("Processed description for listing {ListingId}: {Status}",
            request.ListingId, listing.DescriptionStatus);
        return new ProcessListingResponse(true, "complete", null);
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

    private static void MarkScrapeRunListingComplete(
        AIOMarketMaker.Core.Data.Models.ScrapeRunListing? scrapeRunListing)
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
}
