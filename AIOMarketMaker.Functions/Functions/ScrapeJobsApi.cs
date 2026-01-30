using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using System.Net;
using System.Text.Json;

namespace AIOMarketMaker.Functions.Functions;

public class ScrapeJobsApi
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<ScrapeJobsApi> _logger;

    public ScrapeJobsApi(EtlDbContext dbContext, ILogger<ScrapeJobsApi> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/jobs - List all scrape jobs
    /// </summary>
    [Function("GetJobs")]
    public async Task<HttpResponseData> GetJobs(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "jobs")] HttpRequestData req)
    {
        var jobs = await _dbContext.ScrapeJobs
            .Select(j => new
            {
                j.Id,
                j.SearchTerm,
                j.FilterInstructions,
                j.IsEnabled,
                j.LastRunUtc,
                j.CreatedUtc
            })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(jobs);
        return response;
    }

    /// <summary>
    /// GET /api/jobs/{id} - Get a specific job
    /// </summary>
    [Function("GetJob")]
    public async Task<HttpResponseData> GetJob(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "jobs/{id:int}")] HttpRequestData req,
        int id)
    {
        var job = await _dbContext.ScrapeJobs.FindAsync(id);

        if (job == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = $"Job {id} not found" });
            return notFound;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            job.Id,
            job.SearchTerm,
            job.FilterInstructions,
            job.IsEnabled,
            job.LastRunUtc,
            job.CreatedUtc
        });
        return response;
    }

    /// <summary>
    /// POST /api/jobs - Create a new scrape job
    /// Body: { "searchTerm": "ps5 console", "filterInstructions": "optional", "isEnabled": true }
    /// </summary>
    [Function("CreateJob")]
    public async Task<HttpResponseData> CreateJob(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "jobs")] HttpRequestData req)
    {
        var requestBody = await req.ReadAsStringAsync();
        var input = JsonSerializer.Deserialize<CreateJobRequest>(requestBody ?? "{}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (string.IsNullOrWhiteSpace(input?.SearchTerm))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = "searchTerm is required" });
            return badRequest;
        }

        var job = new ScrapeJob
        {
            SearchTerm = input.SearchTerm,
            FilterInstructions = input.FilterInstructions,
            IsEnabled = input.IsEnabled ?? true,
            CreatedUtc = DateTime.UtcNow
        };

        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created scrape job {JobId}: '{SearchTerm}'", job.Id, job.SearchTerm);

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new
        {
            job.Id,
            job.SearchTerm,
            job.FilterInstructions,
            job.IsEnabled,
            job.CreatedUtc
        });
        return response;
    }

    /// <summary>
    /// PUT /api/jobs/{id} - Update a scrape job
    /// Body: { "searchTerm": "...", "filterInstructions": "...", "isEnabled": true/false }
    /// </summary>
    [Function("UpdateJob")]
    public async Task<HttpResponseData> UpdateJob(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "jobs/{id:int}")] HttpRequestData req,
        int id)
    {
        var job = await _dbContext.ScrapeJobs.FindAsync(id);

        if (job == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = $"Job {id} not found" });
            return notFound;
        }

        var requestBody = await req.ReadAsStringAsync();
        var input = JsonSerializer.Deserialize<UpdateJobRequest>(requestBody ?? "{}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (input?.SearchTerm != null)
            job.SearchTerm = input.SearchTerm;

        if (input?.FilterInstructions != null)
            job.FilterInstructions = input.FilterInstructions;

        if (input?.IsEnabled != null)
            job.IsEnabled = input.IsEnabled.Value;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated scrape job {JobId}", job.Id);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            job.Id,
            job.SearchTerm,
            job.FilterInstructions,
            job.IsEnabled,
            job.LastRunUtc,
            job.CreatedUtc
        });
        return response;
    }

    /// <summary>
    /// DELETE /api/jobs/{id} - Delete a scrape job
    /// </summary>
    [Function("DeleteJob")]
    public async Task<HttpResponseData> DeleteJob(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "jobs/{id:int}")] HttpRequestData req,
        int id)
    {
        var job = await _dbContext.ScrapeJobs.FindAsync(id);

        if (job == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = $"Job {id} not found" });
            return notFound;
        }

        _dbContext.ScrapeJobs.Remove(job);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted scrape job {JobId}: '{SearchTerm}'", id, job.SearchTerm);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = $"Job {id} deleted" });
        return response;
    }

    /// <summary>
    /// POST /api/jobs/{id}/enable - Enable a job
    /// </summary>
    [Function("EnableJob")]
    public async Task<HttpResponseData> EnableJob(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "jobs/{id:int}/enable")] HttpRequestData req,
        int id)
    {
        var job = await _dbContext.ScrapeJobs.FindAsync(id);

        if (job == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = $"Job {id} not found" });
            return notFound;
        }

        job.IsEnabled = true;
        await _dbContext.SaveChangesAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { job.Id, job.SearchTerm, job.IsEnabled });
        return response;
    }

    /// <summary>
    /// POST /api/jobs/{id}/disable - Disable a job
    /// </summary>
    [Function("DisableJob")]
    public async Task<HttpResponseData> DisableJob(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "jobs/{id:int}/disable")] HttpRequestData req,
        int id)
    {
        var job = await _dbContext.ScrapeJobs.FindAsync(id);

        if (job == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = $"Job {id} not found" });
            return notFound;
        }

        job.IsEnabled = false;
        await _dbContext.SaveChangesAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { job.Id, job.SearchTerm, job.IsEnabled });
        return response;
    }

    /// <summary>
    /// GET /api/history - List scrape run history
    /// </summary>
    [Function("GetHistory")]
    public async Task<HttpResponseData> GetHistory(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "history")] HttpRequestData req)
    {
        var runs = await _dbContext.ScrapeRuns
            .OrderByDescending(r => r.StartedUtc)
            .Take(50)
            .Select(r => new
            {
                r.Id,
                r.InstanceId,
                r.JobId,
                JobSearchTerm = r.JobId != null
                    ? _dbContext.ScrapeJobs.Where(j => j.Id == r.JobId).Select(j => j.SearchTerm).FirstOrDefault()
                    : null,
                r.TriggerType,
                r.StartedUtc,
                r.CompletedUtc,
                r.Status,
                r.ListingsAdded,
                r.ListingsSkipped,
                r.ListingsFailed,
                r.TotalListingsFound,
                r.ListingsProcessed,
                r.CurrentPhase,
                r.ErrorMessage
            })
            .ToListAsync();

        // Get issue counts for the retrieved runs
        var runIds = runs.Select(r => r.Id).ToList();
        var issueCounts = await _dbContext.ScrapeRunIssues
            .Where(i => runIds.Contains(i.ScrapeRunId))
            .GroupBy(i => i.ScrapeRunId)
            .Select(g => new { ScrapeRunId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ScrapeRunId, x => x.Count);

        // Add issue counts to the response
        var runsWithIssues = runs.Select(r => new
        {
            r.Id,
            r.InstanceId,
            r.JobId,
            r.JobSearchTerm,
            r.TriggerType,
            r.StartedUtc,
            r.CompletedUtc,
            r.Status,
            r.ListingsAdded,
            r.ListingsSkipped,
            r.ListingsFailed,
            r.TotalListingsFound,
            r.ListingsProcessed,
            r.CurrentPhase,
            r.ErrorMessage,
            IssueCount = issueCounts.GetValueOrDefault(r.Id, 0)
        });

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(runsWithIssues);
        return response;
    }

    /// <summary>
    /// GET /api/history/{runId}/issues - Get issues for a specific scrape run
    /// </summary>
    [Function("GetHistoryIssues")]
    public async Task<HttpResponseData> GetHistoryIssues(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "history/{runId:int}/issues")] HttpRequestData req,
        int runId)
    {
        var runExists = await _dbContext.ScrapeRuns.AnyAsync(r => r.Id == runId);
        if (!runExists)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = $"Run {runId} not found" });
            return notFound;
        }

        var issues = await _dbContext.ScrapeRunIssues
            .Where(i => i.ScrapeRunId == runId)
            .OrderBy(i => i.CreatedUtc)
            .Select(i => new
            {
                ListingId = i.ListingId,
                IssueType = i.IssueType,
                ErrorMessage = i.ErrorMessage,
                CreatedUtc = i.CreatedUtc
            })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(issues);
        return response;
    }

    /// <summary>
    /// GET /api/listings/active - List active listings (opportunities)
    /// </summary>
    [Function("GetActiveListings")]
    public async Task<HttpResponseData> GetActiveListings(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "listings/active")] HttpRequestData req)
    {
        var listings = await _dbContext.Listings
            .Include(l => l.ScrapeJob)
            .Where(l => l.ListingStatus == "Active")
            .OrderByDescending(l => l.CreatedUtc)
            .Take(100)
            .Select(l => new
            {
                l.Id,
                l.ListingId,
                l.Title,
                l.Price,
                l.Currency,
                l.ShippingCost,
                l.Url,
                l.Condition,
                l.ListingStatus,
                l.Location,
                l.EndDateUtc,
                l.CreatedUtc,
                SearchTerm = l.ScrapeJob != null ? l.ScrapeJob.SearchTerm : null,
                FirstImage = l.Images
            })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(listings);
        return response;
    }

    /// <summary>
    /// DELETE /api/listings/invalid - Remove listings with NULL price or title
    /// </summary>
    [Function("DeleteInvalidListings")]
    public async Task<HttpResponseData> DeleteInvalidListings(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "listings/invalid")] HttpRequestData req)
    {
        var invalidListings = await _dbContext.Listings
            .Where(l => l.Title == null || l.Price == null)
            .ToListAsync();

        var count = invalidListings.Count;
        _dbContext.Listings.RemoveRange(invalidListings);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted {Count} invalid listings (missing title or price)", count);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { deleted = count });
        return response;
    }

    /// <summary>
    /// GET /api/listings/stats - Get currency breakdown of invalid listings
    /// </summary>
    [Function("GetListingStats")]
    public async Task<HttpResponseData> GetListingStats(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "listings/stats")] HttpRequestData req)
    {
        var twoHoursAgo = DateTime.UtcNow.AddHours(-2);
        var stats = await _dbContext.Listings
            .Where(l => l.CreatedUtc > twoHoursAgo)
            .GroupBy(l => l.Currency)
            .Select(g => new {
                Currency = g.Key,
                Total = g.Count(),
                NullPrice = g.Count(x => x.Price == null),
                NullTitle = g.Count(x => x.Title == null)
            })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(stats);
        return response;
    }

    /// <summary>
    /// GET /api/listings/invalid - Get invalid listings with their URLs
    /// </summary>
    [Function("GetInvalidListings")]
    public async Task<HttpResponseData> GetInvalidListings(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "listings/invalid")] HttpRequestData req)
    {
        var invalidListings = await _dbContext.Listings
            .Where(l => l.Title == null || l.Price == null)
            .OrderByDescending(l => l.CreatedUtc)
            .Take(100)
            .Select(l => new {
                l.Id,
                l.ListingId,
                l.Title,
                l.Price,
                l.Currency,
                l.Url,
                l.CreatedUtc
            })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(invalidListings);
        return response;
    }

    /// <summary>
    /// DELETE /api/listings/all - Clear all listings from the database
    /// </summary>
    [Function("ClearAllListings")]
    public async Task<HttpResponseData> ClearAllListings(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "listings/all")] HttpRequestData req)
    {
        var count = await _dbContext.Listings.CountAsync();

        if (count > 0)
        {
            // Use raw SQL for efficient bulk delete
            await _dbContext.Database.ExecuteSqlRawAsync("DELETE FROM Listings");
            _logger.LogInformation("Cleared {Count} listings from database", count);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { deleted = count });
        return response;
    }

    /// <summary>
    /// DELETE /api/history/all - Clear all scrape run history from the database
    /// </summary>
    [Function("ClearAllHistory")]
    public async Task<HttpResponseData> ClearAllHistory(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "history/all")] HttpRequestData req)
    {
        var count = await _dbContext.ScrapeRuns.CountAsync();

        if (count > 0)
        {
            await _dbContext.Database.ExecuteSqlRawAsync("DELETE FROM ScrapeRuns");
            _logger.LogInformation("Cleared {Count} scrape runs from database", count);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { deleted = count });
        return response;
    }

    /// <summary>
    /// DELETE /api/data/all - Clear all scrape data (listings, run history, and junction table)
    /// </summary>
    [Function("ClearAllData")]
    public async Task<HttpResponseData> ClearAllData(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "data/all")] HttpRequestData req)
    {
        var listingsCount = await _dbContext.Listings.CountAsync();
        var runsCount = await _dbContext.ScrapeRuns.CountAsync();

        // Delete in correct order: Listings first (no FK deps), then ScrapeRuns (cascades to ScrapeRunListings)
        if (listingsCount > 0)
            await _dbContext.Database.ExecuteSqlRawAsync("DELETE FROM Listings");
        if (runsCount > 0)
            await _dbContext.Database.ExecuteSqlRawAsync("DELETE FROM ScrapeRuns");

        _logger.LogInformation("Cleared all data: {Listings} listings, {Runs} scrape runs (+ cascaded ScrapeRunListings)", listingsCount, runsCount);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { deletedListings = listingsCount, deletedRuns = runsCount });
        return response;
    }

    /// <summary>
    /// GET /api/health - Health check endpoint
    /// </summary>
    [Function("HealthCheck")]
    public async Task<HttpResponseData> HealthCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);

        // Check database connectivity
        bool dbConnected = false;
        try
        {
            dbConnected = await _dbContext.Database.CanConnectAsync();
        }
        catch
        {
            // Database connection failed
        }

        await response.WriteAsJsonAsync(new
        {
            status = dbConnected ? "healthy" : "degraded",
            timestamp = DateTime.UtcNow,
            database = dbConnected ? "connected" : "disconnected"
        });

        return response;
    }
}

public record CreateJobRequest(string? SearchTerm, string? FilterInstructions, bool? IsEnabled);
public record UpdateJobRequest(string? SearchTerm, string? FilterInstructions, bool? IsEnabled);
