using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Etl.Activities;

public class GetPendingCountActivity
{
    private readonly EtlDbContext _dbContext;

    public GetPendingCountActivity(EtlDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [Function(nameof(GetPendingCountActivity))]
    public async Task<int> Run([ActivityTrigger] int scrapeRunId)
    {
        return await _dbContext.ScrapeRunListings
            .CountAsync(srl => srl.ScrapeRunId == scrapeRunId && srl.Status == "Pending");
    }
}
