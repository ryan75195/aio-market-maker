using System.Net;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
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

        // TODO: Process listing (next task)
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new ProcessListingResponse(true, "processed", null));
        return response;
    }
}
