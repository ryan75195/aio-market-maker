using System.Globalization;

namespace AIOMarketMaker.Core.Services;

public static class PredictionCteBuilder
{
    public static string Build(PredictionFilters filters, PricingOptions options, int? singleListingId = null)
    {
        var pb = filters.PriceBand.ToString(CultureInfo.InvariantCulture);
        var fee = filters.FeePercent.ToString(CultureInfo.InvariantCulture);
        var mc = filters.MinComps.ToString(CultureInfo.InvariantCulture);
        var power = options.ConfidenceWeightPower.ToString(CultureInfo.InvariantCulture);
        var iqr = options.IqrMultiplier.ToString(CultureInfo.InvariantCulture);
        var halfLife = options.RecencyHalfLifeDays.ToString(CultureInfo.InvariantCulture);
        var sampleTarget = options.ConfidenceSampleTarget.ToString(CultureInfo.InvariantCulture);
        var sampleWeight = options.SampleSizeWeight.ToString(CultureInfo.InvariantCulture);
        var classifierWeight = options.ClassifierConfidenceWeight.ToString(CultureInfo.InvariantCulture);
        var consistencyWeight = options.ConsistencyWeight.ToString(CultureInfo.InvariantCulture);

        var conditionFilter = filters.MatchCondition
            ? "AND active.[Condition] = sold.[Condition]"
            : "";

        var priceBandFilter = filters.PriceBand > 0
            ? $@"AND active.Price > 0
               AND sold.Price BETWEEN active.Price / {pb} AND active.Price * {pb}"
            : "";

        var singleListingFilter = singleListingId.HasValue
            ? $"AND active.Id = {singleListingId.Value}"
            : "";

        var confExpr = $"ISNULL(rc.ClassifierConfidence, rc.SimilarityScore)";

        // Recency weight: EXP(-days / halfLife) combined with confidence
        var recencyWeight = $@"POWER({confExpr}, {power}) *
            CASE WHEN sold.EndDateUtc IS NOT NULL
                 THEN EXP(-CAST(DATEDIFF(day, sold.EndDateUtc, GETUTCDATE()) AS FLOAT) / {halfLife})
                 ELSE 1.0
            END";

        // Use recency+confidence combined weight for AverageSoldPrice
        var weightExpr = recencyWeight;
        var weightedAvg = $"SUM(sold.Price * ({weightExpr})) / NULLIF(SUM({weightExpr}), 0)";

        var profitExpr = filters.FeePercent > 0
            ? $"CAST({weightedAvg} AS DECIMAL(18,2)) * (1.0 - {fee} / 100.0) - active.Price - ISNULL(active.ShippingCost, 0)"
            : $"CAST({weightedAvg} AS DECIMAL(18,2)) - active.Price";

        // Confidence score: composite of sample size, avg classifier confidence, price consistency
        // sampleFactor = 1 - EXP(-count / target)
        // consistencyFactor = MAX(0, 1 - STDEV/AVG)
        // confidence = sampleWeight * sampleFactor + classifierWeight * avgConf + consistencyWeight * consistencyFactor
        var confidenceExpr = $@"
            {sampleWeight} * (1.0 - EXP(-CAST(COUNT(*) AS FLOAT) / {sampleTarget}))
            + {classifierWeight} * AVG({confExpr})
            + {consistencyWeight} * CASE
                WHEN AVG(sold.Price) > 0 AND COUNT(*) > 1
                THEN IIF(1.0 - STDEV(CAST(sold.Price AS FLOAT)) / AVG(CAST(sold.Price AS FLOAT)) > 0,
                         1.0 - STDEV(CAST(sold.Price AS FLOAT)) / AVG(CAST(sold.Price AS FLOAT)), 0)
                ELSE 0
              END";

        return $@";WITH RawComps AS (
        SELECT r.ListingIdA AS ActiveListingId, r.ListingIdB AS SoldListingId,
               r.ClassifierConfidence, r.SimilarityScore
        FROM ListingRelationships r        INNER JOIN Listings active ON active.Id = r.ListingIdA AND active.ListingStatus = 'Active'
        INNER JOIN Listings sold ON sold.Id = r.ListingIdB AND sold.ListingStatus = 'Sold'
        WHERE r.IsComparable = 1
        {conditionFilter}
        {singleListingFilter}
        UNION ALL
        SELECT r.ListingIdB AS ActiveListingId, r.ListingIdA AS SoldListingId,
               r.ClassifierConfidence, r.SimilarityScore
        FROM ListingRelationships r        INNER JOIN Listings active ON active.Id = r.ListingIdB AND active.ListingStatus = 'Active'
        INNER JOIN Listings sold ON sold.Id = r.ListingIdA AND sold.ListingStatus = 'Sold'
        WHERE r.IsComparable = 1
        {conditionFilter}
        {singleListingFilter}
    ),
    RawCompPrices AS (
        SELECT rc.ActiveListingId, rc.SoldListingId,
               rc.ClassifierConfidence, rc.SimilarityScore,
               sold.Price AS SoldPrice,
               PERCENTILE_CONT(0.25) WITHIN GROUP (ORDER BY sold.Price)
                   OVER (PARTITION BY rc.ActiveListingId) AS Q1,
               PERCENTILE_CONT(0.75) WITHIN GROUP (ORDER BY sold.Price)
                   OVER (PARTITION BY rc.ActiveListingId) AS Q3,
               COUNT(*) OVER (PARTITION BY rc.ActiveListingId) AS TotalComps
        FROM RawComps rc
        INNER JOIN Listings sold ON sold.Id = rc.SoldListingId
        WHERE sold.Price > 0
    ),
    CleanedComps AS (
        SELECT ActiveListingId, SoldListingId,
               ClassifierConfidence, SimilarityScore, TotalComps
        FROM RawCompPrices
        WHERE TotalComps < 4
           OR Q3 = Q1
           OR (SoldPrice >= Q1 - {iqr} * (Q3 - Q1)
               AND SoldPrice <= Q3 + {iqr} * (Q3 - Q1))
    ),
    CleanedMedians AS (
        SELECT DISTINCT cc.ActiveListingId,
            PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY sold.Price)
                OVER (PARTITION BY cc.ActiveListingId) AS MedianSoldPrice
        FROM CleanedComps cc
        INNER JOIN Listings sold ON sold.Id = cc.SoldListingId
    ),
    Aggregated AS (
        SELECT active.Id AS ListingId,
            COUNT(*) AS SimilarSoldCount,
            CAST({weightedAvg} AS DECIMAL(18,2)) AS AverageSoldPrice,
            CAST({profitExpr} AS DECIMAL(18,2)) AS PotentialProfit,
            AVG(CASE WHEN sold.EndDateUtc > sold.CreatedUtc
                     THEN DATEDIFF(day, sold.CreatedUtc, sold.EndDateUtc)
                END) AS EstimatedDaysToSell,
            {confidenceExpr} AS Confidence,
            MAX(rc.TotalComps) - COUNT(*) AS OutliersRemoved
        FROM CleanedComps rc
        INNER JOIN Listings sold ON sold.Id = rc.SoldListingId
        INNER JOIN Listings active ON active.Id = rc.ActiveListingId
        {priceBandFilter}
        GROUP BY active.Id, active.Price, active.ShippingCost
        HAVING COUNT(*) >= {mc}
            AND CAST({profitExpr} AS DECIMAL(18,2)) > 0
    ),
    FilteredPredictions AS (
        SELECT a.ListingId, a.SimilarSoldCount, a.AverageSoldPrice, a.PotentialProfit,
               a.EstimatedDaysToSell, a.Confidence, a.OutliersRemoved,
               CAST(m.MedianSoldPrice AS DECIMAL(18,2)) AS MedianSoldPrice
        FROM Aggregated a
        LEFT JOIN CleanedMedians m ON m.ActiveListingId = a.ListingId
    )";
    }
}
