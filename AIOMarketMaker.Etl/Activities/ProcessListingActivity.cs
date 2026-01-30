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
        var listingBlobPath = $"{input.ScrapeRunId}/{input.ListingId}/listing.html";
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

        // Detect product page redirects (eBay sometimes redirects /itm/ to /p/ catalog pages)
        // Product pages aggregate multiple sellers and don't have individual item data
        var canonicalUrl = listingDoc.QuerySelector("link[rel='canonical']")?.GetAttribute("href");
        if (canonicalUrl != null && canonicalUrl.Contains("/p/") && !canonicalUrl.Contains("/itm/"))
        {
            _logger.LogInformation(
                "Listing {ListingId} redirected to product page ({CanonicalUrl}), skipping",
                input.ListingId, canonicalUrl);
            return new ProcessListingResult(Success: true, IsProductPageRedirect: true);
        }

        var extractedListing = _listingParser.ParseProductListing(listingDoc, $"https://ebay.com/itm/{input.ListingId}");

        // Detect redirected listings - eBay sometimes redirects unavailable listings to similar items
        // This means the original listing was delisted/removed
        if (extractedListing.id != null && extractedListing.id != input.ListingId)
        {
            _logger.LogInformation(
                "Listing {ListingId} was delisted (redirected to {ActualId})",
                input.ListingId, extractedListing.id);
            return new ProcessListingResult(Success: true, IsDelisted: true);
        }

        // Validate all required fields are present
        var missingFields = new List<string>();
        if (extractedListing.id == null) missingFields.Add("id");
        if (extractedListing.title == null) missingFields.Add("title");
        if (extractedListing.price == null) missingFields.Add("price");
        if (extractedListing.currency == null) missingFields.Add("currency");
        if (extractedListing.Condition == null) missingFields.Add("condition");
        if (extractedListing.images == null || !extractedListing.images.Any()) missingFields.Add("images");
        if (extractedListing.listingStatus == null) missingFields.Add("listingStatus");

        // purchaseFormat is only required for active listings - sold/ended listings don't have
        // buy buttons in the HTML so the parser can't determine the format
        var isActiveListing = extractedListing.listingStatus == AIOMarketMaker.Models.Ebay.EbayListingStatus.Active
            || extractedListing.listingStatus == AIOMarketMaker.Models.Ebay.EbayListingStatus.OutOfStock;
        if (isActiveListing && extractedListing.purchaseFormat == AIOMarketMaker.Models.Ebay.PurchaseFormat.Unknown)
        {
            missingFields.Add("purchaseFormat");
        }

        if (missingFields.Any())
        {
            _logger.LogWarning(
                "Parse validation failed for listing {ListingId}: missing {Fields}",
                input.ListingId, string.Join(", ", missingFields));
            return new ProcessListingResult(
                Success: false,
                IsParseFailure: true,
                MissingFields: missingFields,
                ErrorMessage: $"Missing: {string.Join(", ", missingFields)}");
        }

        // Try to fetch description (optional)
        string? description = null;
        var descriptionStatus = "missing";

        if (input.HasDescription)
        {
            try
            {
                var descBlobPath = $"{input.ScrapeRunId}/{input.ListingId}/description.html";
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

        // Save to database using SQL MERGE for atomic upsert (handles concurrent writes)
        var mergeActionResult = await _dbContext.Database.SqlQueryRaw<string>(@"
            MERGE INTO Listings WITH (HOLDLOCK) AS target
            USING (SELECT @p0 AS ListingId) AS source
            ON target.ListingId = source.ListingId
            WHEN MATCHED THEN
                UPDATE SET
                    Title = @p1,
                    Price = @p2,
                    Currency = @p3,
                    ShippingCost = @p4,
                    Condition = @p5,
                    ListingStatus = @p6,
                    PurchaseFormat = @p7,
                    ItemSpecifics = @p8,
                    Images = @p9,
                    Location = @p10,
                    EndDateUtc = @p11,
                    Url = @p12,
                    Description = @p13,
                    DescriptionStatus = @p14,
                    UpdatedUtc = GETUTCDATE()
            WHEN NOT MATCHED THEN
                INSERT (ListingId, ScrapeJobId, Title, Price, Currency, ShippingCost,
                        Condition, ListingStatus, PurchaseFormat, ItemSpecifics, Images,
                        Location, EndDateUtc, Url, Description, DescriptionStatus, CreatedUtc)
                VALUES (@p0, @p15, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, GETUTCDATE())
            OUTPUT $action;",
            input.ListingId,                              // @p0
            extractedListing.title,                       // @p1
            extractedListing.price,                       // @p2
            extractedListing.currency,                    // @p3
            extractedListing.shippingCost,                // @p4
            extractedListing.Condition?.ToString(),       // @p5
            extractedListing.listingStatus?.ToString(),   // @p6
            extractedListing.purchaseFormat?.ToString(),  // @p7
            extractedListing.ItemSpecifics,               // @p8
            imagesJson,                                   // @p9
            extractedListing.Location,                    // @p10
            extractedListing.SoldDateUtc,                 // @p11
            extractedListing.Url,                         // @p12
            description,                                  // @p13
            descriptionStatus,                            // @p14
            input.ScrapeJobId                             // @p15
        ).ToListAsync();

        var isNew = mergeActionResult.FirstOrDefault() == "INSERT";

        _logger.LogInformation(
            "Processed listing {ListingId}: {Action}, descriptionStatus={Status}",
            input.ListingId, isNew ? "added" : "updated", descriptionStatus);

        return new ProcessListingResult(
            Success: true,
            IsNewListing: isNew,
            ListingStatus: extractedListing.listingStatus?.ToString());
    }
}
