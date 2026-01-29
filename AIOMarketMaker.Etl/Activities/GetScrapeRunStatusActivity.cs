using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

public class GetScrapeRunStatusActivity
{
    private readonly EtlDbContext _dbContext;

    public GetScrapeRunStatusActivity(EtlDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [Function(nameof(GetScrapeRunStatusActivity))]
    public async Task<ScrapeRunStatusResult> Run([ActivityTrigger] int scrapeRunId)
    {
        var run = await _dbContext.ScrapeRuns
            .Where(r => r.Id == scrapeRunId)
            .Select(r => new { r.Status, r.TotalListingsFound, r.CurrentPhase })
            .FirstOrDefaultAsync();

        if (run == null)
        {
            return new ScrapeRunStatusResult("NotFound", 0, "NotFound");
        }

        return new ScrapeRunStatusResult(run.Status, run.TotalListingsFound, run.CurrentPhase);
    }
}

public record ScrapeRunStatusResult(string Status, int TotalListingsFound, string CurrentPhase);
