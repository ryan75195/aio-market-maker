using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Services;
using AIOMarketMaker.Etl.Data;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text.Json;

namespace AIOMarketMaker.Functions;

/// <summary>
/// Durable Functions orchestration for ETL job processing.
/// Provides async job execution with status tracking.
/// </summary>
public class EtlOrchestration
{
    private readonly IJobRunner _jobRunner;
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<EtlOrchestration> _logger;

    public EtlOrchestration(
        IJobRunner jobRunner,
        EtlDbContext dbContext,
        ILogger<EtlOrchestration> logger)
    {
        _jobRunner = jobRunner;
        _dbContext = dbContext;
        _logger = logger;
    }

    #region HTTP Triggers

    /// <summary>
    /// Start ETL for a single job by ID
    /// POST /api/etl/run/{jobId}
    /// </summary>
    [Function("StartJobEtl")]
    public async Task<HttpResponseData> StartJobEtl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "etl/run/{jobId:int}")] HttpRequestData req,
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

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(RunSingleJobOrchestration),
            new JobOrchestrationInput(jobId, job.SearchTerm));

        _logger.LogInformation("Started orchestration {InstanceId} for job {JobId}", instanceId, jobId);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new
        {
            instanceId,
            jobId,
            searchTerm = job.SearchTerm,
            statusUrl = $"/api/etl/status/{instanceId}"
        });
        return response;
    }

    /// <summary>
    /// Start ETL for all enabled jobs
    /// POST /api/etl/run-all
    /// </summary>
    [Function("StartAllJobsEtl")]
    public async Task<HttpResponseData> StartAllJobsEtl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "etl/run-all")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var jobIds = await _dbContext.ScrapeJobs
            .Where(j => j.IsEnabled)
            .Select(j => j.Id)
            .ToListAsync();

        if (jobIds.Count == 0)
        {
            var noJobs = req.CreateResponse(HttpStatusCode.OK);
            await noJobs.WriteAsJsonAsync(new { message = "No enabled jobs found", instances = Array.Empty<object>() });
            return noJobs;
        }

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(RunAllJobsOrchestration),
            jobIds);

        _logger.LogInformation("Started orchestration {InstanceId} for {Count} jobs", instanceId, jobIds.Count);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new
        {
            instanceId,
            jobCount = jobIds.Count,
            jobIds,
            statusUrl = $"/api/etl/status/{instanceId}"
        });
        return response;
    }

    /// <summary>
    /// Get orchestration status
    /// GET /api/etl/status/{instanceId}
    /// </summary>
    [Function("GetEtlStatus")]
    public async Task<HttpResponseData> GetEtlStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "etl/status/{instanceId}")] HttpRequestData req,
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

    /// <summary>
    /// Terminate a running orchestration
    /// POST /api/etl/terminate/{instanceId}
    /// </summary>
    [Function("TerminateEtl")]
    public async Task<HttpResponseData> TerminateEtl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "etl/terminate/{instanceId}")] HttpRequestData req,
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

        await client.TerminateInstanceAsync(instanceId, "Terminated by user");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = $"Orchestration {instanceId} terminated" });
        return response;
    }

    #endregion

    #region Orchestrations

    /// <summary>
    /// Orchestration for running a single job
    /// </summary>
    [Function(nameof(RunSingleJobOrchestration))]
    public async Task<JobRunResult> RunSingleJobOrchestration(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<JobOrchestrationInput>()!;
        var logger = context.CreateReplaySafeLogger<EtlOrchestration>();

        logger.LogInformation("Starting orchestration for job {JobId}: {SearchTerm}", input.JobId, input.SearchTerm);

        var result = await context.CallActivityAsync<JobRunResult>(nameof(RunJobActivity), input.JobId);

        logger.LogInformation("Job {JobId} completed: Success={Success}, Products={Products}",
            input.JobId, result.Success, result.ProductsSaved);

        return result;
    }

    /// <summary>
    /// Orchestration for running all enabled jobs sequentially
    /// </summary>
    [Function(nameof(RunAllJobsOrchestration))]
    public async Task<AllJobsResult> RunAllJobsOrchestration(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var jobIds = context.GetInput<List<int>>()!;
        var logger = context.CreateReplaySafeLogger<EtlOrchestration>();

        logger.LogInformation("Starting orchestration for {Count} jobs", jobIds.Count);

        var results = new List<JobRunResult>();

        foreach (var jobId in jobIds)
        {
            try
            {
                var result = await context.CallActivityAsync<JobRunResult>(nameof(RunJobActivity), jobId);
                results.Add(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error running job {JobId}", jobId);
                results.Add(new JobRunResult(jobId, false, 0, 0, 0, ex.Message));
            }
        }

        var summary = new AllJobsResult(
            TotalJobs: results.Count,
            SuccessfulJobs: results.Count(r => r.Success),
            FailedJobs: results.Count(r => !r.Success),
            TotalListingsFound: results.Sum(r => r.ListingsFound),
            TotalNewListings: results.Sum(r => r.NewListingsFetched),
            TotalProductsSaved: results.Sum(r => r.ProductsSaved),
            JobResults: results
        );

        logger.LogInformation("All jobs completed: {Success}/{Total} successful, {Products} products saved",
            summary.SuccessfulJobs, summary.TotalJobs, summary.TotalProductsSaved);

        return summary;
    }

    #endregion

    #region Activities

    /// <summary>
    /// Activity function that runs a single ETL job
    /// </summary>
    [Function(nameof(RunJobActivity))]
    public async Task<JobRunResult> RunJobActivity(
        [ActivityTrigger] int jobId)
    {
        _logger.LogInformation("Activity: Running job {JobId}", jobId);
        return await _jobRunner.RunJob(jobId);
    }

    #endregion
}

#region DTOs

public record JobOrchestrationInput(int JobId, string SearchTerm);

public record AllJobsResult(
    int TotalJobs,
    int SuccessfulJobs,
    int FailedJobs,
    int TotalListingsFound,
    int TotalNewListings,
    int TotalProductsSaved,
    List<JobRunResult> JobResults
);

#endregion
