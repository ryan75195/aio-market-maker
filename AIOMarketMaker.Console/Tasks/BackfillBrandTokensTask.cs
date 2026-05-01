using System.Text.Json;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Console.Tasks;

public class BackfillBrandTokensTask : ITask
{
    private readonly IDbContextFactory<EtlDbContext> _dbFactory;
    private readonly IBrandTokenExtractor _extractor;

    public string Name => "backfill-brand-tokens";
    public string Description =>
        "Extract and store brand tokens for all scrape jobs missing them.\n" +
        "Usage: dotnet run -- backfill-brand-tokens [--start-from-id N]";

    public BackfillBrandTokensTask(
        IDbContextFactory<EtlDbContext> dbFactory,
        IBrandTokenExtractor extractor)
    {
        _dbFactory = dbFactory;
        _extractor = extractor;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var startFromId = CommandHelpers.GetIntArg(args, "--start-from-id") ?? 0;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var jobs = await db.ScrapeJobs
            .Where(j => j.BrandTokens == null && j.Id >= startFromId)
            .OrderBy(j => j.Id)
            .Select(j => new { j.Id, j.SearchTerm })
            .ToListAsync(ct);

        System.Console.WriteLine($"Found {jobs.Count} jobs without brand tokens");

        var processed = 0;
        var failed = 0;

        foreach (var job in jobs)
        {
            try
            {
                var tokens = (await _extractor.Extract(job.SearchTerm, ct)).ToList();
                var json = JsonSerializer.Serialize(tokens);

                await db.ScrapeJobs
                    .Where(j => j.Id == job.Id)
                    .ExecuteUpdateAsync(s =>
                        s.SetProperty(j => j.BrandTokens, json), ct);

                processed++;
                System.Console.WriteLine(
                    $"  [{processed}/{jobs.Count}] Job {job.Id} '{job.SearchTerm}' -> [{string.Join(", ", tokens)}]");
            }
            catch (Exception ex)
            {
                failed++;
                System.Console.WriteLine($"  FAILED Job {job.Id} '{job.SearchTerm}': {ex.Message}");
            }
        }

        System.Console.WriteLine($"\nDone: {processed} processed, {failed} failed");
        return failed > 0 ? 1 : 0;
    }
}
