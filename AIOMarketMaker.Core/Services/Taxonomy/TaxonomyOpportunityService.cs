using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Core.Services.Taxonomy;

record ListingProjection(
    int Id, string? Title, decimal? Price, string? ListingStatus,
    string? Condition, DateTime CreatedUtc, DateTime? EndDateUtc);

public class TaxonomyOpportunityService : ITaxonomyOpportunityService
{
    private readonly IDbContextFactory<EtlDbContext> _dbFactory;
    private readonly ITaxonomyQueryService _taxonomyQuery;
    private readonly ICellPricingService _cellPricing;

    public TaxonomyOpportunityService(
        IDbContextFactory<EtlDbContext> dbFactory,
        ITaxonomyQueryService taxonomyQuery,
        ICellPricingService cellPricing)
    {
        _dbFactory = dbFactory;
        _taxonomyQuery = taxonomyQuery;
        _cellPricing = cellPricing;
    }

    public async Task<int> Compute(int jobId, double feePercent, int minComps, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 1. Delete existing opportunities for this job
        var existing = await db.TaxonomyOpportunities
            .Where(o => o.ScrapeJobId == jobId)
            .ToListAsync(ct);
        db.TaxonomyOpportunities.RemoveRange(existing);
        await db.SaveChangesAsync(ct);

        // 2. Get taxonomy assignments
        var assignments = (await _taxonomyQuery.GetAssignments(jobId, ct)).ToList();
        if (assignments.Count == 0)
        {
            return 0;
        }

        // 3. Load listings for this job
        var assignedListingIds = assignments.Select(a => a.ListingId).ToHashSet();
        var listings = await db.Listings
            .AsNoTracking()
            .Where(l => l.ScrapeJobId == jobId && assignedListingIds.Contains(l.Id))
            .Select(l => new ListingProjection(
                l.Id, l.Title, l.Price, l.ListingStatus,
                l.Condition, l.CreatedUtc, l.EndDateUtc))
            .ToListAsync(ct);

        var listingLookup = listings.ToDictionary(l => l.Id);

        // 4. Build PricedListing list and CellAssignment list
        var pricedListings = new List<PricedListing>();
        var cellAssignments = new List<CellAssignment>();

        var index = 0;
        foreach (var assignment in assignments)
        {
            if (!listingLookup.TryGetValue(assignment.ListingId, out var listing))
            {
                continue;
            }

            var isSold = string.Equals(listing.ListingStatus, "Sold", StringComparison.OrdinalIgnoreCase);
            var price = listing.Price ?? 0m;

            pricedListings.Add(new PricedListing(
                listing.Id,
                listing.Title ?? "",
                price,
                isSold,
                index,
                listing.Condition));

            cellAssignments.Add(new CellAssignment(
                index,
                assignment.Cell,
                HasConflict: false));

            index++;
        }

        if (pricedListings.Count == 0)
        {
            return 0;
        }

        // 5. Build TaxonomyResult and compute pricing
        var taxonomyResult = new TaxonomyResult(
            Enumerable.Empty<Axis>(),
            cellAssignments,
            Enumerable.Empty<CellStats>(),
            CoveragePercent: 100.0,
            ConflictPercent: 0.0);

        var pricingResult = _cellPricing.Compute(taxonomyResult, pricedListings, feePercent, minComps);

        var opportunities = pricingResult.Opportunities.ToList();
        if (opportunities.Count == 0)
        {
            return 0;
        }

        // 6. Calculate AvgDaysToSell per cell from sold listings
        var avgDaysToSellByCell = ComputeAvgDaysToSell(pricedListings, cellAssignments, listingLookup);

        // 7. Map ArbitrageOpportunity -> TaxonomyOpportunity and persist
        var now = DateTime.UtcNow;
        var entities = opportunities.Select(opp => new TaxonomyOpportunity
        {
            ScrapeJobId = jobId,
            ListingId = opp.ListingId,
            CellKey = opp.CellKey,
            AskPrice = opp.AskPrice,
            MedianSoldPrice = opp.MedianSoldPrice,
            EstimatedProfit = opp.EstimatedProfit,
            MarginPercent = opp.MarginPercent,
            SoldComps = opp.SoldComps,
            AvgDaysToSell = avgDaysToSellByCell.GetValueOrDefault(opp.CellKey),
            ComputedUtc = now
        }).ToList();

        db.TaxonomyOpportunities.AddRange(entities);
        await db.SaveChangesAsync(ct);

        return entities.Count;
    }

    private static Dictionary<string, int?> ComputeAvgDaysToSell(
        List<PricedListing> pricedListings,
        List<CellAssignment> cellAssignments,
        Dictionary<int, ListingProjection> listingLookup)
    {
        // Build a mapping from ListingIndex -> cell key (matching CellPricingService logic)
        var indexToCellKey = new Dictionary<int, string>();
        foreach (var assignment in cellAssignments)
        {
            if (assignment.Cell.Count == 0)
            {
                continue;
            }

            var listing = pricedListings.FirstOrDefault(l => l.ListingIndex == assignment.ListingIndex);
            if (listing == null)
            {
                continue;
            }

            var cell = new Dictionary<string, string>(assignment.Cell);
            if (listing.Condition != null)
            {
                cell["condition"] = listing.Condition;
            }

            var cellKey = string.Join(" | ", cell.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
            indexToCellKey[assignment.ListingIndex] = cellKey;
        }

        // Group sold listings by cell key and compute average days to sell
        var cellDays = new Dictionary<string, List<int>>();

        foreach (var listing in pricedListings)
        {
            if (!listing.IsSold)
            {
                continue;
            }

            if (!indexToCellKey.TryGetValue(listing.ListingIndex, out var cellKey))
            {
                continue;
            }

            if (!listingLookup.TryGetValue(listing.ListingId, out var dbListing))
            {
                continue;
            }

            if (dbListing.EndDateUtc == null)
            {
                continue;
            }

            var days = (int)(dbListing.EndDateUtc.Value - dbListing.CreatedUtc).TotalDays;
            if (days < 0)
            {
                continue;
            }

            if (!cellDays.TryGetValue(cellKey, out var daysList))
            {
                daysList = new List<int>();
                cellDays[cellKey] = daysList;
            }

            daysList.Add(days);
        }

        var result = new Dictionary<string, int?>();
        foreach (var (cellKey, daysList) in cellDays)
        {
            result[cellKey] = (int)Math.Round(daysList.Average());
        }

        return result;
    }
}
