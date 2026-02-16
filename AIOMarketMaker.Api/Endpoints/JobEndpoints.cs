using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Api.Endpoints;

public record CreateJobRequest(string? SearchTerm, string? FilterInstructions, bool? IsEnabled, IEnumerable<int>? CategoryIds);
public record UpdateJobRequest(string? SearchTerm, string? FilterInstructions, bool? IsEnabled, IEnumerable<int>? CategoryIds);
public record JobCategoryInfo(int Id, string Name);
public record JobResponse(int Id, string SearchTerm, string? FilterInstructions, bool IsEnabled, DateTime? LastRunUtc, DateTime CreatedUtc, IEnumerable<JobCategoryInfo> Categories);
public record JobToggleResponse(int Id, string SearchTerm, bool IsEnabled);
public record ErrorResponse(string Error);
public record MessageResponse(string Message);

public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/jobs");
        group.MapGet("/", GetJobs);
        group.MapGet("/{id:int}", GetJob);
        group.MapPost("/", CreateJob);
        group.MapPut("/{id:int}", UpdateJob);
        group.MapDelete("/{id:int}", DeleteJob);
        group.MapPost("/{id:int}/categories", SetJobCategories);
        group.MapPost("/{id:int}/enable", EnableJob);
        group.MapPost("/{id:int}/disable", DisableJob);
    }

    private static async Task<IResult> GetJobs(EtlDbContext db)
    {
        var lastRunByJob = await db.ScrapeRuns
            .GroupBy(r => r.JobId)
            .Select(g => new { JobId = g.Key, LastRun = g.Max(r => r.StartedUtc) })
            .ToDictionaryAsync(x => x.JobId, x => x.LastRun);

        var jobCategories = await db.JobCategories
            .Include(jc => jc.Category)
            .GroupBy(jc => jc.JobId)
            .Select(g => new { JobId = g.Key, Categories = g.Select(jc => new JobCategoryInfo(jc.Category.Id, jc.Category.Name)).ToList() })
            .ToDictionaryAsync(x => x.JobId, x => x.Categories);

        var jobs = await db.ScrapeJobs
            .Select(j => new { j.Id, j.SearchTerm, j.FilterInstructions, j.IsEnabled, j.CreatedUtc })
            .ToListAsync();

        var result = jobs.Select(j => new JobResponse(
            j.Id, j.SearchTerm, j.FilterInstructions, j.IsEnabled,
            lastRunByJob.GetValueOrDefault(j.Id),
            j.CreatedUtc,
            jobCategories.GetValueOrDefault(j.Id, new List<JobCategoryInfo>())));

        return Results.Ok(result);
    }

    private static async Task<IResult> GetJob(int id, EtlDbContext db)
    {
        var job = await db.ScrapeJobs.FindAsync(id);
        if (job == null)
        {
            return Results.NotFound(new ErrorResponse($"Job {id} not found"));
        }

        var categories = await db.JobCategories
            .Where(jc => jc.JobId == id)
            .Include(jc => jc.Category)
            .Select(jc => new JobCategoryInfo(jc.Category.Id, jc.Category.Name))
            .ToListAsync();

        return Results.Ok(new JobResponse(
            job.Id, job.SearchTerm, job.FilterInstructions,
            job.IsEnabled, job.LastRunUtc, job.CreatedUtc, categories));
    }

    private static async Task<IResult> CreateJob(
        CreateJobRequest request, EtlDbContext db, ILogger<Program> logger)
    {
        if (string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            return Results.BadRequest(new ErrorResponse("searchTerm is required"));
        }

        var job = new ScrapeJob
        {
            SearchTerm = request.SearchTerm,
            FilterInstructions = request.FilterInstructions,
            IsEnabled = request.IsEnabled ?? true,
            CreatedUtc = DateTime.UtcNow
        };

        db.ScrapeJobs.Add(job);
        await db.SaveChangesAsync();

        if (request.CategoryIds != null)
        {
            foreach (var catId in request.CategoryIds.Distinct())
            {
                if (await db.Categories.AnyAsync(c => c.Id == catId))
                {
                    db.JobCategories.Add(new JobCategory { JobId = job.Id, CategoryId = catId });
                }
            }
            await db.SaveChangesAsync();
        }

        var categories = await db.JobCategories
            .Where(jc => jc.JobId == job.Id)
            .Include(jc => jc.Category)
            .Select(jc => new JobCategoryInfo(jc.Category.Id, jc.Category.Name))
            .ToListAsync();

        logger.LogInformation("Created scrape job {JobId}: '{SearchTerm}'", job.Id, job.SearchTerm);

        return Results.Created($"/api/jobs/{job.Id}", new JobResponse(
            job.Id, job.SearchTerm, job.FilterInstructions,
            job.IsEnabled, job.LastRunUtc, job.CreatedUtc, categories));
    }

    private static async Task<IResult> UpdateJob(
        int id, UpdateJobRequest request, EtlDbContext db, ILogger<Program> logger)
    {
        var job = await db.ScrapeJobs.FindAsync(id);

        if (job == null)
        {
            return Results.NotFound(new ErrorResponse($"Job {id} not found"));
        }

        if (request.SearchTerm != null)
        {
            job.SearchTerm = request.SearchTerm;
        }

        if (request.FilterInstructions != null)
        {
            job.FilterInstructions = request.FilterInstructions;
        }

        if (request.IsEnabled != null)
        {
            job.IsEnabled = request.IsEnabled.Value;
        }

        if (request.CategoryIds != null)
        {
            var existing = await db.JobCategories.Where(jc => jc.JobId == id).ToListAsync();
            db.JobCategories.RemoveRange(existing);

            foreach (var catId in request.CategoryIds.Distinct())
            {
                if (await db.Categories.AnyAsync(c => c.Id == catId))
                {
                    db.JobCategories.Add(new JobCategory { JobId = id, CategoryId = catId });
                }
            }
        }

        await db.SaveChangesAsync();

        logger.LogInformation("Updated scrape job {JobId}", job.Id);

        var categories = await db.JobCategories
            .Where(jc => jc.JobId == id)
            .Include(jc => jc.Category)
            .Select(jc => new JobCategoryInfo(jc.Category.Id, jc.Category.Name))
            .ToListAsync();

        return Results.Ok(new JobResponse(
            job.Id, job.SearchTerm, job.FilterInstructions,
            job.IsEnabled, job.LastRunUtc, job.CreatedUtc, categories));
    }

    private static async Task<IResult> DeleteJob(
        int id, EtlDbContext db, ILogger<Program> logger)
    {
        var job = await db.ScrapeJobs.FindAsync(id);

        if (job == null)
        {
            return Results.NotFound(new ErrorResponse($"Job {id} not found"));
        }

        db.ScrapeJobs.Remove(job);
        await db.SaveChangesAsync();

        logger.LogInformation("Deleted scrape job {JobId}: '{SearchTerm}'", id, job.SearchTerm);

        return Results.Ok(new MessageResponse($"Job {id} deleted"));
    }

    private static async Task<IResult> EnableJob(int id, EtlDbContext db)
    {
        var job = await db.ScrapeJobs.FindAsync(id);

        if (job == null)
        {
            return Results.NotFound(new ErrorResponse($"Job {id} not found"));
        }

        job.IsEnabled = true;
        await db.SaveChangesAsync();

        return Results.Ok(new JobToggleResponse(job.Id, job.SearchTerm, job.IsEnabled));
    }

    private static async Task<IResult> DisableJob(int id, EtlDbContext db)
    {
        var job = await db.ScrapeJobs.FindAsync(id);

        if (job == null)
        {
            return Results.NotFound(new ErrorResponse($"Job {id} not found"));
        }

        job.IsEnabled = false;
        await db.SaveChangesAsync();

        return Results.Ok(new JobToggleResponse(job.Id, job.SearchTerm, job.IsEnabled));
    }

    private static async Task<IResult> SetJobCategories(
        int id, int[] categoryIds, EtlDbContext db)
    {
        var job = await db.ScrapeJobs.FindAsync(id);
        if (job == null)
        {
            return Results.NotFound(new ErrorResponse($"Job {id} not found"));
        }

        var existing = await db.JobCategories.Where(jc => jc.JobId == id).ToListAsync();
        db.JobCategories.RemoveRange(existing);

        foreach (var catId in categoryIds.Distinct())
        {
            if (await db.Categories.AnyAsync(c => c.Id == catId))
            {
                db.JobCategories.Add(new JobCategory { JobId = id, CategoryId = catId });
            }
        }

        await db.SaveChangesAsync();

        var categories = await db.JobCategories
            .Where(jc => jc.JobId == id)
            .Include(jc => jc.Category)
            .Select(jc => new JobCategoryInfo(jc.Category.Id, jc.Category.Name))
            .ToListAsync();

        return Results.Ok(categories);
    }
}
