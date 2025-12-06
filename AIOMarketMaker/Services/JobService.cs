using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Etl.Data.Models;
using AIOMarketMaker.Services.Dtos;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Services;

public class JobService : IJobService
{
    private readonly EtlDbContext _dbContext;

    public JobService(EtlDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<JobDto>> GetAllJobsAsync(CancellationToken ct = default)
    {
        return await _dbContext.ScrapeJobs
            .Select(j => new JobDto(
                j.Id,
                j.SearchTerm,
                j.FilterInstructions,
                j.IsEnabled,
                j.LastRunUtc,
                j.CreatedUtc
            ))
            .ToListAsync(ct);
    }

    public async Task<JobDto> CreateJobAsync(CreateJobRequest request, CancellationToken ct = default)
    {
        var job = new ScrapeJob
        {
            SearchTerm = request.SearchTerm,
            FilterInstructions = request.FilterInstructions,
            IsEnabled = request.IsEnabled,
            CreatedUtc = DateTime.UtcNow
        };

        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync(ct);

        return new JobDto(
            job.Id,
            job.SearchTerm,
            job.FilterInstructions,
            job.IsEnabled,
            job.LastRunUtc,
            job.CreatedUtc
        );
    }

    public async Task<JobDto?> UpdateJobAsync(int id, CreateJobRequest request, CancellationToken ct = default)
    {
        var job = await _dbContext.ScrapeJobs.FindAsync(new object[] { id }, ct);
        if (job == null)
            return null;

        job.SearchTerm = request.SearchTerm;
        job.FilterInstructions = request.FilterInstructions;
        job.IsEnabled = request.IsEnabled;

        await _dbContext.SaveChangesAsync(ct);

        return new JobDto(
            job.Id,
            job.SearchTerm,
            job.FilterInstructions,
            job.IsEnabled,
            job.LastRunUtc,
            job.CreatedUtc
        );
    }

    public async Task<(bool found, bool isEnabled)?> ToggleJobAsync(int id, CancellationToken ct = default)
    {
        var job = await _dbContext.ScrapeJobs.FindAsync(new object[] { id }, ct);
        if (job == null)
            return null;

        job.IsEnabled = !job.IsEnabled;
        await _dbContext.SaveChangesAsync(ct);

        return (true, job.IsEnabled);
    }

    public async Task<DeleteJobResult?> DeleteJobAsync(int id, CancellationToken ct = default)
    {
        var job = await _dbContext.ScrapeJobs.FindAsync(new object[] { id }, ct);
        if (job == null)
            return null;

        var listingCount = await _dbContext.Listings
            .Where(l => l.ScrapeJobId == id)
            .CountAsync(ct);

        _dbContext.Listings.RemoveRange(_dbContext.Listings.Where(l => l.ScrapeJobId == id));
        _dbContext.ScrapeJobs.Remove(job);
        await _dbContext.SaveChangesAsync(ct);

        return new DeleteJobResult(true, listingCount);
    }
}
