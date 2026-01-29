using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

public class FindStalePendingListingsActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly BlobServiceClient _blobService;
    private readonly DurableTaskClient _durableClient;
    private readonly ILogger<FindStalePendingListingsActivity> _logger;

    public FindStalePendingListingsActivity(
        EtlDbContext dbContext,
        BlobServiceClient blobService,
        DurableTaskClient durableClient,
        ILogger<FindStalePendingListingsActivity> logger)
    {
        _dbContext = dbContext;
        _blobService = blobService;
        _durableClient = durableClient;
        _logger = logger;
    }

    [Function(nameof(FindStalePendingListingsActivity))]
    public async Task<FindStalePendingListingsResult> Run([ActivityTrigger] int scrapeRunId)
    {
        // Get all pending listings for this run
        var pendingListings = await _dbContext.ScrapeRunListings
            .Where(srl => srl.ScrapeRunId == scrapeRunId && srl.Status == "Pending")
            .Select(srl => srl.ListingId)
            .ToListAsync();

        if (pendingListings.Count == 0)
        {
            return new FindStalePendingListingsResult(new List<StaleListingInfo>());
        }

        var container = _blobService.GetBlobContainerClient("html");
        var staleListings = new List<StaleListingInfo>();

        foreach (var listingId in pendingListings)
        {
            // Check if listing blob exists
            var blobPath = $"{scrapeRunId}/{listingId}/listing.html";
            var blobClient = container.GetBlobClient(blobPath);
            var blobExists = await blobClient.ExistsAsync();

            if (!blobExists.Value)
            {
                // Blob doesn't exist yet, not stale - worker hasn't finished
                continue;
            }

            // Blob exists - check if orchestration exists
            var instanceId = $"etl-{scrapeRunId}-{listingId}";
            var instance = await _durableClient.GetInstanceAsync(instanceId);
            var orchestrationExists = instance != null;

            // If blob exists but no orchestration, it's stale
            if (!orchestrationExists)
            {
                _logger.LogInformation(
                    "Found stale listing: ScrapeRun {ScrapeRunId}, Listing {ListingId} - blob exists but no orchestration",
                    scrapeRunId, listingId);

                staleListings.Add(new StaleListingInfo(listingId, true, false));
            }
        }

        return new FindStalePendingListingsResult(staleListings);
    }
}
