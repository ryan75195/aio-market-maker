using System.Net;
using System.Text.Json;
using AngleSharp;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;

namespace AIOMarketMaker.Etl.Endpoints;

public record ProcessListingRequest(
    int ScrapeRunId,
    int ScrapeRunListingId,
    string ListingId,
    int ScrapeJobId,
    string BlobPath);

public record ProcessListingResponse(
    bool Success,
    string? Status,  // "added", "updated", "skipped", "failed"
    string? ErrorMessage);

public class ProcessListingEndpoint
{
    private readonly BlobServiceClient _blobService;
    private readonly EtlDbContext _dbContext;
    private readonly IListingParser _listingParser;
    private readonly ILogger<ProcessListingEndpoint> _logger;

    public ProcessListingEndpoint(
        BlobServiceClient blobService,
        EtlDbContext dbContext,
        IListingParser listingParser,
        ILogger<ProcessListingEndpoint> logger)
    {
        _blobService = blobService;
        _dbContext = dbContext;
        _listingParser = listingParser;
        _logger = logger;
    }

    [Function("ProcessListing")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "process-listing")] HttpRequestData req)
    {
        ProcessListingRequest? input;
        try
        {
            // Read body as string first to check if it's valid
            using var reader = new StreamReader(req.Body);
            var bodyString = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(bodyString))
            {
                _logger.LogWarning("Request body is empty");
                var emptyResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await emptyResponse.WriteAsJsonAsync(new ProcessListingResponse(false, null, "Request body is empty"));
                return emptyResponse;
            }

            input = System.Text.Json.JsonSerializer.Deserialize<ProcessListingRequest>(bodyString,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (input == null)
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteAsJsonAsync(new ProcessListingResponse(false, null, "Invalid request body"));
                return badResponse;
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse request body as JSON");
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteAsJsonAsync(new ProcessListingResponse(false, null, "Invalid JSON"));
            return badResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process request");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new ProcessListingResponse(false, null, ex.Message));
            return errorResponse;
        }

        // Check if already processed (idempotency)
        var existingEntry = await _dbContext.ScrapeRunListings
            .FirstOrDefaultAsync(srl => srl.ScrapeRunId == input.ScrapeRunId && srl.ListingId == input.ListingId);

        if (existingEntry?.Status == "Complete")
        {
            _logger.LogInformation("Listing {ListingId} already processed, skipping", input.ListingId);
            var skipResponse = req.CreateResponse(HttpStatusCode.OK);
            await skipResponse.WriteAsJsonAsync(new ProcessListingResponse(true, "skipped", null));
            return skipResponse;
        }

        // Get the blob
        var containerClient = _blobService.GetBlobContainerClient("html");
        var blobClient = containerClient.GetBlobClient(input.BlobPath);

        // Check if blob exists
        var existsResponse = await blobClient.ExistsAsync();
        if (!existsResponse.Value)
        {
            _logger.LogWarning("Blob not found: {BlobPath}", input.BlobPath);
            var notFoundResponse = req.CreateResponse(HttpStatusCode.OK);
            await notFoundResponse.WriteAsJsonAsync(new ProcessListingResponse(false, "failed", "Blob not found"));
            return notFoundResponse;
        }

        // Download blob content
        var downloadResult = await blobClient.DownloadContentAsync();
        var html = downloadResult.Value.Content.ToString();

        // Check for error page (bot detection, GDPR, etc.) - real eBay pages are > 100KB
        const int MinValidHtmlSize = 100 * 1024; // 100KB
        if (html.Length < MinValidHtmlSize)
        {
            _logger.LogWarning("Possible error page detected for {ListingId}: HTML size {Size} bytes < {Threshold} bytes",
                input.ListingId, html.Length, MinValidHtmlSize);
            var errorPageResponse = req.CreateResponse(HttpStatusCode.OK);
            await errorPageResponse.WriteAsJsonAsync(new ProcessListingResponse(false, "failed", "Detected error page (HTML too small)"));
            return errorPageResponse;
        }

        // Parse HTML with AngleSharp
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(request => request.Content(html));
        var listingUrl = $"https://www.ebay.co.uk/itm/{input.ListingId}";

        // Parse the listing
        var parsedListing = _listingParser.ParseProductListing(document, listingUrl);

        // Check if listing already exists (upsert logic)
        var existingListing = await _dbContext.Listings
            .FirstOrDefaultAsync(l => l.ListingId == input.ListingId && l.ScrapeJobId == input.ScrapeJobId);

        string status;
        if (existingListing != null)
        {
            // Update existing listing
            existingListing.Title = parsedListing.title;
            existingListing.Price = parsedListing.price;
            existingListing.Currency = parsedListing.currency;
            existingListing.ShippingCost = parsedListing.shippingCost;
            existingListing.Condition = parsedListing.Condition?.ToString();
            existingListing.ListingStatus = parsedListing.listingStatus?.ToString();
            existingListing.PurchaseFormat = parsedListing.purchaseFormat?.ToString();
            existingListing.ItemSpecifics = parsedListing.ItemSpecifics;
            existingListing.Images = parsedListing.images != null ? JsonSerializer.Serialize(parsedListing.images) : null;
            existingListing.Location = parsedListing.Location;
            existingListing.Url = parsedListing.Url;
            existingListing.UpdatedUtc = DateTime.UtcNow;
            status = "updated";
        }
        else
        {
            // Create new listing
            var newListing = new Listing
            {
                ListingId = input.ListingId,
                ScrapeJobId = input.ScrapeJobId,
                Title = parsedListing.title,
                Price = parsedListing.price,
                Currency = parsedListing.currency,
                ShippingCost = parsedListing.shippingCost,
                Condition = parsedListing.Condition?.ToString(),
                ListingStatus = parsedListing.listingStatus?.ToString(),
                PurchaseFormat = parsedListing.purchaseFormat?.ToString(),
                ItemSpecifics = parsedListing.ItemSpecifics,
                Images = parsedListing.images != null ? JsonSerializer.Serialize(parsedListing.images) : null,
                Location = parsedListing.Location,
                Url = parsedListing.Url,
                CreatedUtc = DateTime.UtcNow
            };
            _dbContext.Listings.Add(newListing);
            status = "added";
        }

        // Update ScrapeRunListing status
        var scrapeRunListing = existingEntry ?? await _dbContext.ScrapeRunListings
            .FirstOrDefaultAsync(srl => srl.ScrapeRunId == input.ScrapeRunId && srl.ListingId == input.ListingId);

        if (scrapeRunListing != null)
        {
            scrapeRunListing.Status = "Complete";
            scrapeRunListing.CompletedUtc = DateTime.UtcNow;
        }

        // Increment ScrapeRun.ListingsProcessed
        var scrapeRun = await _dbContext.ScrapeRuns.FirstOrDefaultAsync(sr => sr.Id == input.ScrapeRunId);
        if (scrapeRun != null)
        {
            scrapeRun.ListingsProcessed++;
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Processed listing {ListingId} with status {Status}", input.ListingId, status);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new ProcessListingResponse(true, status, null));
        return response;
    }
}
