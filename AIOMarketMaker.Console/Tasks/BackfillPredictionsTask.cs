using System.Data;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Console.Tasks;

public class BackfillPredictionsTask : ITask
{
    private readonly IDbContextFactory<EtlDbContext> _dbFactory;

    public string Name => "backfill-predictions";
    public string Description => "One-time backfill of the ListingPredictions table from existing data";

    public BackfillPredictionsTask(IDbContextFactory<EtlDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        System.Console.WriteLine("=== Backfill ListingPredictions ===");
        System.Console.WriteLine();

        var pricingOptions = new PricingOptions();
        var filters = new PredictionFilters(
            FeePercent: (decimal)pricingOptions.FeePercent,
            MatchCondition: true,
            MinComps: pricingOptions.MinComps);

        var cte = PredictionCteBuilder.Build(filters, pricingOptions);

        var sql = $@"
            BEGIN TRANSACTION;
            DELETE FROM ListingPredictions;
            {cte}
            INSERT INTO ListingPredictions
                (ListingId, SimilarSoldCount, AverageSoldPrice, MedianSoldPrice,
                 PotentialProfit, EstimatedDaysToSell, Confidence, OutliersRemoved, ComputedUtc)
            SELECT ListingId, SimilarSoldCount, AverageSoldPrice, MedianSoldPrice,
                   PotentialProfit, EstimatedDaysToSell, Confidence, OutliersRemoved, GETUTCDATE()
            FROM FilteredPredictions;
            COMMIT;";

        System.Console.WriteLine("Executing DELETE + INSERT into ListingPredictions...");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();

        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        sw.Stop();

        System.Console.WriteLine($"Materialized {rows:N0} predictions into ListingPredictions");
        System.Console.WriteLine($"Duration: {sw.Elapsed.TotalSeconds:F1}s");

        return 0;
    }
}
