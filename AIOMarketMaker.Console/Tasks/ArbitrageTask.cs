using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Console.Tasks;

record ArbitrageListingProjection(int Id, string? Title, decimal? Price, string? ListingStatus, string? Condition);

public class ArbitrageTask : ITask
{
    private readonly IDbContextFactory<EtlDbContext> _dbFactory;
    private readonly ITaxonomyService _taxonomyService;
    private readonly ICellPricingService _pricingService;

    public string Name => "arbitrage";
    public string Description => "Find arbitrage opportunities using taxonomy cells. " +
        "Usage: arbitrage <jobId> [--fee 13.25] [--min-comps 3]";

    public ArbitrageTask(
        IDbContextFactory<EtlDbContext> dbFactory,
        ITaxonomyService taxonomyService,
        ICellPricingService pricingService)
    {
        _dbFactory = dbFactory;
        _taxonomyService = taxonomyService;
        _pricingService = pricingService;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0 || !int.TryParse(args[0], out var jobId))
        {
            System.Console.WriteLine(Description);
            return 1;
        }

        var feePercent = CommandHelpers.GetDoubleFlag(args, "--fee", 13.25);
        var minComps = CommandHelpers.GetIntFlag(args, "--min-comps", 3);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var job = await db.ScrapeJobs.FindAsync(new object[] { jobId }, ct);
        if (job == null)
        {
            System.Console.WriteLine($"Job {jobId} not found.");
            return 1;
        }

        System.Console.WriteLine($"Job {jobId}: \"{job.SearchTerm}\"");
        System.Console.WriteLine($"Fee: {feePercent}%, Min comps: {minComps}");
        System.Console.WriteLine();

        var listings = await LoadListings(db, jobId, ct);
        if (listings.Count == 0)
        {
            System.Console.WriteLine("No priced listings found.");
            return 1;
        }

        System.Console.WriteLine($"Loaded {listings.Count} priced listings.");

        var taxonomy = await RunTaxonomy(listings, ct);

        var pricedListings = listings.Select((l, idx) => new PricedListing(
            l.Id, l.Title!, l.Price!.Value,
            IsSold: l.ListingStatus == "Sold",
            ListingIndex: idx,
            Condition: l.Condition));

        var result = _pricingService.Compute(taxonomy, pricedListings, feePercent, minComps);

        var focusListingId = CommandHelpers.GetIntFlag(args, "--listing", 0);
        if (focusListingId > 0)
        {
            PrintListingDrilldown(focusListingId, listings, taxonomy, result);
            return 0;
        }

        PrintCellSummary(result);
        PrintOpportunities(result);

        System.Console.WriteLine($"Summary: {result.TotalListings} listings, " +
            $"{result.CoveredListings} covered by taxonomy, " +
            $"{result.Opportunities.Count()} opportunities");

        return 0;
    }

    private static async Task<List<ArbitrageListingProjection>> LoadListings(
        EtlDbContext db, int jobId, CancellationToken ct)
    {
        return await db.Listings
            .AsNoTracking()
            .Where(l => l.ScrapeJobId == jobId && l.Title != null && l.Price != null)
            .OrderBy(l => l.Id)
            .Select(l => new ArbitrageListingProjection(l.Id, l.Title, l.Price, l.ListingStatus, l.Condition))
            .ToListAsync(ct);
    }

    private async Task<TaxonomyResult> RunTaxonomy(
        List<ArbitrageListingProjection> listings, CancellationToken ct)
    {
        var titles = listings.Select(l => l.Title!).ToList();
        System.Console.WriteLine("Running taxonomy...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var taxonomy = await _taxonomyService.Generate(titles, ct: ct);
        sw.Stop();
        System.Console.WriteLine($"Taxonomy: {taxonomy.Axes.Count()} axes, " +
            $"{taxonomy.CoveragePercent:F1}% coverage in {sw.Elapsed.TotalSeconds:F1}s");
        System.Console.WriteLine();
        return taxonomy;
    }

    private static void PrintCellSummary(CellPricingResult result)
    {
        var cellsList = result.Cells.OrderByDescending(c => c.SoldCount).ToList();
        System.Console.WriteLine($"=== CELL PRICING ({cellsList.Count} cells) ===");
        System.Console.WriteLine();
        System.Console.WriteLine($"{"Cell",-80} {"Active",7} {"Sold",7} {"Med.Active",11} {"Med.Sold",11} {"Spread",9}");
        System.Console.WriteLine(new string('-', 125));

        foreach (var cell in cellsList.Take(20))
        {
            var cellName = cell.CellKey.Length > 78 ? cell.CellKey[..75] + "..." : cell.CellKey;
            var medActive = cell.MedianActivePrice.HasValue ? $"{cell.MedianActivePrice:C0}" : "-";
            var medSold = cell.MedianSoldPrice.HasValue ? $"{cell.MedianSoldPrice:C0}" : "-";
            var spread = cell.Spread.HasValue ? $"{cell.Spread:C0}" : "-";

            System.Console.WriteLine($"{cellName,-80} {cell.ActiveCount,7} {cell.SoldCount,7} {medActive,11} {medSold,11} {spread,9}");
        }
        System.Console.WriteLine();
    }

    private static void PrintListingDrilldown(
        int listingId,
        List<ArbitrageListingProjection> listings,
        TaxonomyResult taxonomy,
        CellPricingResult result)
    {
        var idx = listings.FindIndex(l => l.Id == listingId);
        if (idx < 0)
        {
            System.Console.WriteLine($"Listing {listingId} not found in this job's priced listings.");
            return;
        }

        var listing = listings[idx];
        System.Console.WriteLine($"=== LISTING DRILLDOWN: {listingId} ===");
        System.Console.WriteLine($"Title:  {listing.Title}");
        System.Console.WriteLine($"Price:  {listing.Price:C2}");
        System.Console.WriteLine($"Status: {listing.ListingStatus}");
        System.Console.WriteLine();

        // Find taxonomy cell assignment
        var assignments = taxonomy.Assignments.ToList();
        if (idx < assignments.Count)
        {
            var assignment = assignments[idx];
            if (assignment.Cell.Count > 0)
            {
                var cellKey = string.Join(" | ", assignment.Cell.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
                System.Console.WriteLine($"Cell: {cellKey}");
                System.Console.WriteLine($"Has conflict: {assignment.HasConflict}");
                System.Console.WriteLine();

                // Find this cell in pricing results
                var cellPricing = result.Cells.FirstOrDefault(c => c.CellKey == cellKey);
                if (cellPricing != null)
                {
                    System.Console.WriteLine($"=== CELL STATS ===");
                    System.Console.WriteLine($"Active count:       {cellPricing.ActiveCount}");
                    System.Console.WriteLine($"Sold count:         {cellPricing.SoldCount}");
                    System.Console.WriteLine($"Median active:      {(cellPricing.MedianActivePrice.HasValue ? $"{cellPricing.MedianActivePrice:C2}" : "-")}");
                    System.Console.WriteLine($"Median sold:        {(cellPricing.MedianSoldPrice.HasValue ? $"{cellPricing.MedianSoldPrice:C2}" : "-")}");
                    System.Console.WriteLine($"Spread:             {(cellPricing.Spread.HasValue ? $"{cellPricing.Spread:C2}" : "-")}");
                    System.Console.WriteLine();

                    // Show all listings in this cell
                    var cellMembers = listings
                        .Select((l, i) => (Listing: l, Index: i))
                        .Where(x => x.Index < assignments.Count
                            && string.Join(" | ", assignments[x.Index].Cell.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")) == cellKey)
                        .ToList();

                    System.Console.WriteLine($"=== ALL LISTINGS IN CELL ({cellMembers.Count}) ===");
                    System.Console.WriteLine($"{"ID",8} {"Status",-8} {"Price",10} {"Title"}");
                    System.Console.WriteLine(new string('-', 100));
                    foreach (var member in cellMembers.OrderBy(m => m.Listing.Price))
                    {
                        var marker = member.Listing.Id == listingId ? " <--" : "";
                        System.Console.WriteLine($"{member.Listing.Id,8} {member.Listing.ListingStatus ?? "?",-8} {member.Listing.Price,10:C2} {member.Listing.Title}{marker}");
                    }
                }
                else
                {
                    System.Console.WriteLine("Cell not found in pricing results.");
                }
            }
            else
            {
                System.Console.WriteLine("Listing was NOT assigned to any taxonomy cell.");
            }
        }

        System.Console.WriteLine();

        // Check if it's in opportunities
        var opp = result.Opportunities.FirstOrDefault(o => o.ListingId == listingId);
        if (opp != null)
        {
            System.Console.WriteLine($"=== ARBITRAGE OPPORTUNITY ===");
            System.Console.WriteLine($"Ask:           {opp.AskPrice:C2}");
            System.Console.WriteLine($"Median sold:   {opp.MedianSoldPrice:C2}");
            System.Console.WriteLine($"Est. profit:   {opp.EstimatedProfit:C2}");
            System.Console.WriteLine($"Margin:        {opp.MarginPercent:F1}%");
            System.Console.WriteLine($"Sold comps:    {opp.SoldComps}");
        }
        else
        {
            System.Console.WriteLine("NOT flagged as an arbitrage opportunity.");
        }
    }

    private static void PrintOpportunities(CellPricingResult result)
    {
        var opps = result.Opportunities.ToList();
        System.Console.WriteLine($"=== ARBITRAGE OPPORTUNITIES ({opps.Count} found) ===");
        System.Console.WriteLine();

        if (opps.Count == 0)
        {
            System.Console.WriteLine("No arbitrage opportunities found.");
            return;
        }

        System.Console.WriteLine($"{"Title",-45} {"Ask",9} {"Sells For",10} {"Profit",9} {"Margin",7} {"Comps",5}");
        System.Console.WriteLine(new string('-', 90));

        foreach (var opp in opps.Take(30))
        {
            var title = opp.Title.Length > 43 ? opp.Title[..40] + "..." : opp.Title;
            System.Console.WriteLine(
                $"{title,-45} {opp.AskPrice,9:C0} {opp.MedianSoldPrice,10:C0} " +
                $"{opp.EstimatedProfit,9:C2} {opp.MarginPercent,6:F1}% {opp.SoldComps,5}");
        }

        System.Console.WriteLine();
    }
}
