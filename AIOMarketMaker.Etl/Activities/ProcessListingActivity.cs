using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Etl.Models;
using AngleSharp.Html.Parser;

namespace AIOMarketMaker.Etl.Activities;

public class ProcessListingActivity
{
    private readonly BlobServiceClient _blobService;
    private readonly EtlDbContext _dbContext;
    private readonly IListingParser _listingParser;
    private readonly ILogger<ProcessListingActivity> _logger;

    public ProcessListingActivity(
        BlobServiceClient blobService,
        EtlDbContext dbContext,
        IListingParser listingParser,
        ILogger<ProcessListingActivity> logger)
    {
        _blobService = blobService;
        _dbContext = dbContext;
        _listingParser = listingParser;
        _logger = logger;
    }

    [Function(nameof(ProcessListingActivity))]
    public async Task<ProcessListingResult> Run([ActivityTrigger] ProcessListingInput input)
    {
        var container = _blobService.GetBlobContainerClient("html");

        // Fetch listing HTML (required)
        var listingBlobPath = $"{input.ListingId}/listing.html";
        var listingBlob = container.GetBlobClient(listingBlobPath);
        var listingContent = await listingBlob.DownloadContentAsync();
        var listingHtml = listingContent.Value.Content.ToString();

        // Parse listing
        var parser = new HtmlParser();
        var listingDoc = await parser.ParseDocumentAsync(listingHtml);

        // Detect eBay error pages before attempting to parse listing data
        if (listingDoc.QuerySelector(".s-error") != null)
        {
            _logger.LogWarning("eBay error page detected for listing {ListingId}, marking as failed", input.ListingId);
            return new ProcessListingResult(Success: false, ErrorMessage: "eBay error page");
        }
        var extractedListing = _listingParser.ParseProductListing(listingDoc, $"https://ebay.com/itm/{input.ListingId}");

        // Try to fetch description (optional)
        string? description = null;
        var descriptionStatus = "missing";

        if (input.HasDescription)
        {
            try
            {
                var descBlobPath = $"{input.ListingId}/description.html";
                var descBlob = container.GetBlobClient(descBlobPath);
                var descContent = await descBlob.DownloadContentAsync();
                var descHtml = descContent.Value.Content.ToString();

                var descDoc = await parser.ParseDocumentAsync(descHtml);
                description = _listingParser.ParseDescription(descDoc);
                descriptionStatus = string.IsNullOrEmpty(description) ? "failed" : "complete";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse description for {ListingId}", input.ListingId);
                descriptionStatus = "failed";
            }
        }

        // Serialize images to JSON
        string? imagesJson = null;
        if (extractedListing.images != null && extractedListing.images.Any())
        {
            imagesJson = JsonSerializer.Serialize(extractedListing.images);
        }

        // Save to database - use LINQ query since primary key is Id, not ListingId
        var existing = await _dbContext.Listings
            .FirstOrDefaultAsync(l => l.ListingId == input.ListingId);

        if (existing != null)
        {
            // Update existing
            existing.Title = extractedListing.title;
            existing.Price = extractedListing.price;
            existing.Currency = extractedListing.currency;
            existing.ShippingCost = extractedListing.shippingCost;
            existing.Condition = extractedListing.Condition?.ToString();
            existing.ListingStatus = extractedListing.listingStatus?.ToString();
            existing.PurchaseFormat = extractedListing.purchaseFormat?.ToString();
            existing.ItemSpecifics = extractedListing.ItemSpecifics;
            existing.Images = imagesJson;
            existing.Location = extractedListing.Location;
            existing.EndDateUtc = extractedListing.SoldDateUtc;
            existing.Url = extractedListing.Url;
            existing.Description = description;
            existing.DescriptionStatus = descriptionStatus;
            existing.UpdatedUtc = DateTime.UtcNow;
        }
        else
        {
            // Insert new
            var listing = new Listing
            {
                ListingId = input.ListingId,
                ScrapeJobId = input.ScrapeJobId,
                Title = extractedListing.title,
                Price = extractedListing.price,
                Currency = extractedListing.currency,
                ShippingCost = extractedListing.shippingCost,
                Condition = extractedListing.Condition?.ToString(),
                ListingStatus = extractedListing.listingStatus?.ToString(),
                PurchaseFormat = extractedListing.purchaseFormat?.ToString(),
                ItemSpecifics = extractedListing.ItemSpecifics,
                Images = imagesJson,
                Location = extractedListing.Location,
                EndDateUtc = extractedListing.SoldDateUtc,
                Url = extractedListing.Url,
                Description = description,
                DescriptionStatus = descriptionStatus,
                CreatedUtc = DateTime.UtcNow
            };
            _dbContext.Listings.Add(listing);
        }

        var isNew = existing == null;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Processed listing {ListingId}: {Action}, descriptionStatus={Status}",
            input.ListingId, isNew ? "added" : "updated", descriptionStatus);

        return new ProcessListingResult(Success: true, IsNewListing: isNew);
    }
}
