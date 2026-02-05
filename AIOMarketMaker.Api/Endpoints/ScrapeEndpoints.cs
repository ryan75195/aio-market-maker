using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Services;

namespace AIOMarketMaker.Api.Endpoints;

public record StartScrapeResponse(IEnumerable<ScrapeRunInfo> Runs);
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
        var runIds = new List<int>();
        foreach (var job in jobs)
        {
            var run = await processor.CreateRun(job, "Manual");
            runIds.Add(run.Id);
        }

        // Process in background with its own DI scope (request scope gets disposed after response)
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var bgProcessor = scope.ServiceProvider.GetRequiredService<IScrapeJobProcessor>();
            var bgDb = scope.ServiceProvider.GetRequiredService<EtlDbContext>();

            for (var i = 0; i < jobs.Count; i++)
            {
                var run = await bgDb.ScrapeRuns.FindAsync(runIds[i]);
                if (run == null) { continue; }

                try
                {
                    await bgProcessor.Execute(run, jobs[i]);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Background scrape failed for run {RunId}", runIds[i]);
                }
            }
        });

        var runInfos = runIds.Zip(jobs, (id, job) => new ScrapeRunInfo(id, job.Id, "Queued"));
        return Results.Accepted(value: new StartScrapeResponse(runInfos));
    }
}
