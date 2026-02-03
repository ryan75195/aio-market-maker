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
using AIOMarketMaker.Core.Services;

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

        // Parse HTML with AngleSharp
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(request => request.Content(html));
        var listingUrl = $"https://www.ebay.co.uk/itm/{input.ListingId}";

        // Check if this is a product catalog page (redirected from /itm/ to /p/)
        // These are valid eBay pages, just not individual item listings - skip them
        if (_listingParser.IsProductCatalogPage(document))
        {
            _logger.LogInformation("Listing {ListingId} redirected to product catalog page, skipping",
                input.ListingId);

            var skippedSrl = existingEntry ?? await _dbContext.ScrapeRunListings
                .FirstOrDefaultAsync(srl => srl.ScrapeRunId == input.ScrapeRunId && srl.ListingId == input.ListingId);
            if (skippedSrl != null)
            {
                skippedSrl.Status = "Skipped";
                skippedSrl.FailureReason = "PRODUCT_PAGE";
                skippedSrl.CompletedUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();
            }

            await IncrementScrapeRunCountersAsync(input.ScrapeRunId, "skipped");

            var productPageResponse = req.CreateResponse(HttpStatusCode.OK);
            await productPageResponse.WriteAsJsonAsync(new ProcessListingResponse(true, "skipped", null));
            return productPageResponse;
        }

        // Parse the listing
        var parsedListing = _listingParser.ParseProductListing(document, listingUrl);

        // Validate parser extracted required data - if no title, it's an error page or bot detection
        if (string.IsNullOrEmpty(parsedListing.title))
        {
            _logger.LogWarning("Failed to parse listing {ListingId}: no title extracted (possible error page, HTML size: {Size} bytes)",
                input.ListingId, html.Length);

            // Mark the ScrapeRunListing as Failed so it doesn't stay Pending forever
            var failedSrl = existingEntry ?? await _dbContext.ScrapeRunListings
                .FirstOrDefaultAsync(srl => srl.ScrapeRunId == input.ScrapeRunId && srl.ListingId == input.ListingId);
            if (failedSrl != null)
            {
                failedSrl.Status = "Failed";
                failedSrl.FailureReason = "PARSE_FAILED";
                failedSrl.CompletedUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();
            }

            // Increment the failed counter on ScrapeRun
            await IncrementScrapeRunCountersAsync(input.ScrapeRunId, "failed");

            var parseFailedResponse = req.CreateResponse(HttpStatusCode.OK);
            await parseFailedResponse.WriteAsJsonAsync(new ProcessListingResponse(false, "failed", "Failed to parse listing - no title extracted"));
            return parseFailedResponse;
        }

        // Check if listing already exists (upsert logic)
        var existingListing = await _dbContext.Listings
            .FirstOrDefaultAsync(l => l.ListingId == input.ListingId && l.ScrapeJobId == input.ScrapeJobId);

        var newStatus = parsedListing.listingStatus?.ToString();

        // Validate status transition if listing exists
        if (existingListing != null && !ListingStatusHelper.CanUpdateStatus(existingListing.ListingStatus, newStatus))
        {
            _logger.LogWarning("Invalid status transition for {ListingId}: {OldStatus} -> {NewStatus}",
                input.ListingId, existingListing.ListingStatus, newStatus);

            // Increment skip counter
            await IncrementScrapeRunCountersAsync(input.ScrapeRunId, "skipped");

            // Mark ScrapeRunListing as Skipped
            var skippedSrl = existingEntry ?? await _dbContext.ScrapeRunListings
                .FirstOrDefaultAsync(srl => srl.ScrapeRunId == input.ScrapeRunId && srl.ListingId == input.ListingId);
            if (skippedSrl != null)
            {
                skippedSrl.Status = "Skipped";
                skippedSrl.CompletedUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();
            }

            var skipResponse = req.CreateResponse(HttpStatusCode.OK);
            await skipResponse.WriteAsJsonAsync(new ProcessListingResponse(
                true, "skipped", $"Invalid status transition: {existingListing.ListingStatus} -> {newStatus}"));
            return skipResponse;
        }

        // Capture old values before updating (for history tracking)
        var oldStatus = existingListing?.ListingStatus;
        var oldPrice = existingListing?.Price;

        string status;
        Listing listing;
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
            listing = existingListing;
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
            listing = newListing;
        }

        // Update ScrapeRunListing status
        var scrapeRunListing = existingEntry ?? await _dbContext.ScrapeRunListings
            .FirstOrDefaultAsync(srl => srl.ScrapeRunId == input.ScrapeRunId && srl.ListingId == input.ListingId);

        if (scrapeRunListing != null)
        {
            scrapeRunListing.Status = "Complete";
            scrapeRunListing.CompletedUtc = DateTime.UtcNow;
        }

        // Save the listing and ScrapeRunListing changes first
        await _dbContext.SaveChangesAsync();

        // Create ListingStatusHistory record on status/price changes or initial scrape
        var statusChanged = existingListing != null && oldStatus != newStatus;
        var priceChanged = existingListing != null && oldPrice != parsedListing.price;

        if (existingListing == null || statusChanged || priceChanged)
        {
            var historySource = existingListing == null
                ? "InitialScrape"
                : (statusChanged ? "StatusUpdate" : "PriceUpdate");

            var historyRecord = new ListingStatusHistory
            {
                ListingId = listing.Id,
                ListingStatus = newStatus ?? "Unknown",
                Price = parsedListing.price,
                SoldDateUtc = parsedListing.SoldDateUtc,
                RecordedUtc = DateTime.UtcNow,
                Source = historySource
            };
            _dbContext.ListingStatusHistory.Add(historyRecord);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Created ListingStatusHistory for {ListingId}: {Source} ({Status}, {Price})",
                input.ListingId, historySource, newStatus, parsedListing.price);
        }

        // Increment ScrapeRun counters atomically to prevent race conditions
        // When multiple workers process listings concurrently, read-modify-write
        // causes lost updates. Use atomic SQL UPDATE instead.
        await IncrementScrapeRunCountersAsync(input.ScrapeRunId, status, newStatus);

        _logger.LogInformation("Processed listing {ListingId} with status {Status}", input.ListingId, status);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new ProcessListingResponse(true, status, null));
        return response;
    }

    /// <summary>
    /// Atomically increment ScrapeRun counters using SQL UPDATE to prevent race conditions.
    /// Multiple workers processing different listings concurrently were causing lost updates
    /// with the previous read-modify-write pattern.
    /// Also checks if this was the last listing and marks the run as Completed.
    /// </summary>
    private async Task IncrementScrapeRunCountersAsync(int scrapeRunId, string status, string? listingStatus = null)
    {
        // Determine if this is a sold listing (for differentiating added counters)
        var isSold = listingStatus == "Sold";

        // Check if using a relational database (SQL Server, SQLite, etc.)
        // InMemory database doesn't support raw SQL, so use EF Core for tests
        if (_dbContext.Database.IsRelational())
        {
            // Use atomic SQL UPDATE to prevent lost updates from concurrent workers
            string sql = status switch
            {
                "added" when isSold => "UPDATE ScrapeRuns SET ListingsProcessed = ListingsProcessed + 1, ListingsAddedSold = ListingsAddedSold + 1 WHERE Id = {0}",
                "added" => "UPDATE ScrapeRuns SET ListingsProcessed = ListingsProcessed + 1, ListingsAddedActive = ListingsAddedActive + 1 WHERE Id = {0}",
                "updated" => "UPDATE ScrapeRuns SET ListingsProcessed = ListingsProcessed + 1, ListingsUpdated = ListingsUpdated + 1 WHERE Id = {0}",
                "skipped" => "UPDATE ScrapeRuns SET ListingsProcessed = ListingsProcessed + 1, ListingsSkipped = ListingsSkipped + 1 WHERE Id = {0}",
                "failed" => "UPDATE ScrapeRuns SET ListingsProcessed = ListingsProcessed + 1, ListingsFailed = ListingsFailed + 1 WHERE Id = {0}",
                _ => "UPDATE ScrapeRuns SET ListingsProcessed = ListingsProcessed + 1 WHERE Id = {0}"
            };

            var rowsAffected = await _dbContext.Database.ExecuteSqlRawAsync(sql, scrapeRunId);

            if (rowsAffected == 0)
            {
                _logger.LogWarning("ScrapeRun {ScrapeRunId} not found while incrementing counters", scrapeRunId);
                return;
            }

            // Check if this was the last listing and mark as Completed
            // Use atomic SQL to avoid race conditions with other workers
            var completionSql = @"
                UPDATE ScrapeRuns
                SET Status = 'Completed', CurrentPhase = 'Completed', CompletedUtc = {1}
                WHERE Id = {0}
                  AND (Status = 'Running' OR Status = 'Indexing')
                  AND CurrentPhase = 'Indexing'
                  AND TotalListingsFound > 0
                  AND ListingsProcessed >= (TotalListingsFound - ListingsFilteredPreQueue)";

            var completedRows = await _dbContext.Database.ExecuteSqlRawAsync(completionSql, scrapeRunId, DateTime.UtcNow);

            if (completedRows > 0)
            {
                _logger.LogInformation("Marked ScrapeRun {ScrapeRunId} as Completed (last listing processed)", scrapeRunId);
            }
        }
        else
        {
            // Fall back to EF Core for InMemory database (tests)
            var scrapeRun = await _dbContext.ScrapeRuns.FirstOrDefaultAsync(sr => sr.Id == scrapeRunId);
            if (scrapeRun != null)
            {
                scrapeRun.ListingsProcessed++;
                if (status == "added" && isSold) scrapeRun.ListingsAddedSold++;
                else if (status == "added") scrapeRun.ListingsAddedActive++;
                else if (status == "updated") scrapeRun.ListingsUpdated++;
                else if (status == "skipped") scrapeRun.ListingsSkipped++;
                else if (status == "failed") scrapeRun.ListingsFailed++;

                // Check if this was the last listing and mark as Completed
                var listingsToProcess = scrapeRun.TotalListingsFound - scrapeRun.ListingsFilteredPreQueue;
                if ((scrapeRun.Status == "Running" || scrapeRun.Status == "Indexing") &&
                    scrapeRun.CurrentPhase == "Indexing" &&
                    scrapeRun.TotalListingsFound > 0 &&
                    scrapeRun.ListingsProcessed >= listingsToProcess)
                {
                    scrapeRun.Status = "Completed";
                    scrapeRun.CurrentPhase = "Completed";
                    scrapeRun.CompletedUtc = DateTime.UtcNow;
                    _logger.LogInformation("Marked ScrapeRun {ScrapeRunId} as Completed (last listing processed)", scrapeRunId);
                }

                await _dbContext.SaveChangesAsync();
            }
        }
    }
}
