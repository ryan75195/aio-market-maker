using System.Diagnostics;
using AIOMarketMaker.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services.Taxonomy;

public class TaxonomyPostJobStage : IPostJobStage
{
    private const int MinListings = 10;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TaxonomyPostJobStage> _logger;

    public string Name => "Taxonomy";

    public TaxonomyPostJobStage(
        IServiceScopeFactory scopeFactory,
        ILogger<TaxonomyPostJobStage> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task Execute(PostJobContext context, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
        var taxonomyService = scope.ServiceProvider.GetRequiredService<ITaxonomyService>();
        var persistenceService = scope.ServiceProvider.GetRequiredService<ITaxonomyPersistenceService>();

        var listings = await db.Listings
            .AsNoTracking()
            .Where(l => l.ScrapeJobId == context.JobId && l.Title != null)
            .OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.Title })
            .ToListAsync(ct);

        if (listings.Count < MinListings)
        {
            _logger.LogInformation(
                "Skipping taxonomy for job {JobId}: only {Count} listings (minimum {Min})",
                context.JobId, listings.Count, MinListings);
            return;
        }

        var titles = listings.Select(l => l.Title!).ToList();
        var listingIds = listings.Select(l => l.Id).ToList();

        _logger.LogInformation("Running taxonomy for job {JobId} with {Count} listings",
            context.JobId, listings.Count);

        var sw = Stopwatch.StartNew();
        var result = await taxonomyService.Generate(titles, ct);
        sw.Stop();

        var persisted = await persistenceService.Save(
            context.JobId, result, listingIds, (int)sw.ElapsedMilliseconds, ct);

        _logger.LogInformation(
            "Taxonomy saved for job {JobId}: {AxisCount} axes, {Assigned} assigned, " +
            "{Coverage:F1}% coverage in {Duration}ms",
            context.JobId, persisted.AxisCount, persisted.AssignedListings,
            persisted.CoveragePercent, sw.ElapsedMilliseconds);
    }
}
