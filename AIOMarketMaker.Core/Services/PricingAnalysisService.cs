using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Core.Services;

public interface IPricingAnalysisService
{
    PricingAnalysisResult Analyze(
        IEnumerable<Listing> listings,
        IEnumerable<SemanticSearchHit> hits,
        PricingAnalysisOptions? options = null);
}

public class PricingAnalysisService : IPricingAnalysisService
{
    public PricingAnalysisResult Analyze(
        IEnumerable<Listing> listings,
        IEnumerable<SemanticSearchHit> hits,
        PricingAnalysisOptions? options = null)
    {
        options ??= new PricingAnalysisOptions();

        var listingDict = listings.ToDictionary(l => l.ListingId);
        var hitsList = hits.ToList();

        // Join hits with listings, filter to those with prices
        var pricedItems = hitsList
            .Where(h => listingDict.TryGetValue(h.ListingId, out var listing)
                        && listing.Price.HasValue
                        && listing.Price > 0)
            .Select(h => new PricedItem(
                listingDict[h.ListingId],
                h.Score))
            .ToList();

        if (pricedItems.Count == 0)
        {
            return PricingAnalysisResult.Empty;
        }

        // Filter to sold-only if requested
        if (options.SoldOnly)
        {
            pricedItems = pricedItems
                .Where(p => string.Equals(p.Listing.ListingStatus, "Sold", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (pricedItems.Count == 0)
            {
                return PricingAnalysisResult.Empty;
            }
        }

        // Apply IQR outlier detection
        var (cleanedItems, outliers) = RemoveOutliers(pricedItems, options.IqrMultiplier);

        if (cleanedItems.Count == 0)
        {
            return PricingAnalysisResult.Empty;
        }

        // Calculate basic statistics
        var prices = cleanedItems.Select(p => p.Listing.Price!.Value).OrderBy(p => p).ToList();
        var mean = prices.Average();
        var median = CalculateMedian(prices);
        var min = prices.Min();
        var max = prices.Max();
        var stdDev = CalculateStdDev(prices, mean);

        // Calculate similarity-weighted average
        var weightedMean = CalculateWeightedMean(cleanedItems, options);

        // Calculate recency-weighted average (if dates available)
        var recencyWeightedMean = CalculateRecencyWeightedMean(cleanedItems, options);

        // Calculate confidence based on sample size and score distribution
        var confidence = CalculateConfidence(cleanedItems, options);

        return new PricingAnalysisResult(
            SampleSize: cleanedItems.Count,
            OutliersRemoved: outliers.Count,
            Mean: Math.Round(mean, 2),
            Median: Math.Round(median, 2),
            WeightedMean: Math.Round(weightedMean, 2),
            RecencyWeightedMean: recencyWeightedMean.HasValue ? Math.Round(recencyWeightedMean.Value, 2) : null,
            Min: Math.Round(min, 2),
            Max: Math.Round(max, 2),
            StdDev: Math.Round(stdDev, 2),
            Confidence: Math.Round(confidence, 2),
            PriceRange: new PriceRange(
                Math.Round(median - stdDev, 2),
                Math.Round(median + stdDev, 2)),
            Outliers: outliers.Select(o => new OutlierInfo(
                o.Listing.ListingId,
                o.Listing.Price!.Value,
                o.Score)).ToList());
    }

    private static (List<PricedItem> Cleaned, List<PricedItem> Outliers) RemoveOutliers(
        List<PricedItem> items,
        double iqrMultiplier)
    {
        if (items.Count < 4)
        {
            // Not enough data for IQR, return all items
            return (items, new List<PricedItem>());
        }

        var prices = items.Select(p => p.Listing.Price!.Value).OrderBy(p => p).ToList();

        var q1 = CalculatePercentile(prices, 25);
        var q3 = CalculatePercentile(prices, 75);
        var iqr = q3 - q1;

        // Handle edge case where IQR is 0 (all same price)
        if (iqr == 0)
        {
            return (items, new List<PricedItem>());
        }

        var lowerBound = q1 - (decimal)iqrMultiplier * iqr;
        var upperBound = q3 + (decimal)iqrMultiplier * iqr;

        var cleaned = new List<PricedItem>();
        var outliers = new List<PricedItem>();

        foreach (var item in items)
        {
            var price = item.Listing.Price!.Value;
            if (price >= lowerBound && price <= upperBound)
            {
                cleaned.Add(item);
            }
            else
            {
                outliers.Add(item);
            }
        }

        return (cleaned, outliers);
    }

    private static decimal CalculateWeightedMean(List<PricedItem> items, PricingAnalysisOptions options)
    {
        // Weight by similarity score
        // Higher scores get more weight
        var totalWeight = 0.0;
        var weightedSum = 0.0;

        foreach (var item in items)
        {
            // Use score as weight, optionally with power to emphasize high scores
            var weight = Math.Pow(item.Score, options.SimilarityWeightPower);
            weightedSum += (double)item.Listing.Price!.Value * weight;
            totalWeight += weight;
        }

        return totalWeight > 0 ? (decimal)(weightedSum / totalWeight) : 0;
    }

    private static decimal? CalculateRecencyWeightedMean(List<PricedItem> items, PricingAnalysisOptions options)
    {
        var itemsWithDates = items
            .Where(i => i.Listing.EndDateUtc.HasValue || i.Listing.CreatedUtc != default)
            .ToList();

        if (itemsWithDates.Count == 0)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var totalWeight = 0.0;
        var weightedSum = 0.0;

        foreach (var item in itemsWithDates)
        {
            // Use EndDateUtc (sold date) if available, otherwise CreatedUtc
            var date = item.Listing.EndDateUtc ?? item.Listing.CreatedUtc;
            var daysSince = (now - date).TotalDays;

            // Exponential decay: items from today = 1.0, items from 30 days ago ≈ 0.37
            var recencyWeight = Math.Exp(-daysSince / options.RecencyHalfLifeDays);

            // Combined weight = similarity * recency
            var combinedWeight = Math.Pow(item.Score, options.SimilarityWeightPower) * recencyWeight;

            weightedSum += (double)item.Listing.Price!.Value * combinedWeight;
            totalWeight += combinedWeight;
        }

        return totalWeight > 0 ? (decimal)(weightedSum / totalWeight) : null;
    }

    private static double CalculateConfidence(List<PricedItem> items, PricingAnalysisOptions options)
    {
        // Confidence factors:
        // 1. Sample size (more = better, diminishing returns)
        // 2. Average similarity score (higher = better)
        // 3. Price consistency (lower std dev = better)

        if (items.Count == 0) return 0;

        // Sample size factor: 0-1, approaches 1 as count increases
        var sampleFactor = 1 - Math.Exp(-items.Count / (double)options.ConfidenceSampleTarget);

        // Similarity factor: average score
        var avgScore = items.Average(i => i.Score);
        var similarityFactor = avgScore;

        // Consistency factor: coefficient of variation (inverse)
        var prices = items.Select(p => (double)p.Listing.Price!.Value).ToList();
        var mean = prices.Average();
        var stdDev = CalculateStdDev(prices.Select(p => (decimal)p).ToList(), (decimal)mean);
        var cv = mean > 0 ? (double)stdDev / mean : 1;
        var consistencyFactor = Math.Max(0, 1 - cv); // 0 when CV >= 1, 1 when CV = 0

        // Weighted combination
        var confidence = (sampleFactor * 0.3) + (similarityFactor * 0.4) + (consistencyFactor * 0.3);

        return Math.Min(1.0, Math.Max(0.0, confidence));
    }

    private static decimal CalculateMedian(List<decimal> sortedPrices)
    {
        var count = sortedPrices.Count;
        if (count == 0) return 0;

        if (count % 2 == 0)
        {
            return (sortedPrices[count / 2 - 1] + sortedPrices[count / 2]) / 2;
        }

        return sortedPrices[count / 2];
    }

    private static decimal CalculatePercentile(List<decimal> sortedPrices, int percentile)
    {
        if (sortedPrices.Count == 0) return 0;

        var index = (percentile / 100.0) * (sortedPrices.Count - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);

        if (lower == upper)
        {
            return sortedPrices[lower];
        }

        var weight = (decimal)(index - lower);
        return sortedPrices[lower] * (1 - weight) + sortedPrices[upper] * weight;
    }

    private static decimal CalculateStdDev(List<decimal> prices, decimal mean)
    {
        if (prices.Count < 2) return 0;

        var sumSquaredDiff = prices.Sum(p => (p - mean) * (p - mean));
        return (decimal)Math.Sqrt((double)(sumSquaredDiff / (prices.Count - 1)));
    }

    private record PricedItem(Listing Listing, float Score);
}

public record PricingAnalysisOptions
{
    /// <summary>
    /// IQR multiplier for outlier detection. Default 1.5 (standard).
    /// Higher values are more lenient.
    /// </summary>
    public double IqrMultiplier { get; init; } = 1.5;

    /// <summary>
    /// Power to apply to similarity scores for weighting.
    /// 1.0 = linear, 2.0 = squared (emphasizes high scores more).
    /// </summary>
    public double SimilarityWeightPower { get; init; } = 2.0;

    /// <summary>
    /// Half-life in days for recency weighting.
    /// Items this many days old have half the weight of today's items.
    /// </summary>
    public double RecencyHalfLifeDays { get; init; } = 30.0;

    /// <summary>
    /// Target sample size for confidence calculation.
    /// At this many items, sample factor ≈ 0.63.
    /// </summary>
    public int ConfidenceSampleTarget { get; init; } = 20;

    /// <summary>
    /// Only include sold listings (not active).
    /// </summary>
    public bool SoldOnly { get; init; } = false;
}

public record PricingAnalysisResult(
    int SampleSize,
    int OutliersRemoved,
    decimal Mean,
    decimal Median,
    decimal WeightedMean,
    decimal? RecencyWeightedMean,
    decimal Min,
    decimal Max,
    decimal StdDev,
    double Confidence,
    PriceRange PriceRange,
    IReadOnlyList<OutlierInfo> Outliers)
{
    public static PricingAnalysisResult Empty => new(
        0, 0, 0, 0, 0, null, 0, 0, 0, 0,
        new PriceRange(0, 0),
        Array.Empty<OutlierInfo>());
}

public record PriceRange(decimal Low, decimal High);

public record OutlierInfo(string ListingId, decimal Price, float Score);
