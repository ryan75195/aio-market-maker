namespace AIOMarketMaker.Core.Services;

public record PricedComparable(decimal Price, double ClassifierConfidence, DateTime? SoldDateUtc);

public record PricingResult(
    int SampleSize,
    int OutliersRemoved,
    decimal Mean,
    decimal Median,
    decimal WeightedMean,
    decimal? RecencyWeightedMean,
    decimal Min,
    decimal Max,
    decimal StdDev,
    double Confidence)
{
    public static PricingResult Empty => new(0, 0, 0, 0, 0, null, 0, 0, 0, 0);
}

public static class PricingCalculator
{
    public static PricingResult Analyze(IEnumerable<PricedComparable> comparables, PricingOptions? options = null)
    {
        options ??= new PricingOptions();
        var items = comparables.ToList();

        if (items.Count == 0)
        {
            return PricingResult.Empty;
        }

        var (cleaned, outliersRemoved) = RemoveOutliers(items, options.IqrMultiplier);

        if (cleaned.Count == 0)
        {
            return PricingResult.Empty;
        }

        var prices = cleaned.Select(c => c.Price).OrderBy(p => p).ToList();
        var mean = prices.Average();
        var median = CalculateMedian(prices);
        var min = prices.Min();
        var max = prices.Max();
        var stdDev = CalculateStdDev(prices, mean);

        var weightedMean = CalculateConfidenceWeightedMean(cleaned, options.ConfidenceWeightPower);
        var recencyWeightedMean = CalculateRecencyWeightedMean(cleaned, options);
        var confidence = CalculateConfidence(cleaned, options);

        return new PricingResult(
            SampleSize: cleaned.Count,
            OutliersRemoved: outliersRemoved,
            Mean: Math.Round(mean, 2),
            Median: Math.Round(median, 2),
            WeightedMean: Math.Round(weightedMean, 2),
            RecencyWeightedMean: recencyWeightedMean.HasValue ? Math.Round(recencyWeightedMean.Value, 2) : null,
            Min: Math.Round(min, 2),
            Max: Math.Round(max, 2),
            StdDev: Math.Round(stdDev, 2),
            Confidence: Math.Round(confidence, 4));
    }

    internal static (List<PricedComparable> Cleaned, int OutliersRemoved) RemoveOutliers(
        List<PricedComparable> items, double iqrMultiplier)
    {
        if (items.Count < 4)
        {
            return (items, 0);
        }

        var prices = items.Select(c => c.Price).OrderBy(p => p).ToList();
        var q1 = CalculatePercentile(prices, 25);
        var q3 = CalculatePercentile(prices, 75);
        var iqr = q3 - q1;

        if (iqr == 0)
        {
            return (items, 0);
        }

        var lowerBound = q1 - (decimal)iqrMultiplier * iqr;
        var upperBound = q3 + (decimal)iqrMultiplier * iqr;

        var cleaned = new List<PricedComparable>();
        var outliersRemoved = 0;

        foreach (var item in items)
        {
            if (item.Price >= lowerBound && item.Price <= upperBound)
            {
                cleaned.Add(item);
            }
            else
            {
                outliersRemoved++;
            }
        }

        return (cleaned, outliersRemoved);
    }

    internal static decimal CalculateConfidenceWeightedMean(
        List<PricedComparable> items, double power)
    {
        var totalWeight = 0.0;
        var weightedSum = 0.0;

        foreach (var item in items)
        {
            var weight = Math.Pow(item.ClassifierConfidence, power);
            weightedSum += (double)item.Price * weight;
            totalWeight += weight;
        }

        return totalWeight > 0 ? (decimal)(weightedSum / totalWeight) : 0;
    }

    internal static decimal? CalculateRecencyWeightedMean(
        List<PricedComparable> items, PricingOptions options)
    {
        var itemsWithDates = items.Where(i => i.SoldDateUtc.HasValue).ToList();

        if (itemsWithDates.Count == 0)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var totalWeight = 0.0;
        var weightedSum = 0.0;

        foreach (var item in itemsWithDates)
        {
            var daysSince = (now - item.SoldDateUtc!.Value).TotalDays;
            var recencyWeight = Math.Exp(-daysSince / options.RecencyHalfLifeDays);
            var combinedWeight = Math.Pow(item.ClassifierConfidence, options.ConfidenceWeightPower) * recencyWeight;

            weightedSum += (double)item.Price * combinedWeight;
            totalWeight += combinedWeight;
        }

        return totalWeight > 0 ? (decimal)(weightedSum / totalWeight) : null;
    }

    internal static double CalculateConfidence(List<PricedComparable> items, PricingOptions options)
    {
        if (items.Count == 0)
        {
            return 0;
        }

        var sampleFactor = 1 - Math.Exp(-items.Count / (double)options.ConfidenceSampleTarget);
        var avgClassifierConfidence = items.Average(i => i.ClassifierConfidence);

        var prices = items.Select(p => (double)p.Price).ToList();
        var mean = prices.Average();
        var stdDev = CalculateStdDevDouble(prices, mean);
        var cv = mean > 0 ? stdDev / mean : 1;
        var consistencyFactor = Math.Max(0, 1 - cv);

        var confidence =
            options.SampleSizeWeight * sampleFactor +
            options.ClassifierConfidenceWeight * avgClassifierConfidence +
            options.ConsistencyWeight * consistencyFactor;

        return Math.Min(1.0, Math.Max(0.0, confidence));
    }

    internal static decimal CalculateMedian(List<decimal> sortedPrices)
    {
        var count = sortedPrices.Count;
        if (count == 0)
        {
            return 0;
        }

        if (count % 2 == 0)
        {
            return (sortedPrices[count / 2 - 1] + sortedPrices[count / 2]) / 2;
        }

        return sortedPrices[count / 2];
    }

    internal static decimal CalculatePercentile(List<decimal> sortedPrices, int percentile)
    {
        if (sortedPrices.Count == 0)
        {
            return 0;
        }

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
        if (prices.Count < 2)
        {
            return 0;
        }

        var sumSquaredDiff = prices.Sum(p => (p - mean) * (p - mean));
        return (decimal)Math.Sqrt((double)(sumSquaredDiff / (prices.Count - 1)));
    }

    private static double CalculateStdDevDouble(List<double> prices, double mean)
    {
        if (prices.Count < 2)
        {
            return 0;
        }

        var sumSquaredDiff = prices.Sum(p => (p - mean) * (p - mean));
        return Math.Sqrt(sumSquaredDiff / (prices.Count - 1));
    }
}
