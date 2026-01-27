using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

public class CheckBlobsActivity
{
    private readonly BlobServiceClient _blobService;
    private readonly ILogger<CheckBlobsActivity> _logger;

    public CheckBlobsActivity(BlobServiceClient blobService, ILogger<CheckBlobsActivity> logger)
    {
        _blobService = blobService;
        _logger = logger;
    }

    [Function(nameof(CheckBlobsActivity))]
    public async Task<BlobState> Run([ActivityTrigger] ListingEtlInput input)
    {
        var container = _blobService.GetBlobContainerClient("html");

        var listingBlobPath = $"{input.JobId}/{input.ListingId}/listing.html";
        var descriptionBlobPath = $"{input.JobId}/{input.ListingId}/description.html";

        var listingBlob = container.GetBlobClient(listingBlobPath);
        var descriptionBlob = container.GetBlobClient(descriptionBlobPath);

        var hasListing = await listingBlob.ExistsAsync();
        var hasDescription = await descriptionBlob.ExistsAsync();

        _logger.LogInformation(
            "Blob check for {ListingId}: listing={HasListing}, description={HasDescription}",
            input.ListingId, hasListing.Value, hasDescription.Value);

        string? missingBlob = null;
        if (!hasListing.Value) missingBlob = "listing";
        else if (!hasDescription.Value) missingBlob = "description";

        return new BlobState(hasListing.Value, hasDescription.Value, missingBlob);
    }
}
