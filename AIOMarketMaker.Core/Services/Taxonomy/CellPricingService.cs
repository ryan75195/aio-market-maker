using System.Text.RegularExpressions;

namespace AIOMarketMaker.Core.Services.Taxonomy;

public partial class CellPricingService : ICellPricingService
{
    private const double CvSplitThreshold = 0.5;
    private const double MinCvReductionPercent = 20.0;
    private const int MinSoldForSplit = 6;
    private const int MinSubGroupSize = 3;

    public CellPricingResult Compute(
        TaxonomyResult taxonomy,
        IEnumerable<PricedListing> listings,
        double feePercent,
        int minComps)
    {
        var listingList = listings.ToList();
        var assignmentLookup = taxonomy.Assignments
            .ToDictionary(a => a.ListingIndex);

        var cellGroups = new Dictionary<string, List<PricedListing>>();
        var cellMaps = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var coveredCount = 0;

        foreach (var listing in listingList)
        {
            if (!assignmentLookup.TryGetValue(listing.ListingIndex, out var assignment)
                || assignment.Cell.Count == 0)
            {
                continue;
            }

            coveredCount++;
            var cell = new Dictionary<string, string>(assignment.Cell);
            if (listing.Condition != null)
            {
                cell["condition"] = listing.Condition;
            }
            var cellKey = BuildCellKey(cell);
            if (!cellGroups.ContainsKey(cellKey))
            {
                cellGroups[cellKey] = new List<PricedListing>();
                cellMaps[cellKey] = cell;
            }
            cellGroups[cellKey].Add(listing);
        }

        var cells = new List<CellPricing>();
        var opportunities = new List<ArbitrageOpportunity>();
        var feeFraction = (decimal)(feePercent / 100.0);

        foreach (var (cellKey, group) in cellGroups)
        {
            var subGroups = TrySplitByVariance(group, cellKey);
            foreach (var (subKey, subGroup) in subGroups)
            {
                var resolvedKey = subKey ?? cellKey;
                var resolvedMap = subKey != null
                    ? ParseCellKey(resolvedKey)
                    : cellMaps[cellKey];

                ProcessCellGroup(resolvedKey, resolvedMap, subGroup,
                    feeFraction, minComps, cells, opportunities);
            }
        }

        opportunities.Sort((a, b) => b.EstimatedProfit.CompareTo(a.EstimatedProfit));

        return new CellPricingResult(
            cells, opportunities,
            listingList.Count,
            listingList.Count(l => l.Price > 0),
            coveredCount);
    }

    private static void ProcessCellGroup(
        string cellKey,
        IReadOnlyDictionary<string, string> cellMap,
        List<PricedListing> group,
        decimal feeFraction,
        int minComps,
        List<CellPricing> cells,
        List<ArbitrageOpportunity> opportunities)
    {
        var sold = group.Where(l => l.IsSold).Select(l => l.Price).OrderBy(p => p).ToList();
        var active = group.Where(l => !l.IsSold).Select(l => l.Price).OrderBy(p => p).ToList();

        var medianSold = sold.Count > 0 ? Median(sold) : (decimal?)null;
        var medianActive = active.Count > 0 ? Median(active) : (decimal?)null;
        var spread = medianActive.HasValue && medianSold.HasValue
            ? medianActive.Value - medianSold.Value : (decimal?)null;

        double? cv = sold.Count >= 2
            ? CoefficientOfVariation(sold.Select(p => (double)p).ToArray())
            : null;

        cells.Add(new CellPricing(
            cellKey, cellMap,
            active.Count, sold.Count,
            medianActive, medianSold, spread, cv));

        if (medianSold == null || sold.Count < minComps)
        {
            return;
        }

        if (cv.HasValue && cv.Value >= CvSplitThreshold)
        {
            return;
        }

        foreach (var listing in group.Where(l => !l.IsSold))
        {
            if (medianSold.Value > 0 && listing.Price / medianSold.Value < 0.15m)
            {
                continue;
            }

            var fees = medianSold.Value * feeFraction;
            var profit = medianSold.Value - listing.Price - fees;
            if (profit <= 0)
            {
                continue;
            }

            var margin = (double)(profit / medianSold.Value) * 100.0;
            opportunities.Add(new ArbitrageOpportunity(
                listing.ListingId, listing.Title, listing.Price,
                medianSold.Value, Math.Round(profit, 2), Math.Round(margin, 1),
                sold.Count, cellKey));
        }
    }

    private static IEnumerable<(string? SubKey, List<PricedListing> Group)> TrySplitByVariance(
        List<PricedListing> group, string cellKey)
    {
        var sold = group.Where(l => l.IsSold).ToList();
        if (sold.Count < MinSoldForSplit)
        {
            return [(null, group)];
        }

        var soldPrices = sold.Select(l => (double)l.Price).ToArray();
        var cv = CoefficientOfVariation(soldPrices);
        if (cv < CvSplitThreshold)
        {
            return [(null, group)];
        }

        var bestSplit = FindBestSplitToken(group, sold, cv);
        if (bestSplit == null)
        {
            return [(null, group)];
        }

        var tokenSet = TokenizeTitle(bestSplit);
        var withToken = new List<PricedListing>();
        var withoutToken = new List<PricedListing>();
        foreach (var listing in group)
        {
            if (TokenizeTitle(listing.Title).Overlaps(tokenSet))
            {
                withToken.Add(listing);
            }
            else
            {
                withoutToken.Add(listing);
            }
        }

        var splitKey = string.IsNullOrEmpty(cellKey)
            ? $"_split={bestSplit}"
            : $"{cellKey} | _split={bestSplit}";

        return
        [
            (splitKey, withToken),
            (null, withoutToken),
        ];
    }

    private static string? FindBestSplitToken(
        List<PricedListing> group,
        List<PricedListing> sold,
        double originalCv)
    {
        var tokenCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var listingTokens = new Dictionary<int, HashSet<string>>();

        foreach (var listing in sold)
        {
            var tokens = TokenizeTitle(listing.Title);
            listingTokens[listing.ListingId] = tokens;
            foreach (var token in tokens)
            {
                tokenCounts[token] = tokenCounts.GetValueOrDefault(token) + 1;
            }
        }

        string? bestToken = null;
        var bestReduction = 0.0;

        foreach (var (token, count) in tokenCounts)
        {
            var frac = (double)count / sold.Count;
            if (frac < 0.15 || frac > 0.85)
            {
                continue;
            }

            var withPrices = new List<double>();
            var withoutPrices = new List<double>();
            foreach (var listing in sold)
            {
                if (listingTokens[listing.ListingId].Contains(token))
                {
                    withPrices.Add((double)listing.Price);
                }
                else
                {
                    withoutPrices.Add((double)listing.Price);
                }
            }

            if (withPrices.Count < MinSubGroupSize || withoutPrices.Count < MinSubGroupSize)
            {
                continue;
            }

            var splitCv = WeightedAverageCv(withPrices, withoutPrices);
            var reduction = (originalCv - splitCv) / originalCv * 100.0;
            if (reduction > bestReduction)
            {
                bestReduction = reduction;
                bestToken = token;
            }
        }

        if (bestReduction < MinCvReductionPercent)
        {
            return null;
        }

        return bestToken;
    }

    private static HashSet<string> TokenizeTitle(string title)
    {
        return new HashSet<string>(
            WordPattern().Matches(title.ToLowerInvariant()).Select(m => m.Value)
                .Where(w => w.Length > 1),
            StringComparer.OrdinalIgnoreCase);
    }

    private static double CoefficientOfVariation(double[] values)
    {
        if (values.Length < 2)
        {
            return 0.0;
        }

        var mean = values.Average();
        if (mean == 0)
        {
            return 0.0;
        }

        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Length;
        return Math.Sqrt(variance) / mean;
    }

    private static double WeightedAverageCv(List<double> groupA, List<double> groupB)
    {
        var total = groupA.Count + groupB.Count;
        var cvA = CoefficientOfVariation(groupA.ToArray());
        var cvB = CoefficientOfVariation(groupB.ToArray());
        return cvA * groupA.Count / total + cvB * groupB.Count / total;
    }

    private static IReadOnlyDictionary<string, string> ParseCellKey(string cellKey)
    {
        var dict = new Dictionary<string, string>();
        foreach (var part in cellKey.Split(" | "))
        {
            var eqIndex = part.IndexOf('=');
            if (eqIndex > 0)
            {
                dict[part[..eqIndex]] = part[(eqIndex + 1)..];
            }
        }
        return dict;
    }

    private static string BuildCellKey(IReadOnlyDictionary<string, string> cell) =>
        string.Join(" | ", cell.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));

    private static decimal Median(List<decimal> sorted)
    {
        var count = sorted.Count;
        if (count == 0)
        {
            return 0;
        }

        if (count % 2 == 1)
        {
            return sorted[count / 2];
        }

        return (sorted[count / 2 - 1] + sorted[count / 2]) / 2m;
    }

    [GeneratedRegex(@"\b\w+\b")]
    private static partial Regex WordPattern();
}
