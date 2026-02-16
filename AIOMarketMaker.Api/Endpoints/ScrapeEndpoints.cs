using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Services;

namespace AIOMarketMaker.Api.Endpoints;

public record StartScrapeResponse(Guid BatchId, IEnumerable<ScrapeRunInfo> Runs);
public record ScrapeRunInfo(int Id, int? JobId, string? Status);
public record NoJobsResponse(string Message);

public static class ScrapeEndpoints
{
    public static void MapScrapeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/scrape/start", StartScrape);
    }

    private static async Task<IResult> StartScrape(
        IScrapeJobProcessor processor,
        EtlDbContext db,
        IServiceScopeFactory scopeFactory,
        ScrapingConfig scrapingConfig,
        ILogger<Program> logger)
    {
        var jobs = await db.ScrapeJobs.Where(j => j.IsEnabled)
            .Select(j => new ScrapeJobConfig(j.Id, j.SearchTerm))
            .ToListAsync();

        if (!jobs.Any())
        {
            return Results.Ok(new NoJobsResponse("No enabled jobs"));
        }

        // Create runs in request scope so UI can see them immediately
        var batchId = Guid.NewGuid();
        var runIds = new List<int>();
        foreach (var job in jobs)
        {
            var run = await processor.CreateRun(job, "Manual", batchId);
            runIds.Add(run.Id);
        }

        // Process in background with parallel execution
        _ = Task.Run(async () =>
        {
            var parallelism = new SemaphoreSlim(scrapingConfig.MaxConcurrentRuns);
            var tasks = jobs.Select((job, i) => Task.Run(async () =>
            {
                await parallelism.WaitAsync();
                try
                {
                    using var jobScope = scopeFactory.CreateScope();
                    var jobProcessor = jobScope.ServiceProvider.GetRequiredService<IScrapeJobProcessor>();
                    var jobDb = jobScope.ServiceProvider.GetRequiredService<EtlDbContext>();

                    var run = await jobDb.ScrapeRuns.FindAsync(runIds[i]);
                    if (run == null) { return; }

                    await jobProcessor.Execute(run, job);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Background scrape failed for run {RunId}", runIds[i]);
                }
                finally
                {
                    parallelism.Release();
                }
            }));
            await Task.WhenAll(tasks);
        });

        var runInfos = runIds.Zip(jobs, (id, job) => new ScrapeRunInfo(id, job.Id, "Queued"));
        return Results.Accepted(value: new StartScrapeResponse(batchId, runInfos));
    }
}
