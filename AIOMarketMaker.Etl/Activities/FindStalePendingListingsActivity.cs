using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

public class FindStalePendingListingsActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly BlobServiceClient _blobService;
    private readonly ILogger<FindStalePendingListingsActivity> _logger;

    public FindStalePendingListingsActivity(
        EtlDbContext dbContext,
        BlobServiceClient blobService,
        ILogger<FindStalePendingListingsActivity> logger)
    {
        _dbContext = dbContext;
        _blobService = blobService;
        _logger = logger;
    }

    [Function(nameof(FindStalePendingListingsActivity))]
    public async Task<FindPendingListingsResult> Run([ActivityTrigger] int scrapeRunId)
    {
        // Get all pending listings for this run
        var pendingListings = await _dbContext.ScrapeRunListings
            .Where(srl => srl.ScrapeRunId == scrapeRunId && srl.Status == "Pending")
            .Select(srl => srl.ListingId)
            .ToListAsync();

        if (pendingListings.Count == 0)
        {
            return new FindPendingListingsResult(new List<PendingListingInfo>());
        }

        var container = _blobService.GetBlobContainerClient("html");
        var results = new List<PendingListingInfo>();

        foreach (var listingId in pendingListings)
        {
            // Check if listing blob exists
            var blobPath = $"{scrapeRunId}/{listingId}/listing.html";
            var blobClient = container.GetBlobClient(blobPath);
            var blobExists = await blobClient.ExistsAsync();

            results.Add(new PendingListingInfo(listingId, blobExists.Value));
        }

        _logger.LogInformation(
            "Found {Total} pending listings for ScrapeRun {ScrapeRunId}, {WithBlob} have blobs",
            results.Count, scrapeRunId, results.Count(r => r.BlobExists));

        return new FindPendingListingsResult(results);
    }
}
