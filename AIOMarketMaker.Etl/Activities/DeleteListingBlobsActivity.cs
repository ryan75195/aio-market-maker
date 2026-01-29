using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Etl.Activities;

public record DeleteBlobsInput(int ScrapeRunId, string ListingId);

public class DeleteListingBlobsActivity
{
    private readonly BlobServiceClient _blobService;
    private readonly ILogger<DeleteListingBlobsActivity> _logger;

    public DeleteListingBlobsActivity(
        BlobServiceClient blobService,
        ILogger<DeleteListingBlobsActivity> logger)
    {
        _blobService = blobService;
        _logger = logger;
    }

    [Function(nameof(DeleteListingBlobsActivity))]
    public async Task Run([ActivityTrigger] DeleteBlobsInput input)
    {
        var container = _blobService.GetBlobContainerClient("html");

        var listingBlob = container.GetBlobClient($"{input.ScrapeRunId}/{input.ListingId}/listing.html");
        var descBlob = container.GetBlobClient($"{input.ScrapeRunId}/{input.ListingId}/description.html");

        await listingBlob.DeleteIfExistsAsync();
        await descBlob.DeleteIfExistsAsync();

        _logger.LogDebug("Deleted blobs for {ScrapeRunId}/{ListingId}", input.ScrapeRunId, input.ListingId);
    }
}
