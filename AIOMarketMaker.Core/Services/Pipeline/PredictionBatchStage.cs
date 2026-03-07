using System.Data;
using AIOMarketMaker.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIOMarketMaker.Core.Services.Pipeline;

public class PredictionBatchStage : IBatchStage
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PricingOptions _pricingOptions;
    private readonly ILogger<PredictionBatchStage> _logger;

    public string Name => "Materializing Predictions";

    public PredictionBatchStage(
        IServiceScopeFactory scopeFactory,
        IOptions<PricingOptions> pricingOptions,
        ILogger<PredictionBatchStage> logger)
    {
        _scopeFactory = scopeFactory;
        _pricingOptions = pricingOptions.Value;
        _logger = logger;
    }

    public async Task Execute(BatchContext context, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
        var conn = db.Database.GetDbConnection();

        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        var filters = new PredictionFilters(
            FeePercent: (decimal)_pricingOptions.FeePercent,
            MatchCondition: true,
            MinComps: _pricingOptions.MinComps);
        var cte = PredictionCteBuilder.Build(filters, _pricingOptions);

        // Full replace in a transaction so readers see either old or new data
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

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        var rows = await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Materialized {Count} predictions into ListingPredictions", rows);
    }
}
