using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Services;

namespace AIOMarketMaker.Etl.Endpoints;

public record ProcessListingRequest(
    int ScrapeRunId,
    int ScrapeRunListingId,
    string ListingId,
    int ScrapeJobId,
    string BlobPath);

public record ProcessListingResponse(
    bool Success,
    string? Status,
    string? ErrorMessage);

public class ProcessListingEndpoint
{
    private readonly IListingProcessorService _processorService;
    private readonly ILogger<ProcessListingEndpoint> _logger;

    public ProcessListingEndpoint(
        IListingProcessorService processorService,
        ILogger<ProcessListingEndpoint> logger)
    {
        _processorService = processorService;
        _logger = logger;
    }

    [Function("ProcessListing")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "process-listing")] HttpRequestData req)
    {
        ProcessListingRequest? input;
        try
        {
            using var reader = new StreamReader(req.Body);
            var bodyString = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(bodyString))
            {
                var emptyResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await emptyResponse.WriteAsJsonAsync(new ProcessListingResponse(false, null, "Request body is empty"));
                return emptyResponse;
            }

            input = JsonSerializer.Deserialize<ProcessListingRequest>(bodyString,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (input == null)
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteAsJsonAsync(new ProcessListingResponse(false, null, "Invalid request body"));
                return badResponse;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse request body as JSON");
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteAsJsonAsync(new ProcessListingResponse(false, null, "Invalid JSON"));
            return badResponse;
        }

        var result = await _processorService.Process(input);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(result);
        return response;
    }
}
