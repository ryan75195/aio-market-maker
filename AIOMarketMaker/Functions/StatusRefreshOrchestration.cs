using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Services;
using AIOMarketMaker.Etl.Data;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace AIOMarketMaker.Functions;

/// <summary>
/// Durable Functions orchestration for refreshing listing statuses.
/// Checks active listings for status changes (Active -> Sold, etc.)
/// </summary>
public class StatusRefreshOrchestration
{
    private readonly IStatusRefreshRunner _statusRefreshRunner;
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<StatusRefreshOrchestration> _logger;

    public StatusRefreshOrchestration(
        IStatusRefreshRunner statusRefreshRunner,
        EtlDbContext dbContext,
        ILogger<StatusRefreshOrchestration> logger)
    {
        _statusRefreshRunner = statusRefreshRunner;
        _dbContext = dbContext;
        _logger = logger;
    }

    #region HTTP Triggers

    /// <summary>
    /// Refresh status for all active listings across all jobs
    /// POST /api/status/refresh
    /// </summary>
    [Function("StartStatusRefreshAll")]
    public async Task<HttpResponseData> StartStatusRefreshAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "status/refresh")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var activeCount = await _dbContext.Listings
            .CountAsync(l => l.ListingStatus == "Active");

        if (activeCount == 0)
        {
            var noItems = req.CreateResponse(HttpStatusCode.OK);
            await noItems.WriteAsJsonAsync(new { message = "No active listings to refresh" });
            return noItems;
        }

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(RefreshStatusOrchestration),
            new StatusRefreshInput(null));

        _logger.LogInformation("Started status refresh orchestration {InstanceId} for {Count} active listings",
            instanceId, activeCount);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new
        {
            instanceId,
            activeListings = activeCount,
            statusUrl = $"/api/status/refresh/status/{instanceId}"
        });
        return response;
    }

    /// <summary>
    /// Refresh status for active listings in a specific job
    /// POST /api/status/refresh/{jobId}
    /// </summary>
    [Function("StartStatusRefreshJob")]
    public async Task<HttpResponseData> StartStatusRefreshJob(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "status/refresh/{jobId:int}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        int jobId)
    {
        var job = await _dbContext.ScrapeJobs.FindAsync(jobId);
        if (job == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = $"Job {jobId} not found" });
            return notFound;
        }

        var activeCount = await _dbContext.Listings
            .CountAsync(l => l.ScrapeJobId == jobId && l.ListingStatus == "Active");

        if (activeCount == 0)
        {
            var noItems = req.CreateResponse(HttpStatusCode.OK);
            await noItems.WriteAsJsonAsync(new { message = $"No active listings in job {jobId} to refresh" });
            return noItems;
        }

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(RefreshStatusOrchestration),
            new StatusRefreshInput(jobId));

        _logger.LogInformation("Started status refresh orchestration {InstanceId} for job {JobId} ({Count} active listings)",
            instanceId, jobId, activeCount);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new
        {
            instanceId,
            jobId,
            searchTerm = job.SearchTerm,
            activeListings = activeCount,
            statusUrl = $"/api/status/refresh/status/{instanceId}"
        });
        return response;
    }

    /// <summary>
    /// Get status refresh orchestration status
    /// GET /api/status/refresh/status/{instanceId}
    /// </summary>
    [Function("GetStatusRefreshStatus")]
    public async Task<HttpResponseData> GetStatusRefreshStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "status/refresh/status/{instanceId}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        string instanceId)
    {
        var metadata = await client.GetInstanceAsync(instanceId);

        if (metadata == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = $"Orchestration {instanceId} not found" });
            return notFound;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            instanceId = metadata.InstanceId,
            name = metadata.Name,
            status = metadata.RuntimeStatus.ToString(),
            createdAt = metadata.CreatedAt,
            lastUpdatedAt = metadata.LastUpdatedAt,
            input = metadata.SerializedInput,
            output = metadata.SerializedOutput
        });
        return response;
    }

    #endregion

    #region Orchestrations

    /// <summary>
    /// Orchestration for refreshing listing statuses
    /// </summary>
    [Function(nameof(RefreshStatusOrchestration))]
    public async Task<StatusRefreshResult> RefreshStatusOrchestration(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<StatusRefreshInput>()!;
        var logger = context.CreateReplaySafeLogger<StatusRefreshOrchestration>();

        logger.LogInformation("Starting status refresh orchestration for {Scope}",
            input.JobId.HasValue ? $"job {input.JobId}" : "all active listings");

        var result = await context.CallActivityAsync<StatusRefreshResult>(
            nameof(RefreshStatusActivity), input.JobId);

        logger.LogInformation("Status refresh completed: checked {Checked}, updated {Updated}",
            result.Checked, result.Updated);

        return result;
    }

    #endregion

    #region Activities

    /// <summary>
    /// Activity function that performs the status refresh
    /// </summary>
    [Function(nameof(RefreshStatusActivity))]
    public async Task<StatusRefreshResult> RefreshStatusActivity(
        [ActivityTrigger] int? jobId)
    {
        _logger.LogInformation("Activity: Refreshing status for {Scope}",
            jobId.HasValue ? $"job {jobId}" : "all active listings");
        return await _statusRefreshRunner.RefreshActiveListingsAsync(jobId);
    }

    #endregion
}

#region DTOs

public record StatusRefreshInput(int? JobId);

#endregion
