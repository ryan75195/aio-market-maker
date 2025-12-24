using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;
using System.Net;
using System.Text.Json;

namespace AIOMarketMaker.Functions.Functions;

/// <summary>
/// Anonymous endpoint to check orchestration status and job states.
/// </summary>
public class OrchestrationStatus
{
    private readonly EtlDbContext _dbContext;

    public OrchestrationStatus(EtlDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// GET /api/status - Get running orchestrations and job status
    /// </summary>
    [Function("OrchestrationStatus")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "status")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        // Get recent orchestrations
        var query = new OrchestrationQuery
        {
            PageSize = 20,
            CreatedFrom = DateTime.UtcNow.AddHours(-24)
        };

        var orchestrations = new List<object>();
        await foreach (var metadata in client.GetAllInstancesAsync(query))
        {
            orchestrations.Add(new
            {
                instanceId = metadata.InstanceId,
                name = metadata.Name,
                status = metadata.RuntimeStatus.ToString(),
                createdAt = metadata.CreatedAt,
                lastUpdatedAt = metadata.LastUpdatedAt
            });
        }

        // Get job status
        var jobs = await _dbContext.ScrapeJobs
            .Select(j => new
            {
                j.Id,
                j.SearchTerm,
                j.IsEnabled,
                j.LastRunUtc,
                ListingCount = _dbContext.Listings.Count(l => l.ScrapeJobId == j.Id)
            })
            .ToListAsync();

        var result = new
        {
            timestamp = DateTime.UtcNow,
            orchestrations,
            jobs
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return response;
    }

    /// <summary>
    /// GET /api/status/{instanceId} - Get orchestration history with outputs
    /// </summary>
    [Function("OrchestrationHistory")]
    public async Task<HttpResponseData> GetHistory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "status/{instanceId}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        string instanceId)
    {
        var metadata = await client.GetInstanceAsync(instanceId, getInputsAndOutputs: true);

        if (metadata == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = "Instance not found" });
            return notFound;
        }

        var result = new
        {
            instanceId = metadata.InstanceId,
            name = metadata.Name,
            status = metadata.RuntimeStatus.ToString(),
            createdAt = metadata.CreatedAt,
            lastUpdatedAt = metadata.LastUpdatedAt,
            input = metadata.SerializedInput,
            output = metadata.SerializedOutput,
            failureDetails = metadata.FailureDetails?.ToString()
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return response;
    }
}
