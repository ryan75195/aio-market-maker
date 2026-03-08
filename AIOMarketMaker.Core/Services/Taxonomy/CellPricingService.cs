namespace AIOMarketMaker.Core.Services.Taxonomy;

public class CellPricingService : ICellPricingService
{
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
            var cellKey = BuildCellKey(assignment.Cell);
            if (!cellGroups.ContainsKey(cellKey))
            {
                cellGroups[cellKey] = new List<PricedListing>();
                cellMaps[cellKey] = assignment.Cell;
            }
            cellGroups[cellKey].Add(listing);
        }

        var cells = new List<CellPricing>();
        var opportunities = new List<ArbitrageOpportunity>();
        var feeFraction = (decimal)(feePercent / 100.0);

        foreach (var (cellKey, group) in cellGroups)
        {
            var sold = group.Where(l => l.IsSold).Select(l => l.Price).OrderBy(p => p).ToList();
            var active = group.Where(l => !l.IsSold).Select(l => l.Price).OrderBy(p => p).ToList();

            var medianSold = sold.Count > 0 ? Median(sold) : (decimal?)null;
            var medianActive = active.Count > 0 ? Median(active) : (decimal?)null;
            var spread = medianActive.HasValue && medianSold.HasValue
                ? medianActive.Value - medianSold.Value : (decimal?)null;

            cells.Add(new CellPricing(
                cellKey, cellMaps[cellKey],
                active.Count, sold.Count,
                medianActive, medianSold, spread));

            if (medianSold == null || sold.Count < minComps)
            {
                continue;
            }

            foreach (var listing in group.Where(l => !l.IsSold))
            {
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

        opportunities.Sort((a, b) => b.EstimatedProfit.CompareTo(a.EstimatedProfit));

        return new CellPricingResult(
            cells, opportunities,
            listingList.Count,
            listingList.Count(l => l.Price > 0),
            coveredCount);
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
}
