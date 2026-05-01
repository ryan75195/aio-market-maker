# Cell-Based Pricing Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Compute per-cell pricing stats from taxonomy assignments + listing prices, and identify arbitrage opportunities (active listings priced below their cell's median sold price minus fees).

**Architecture:** A new `ICellPricingService` takes a taxonomy result + listings with prices/status and produces cell-level pricing stats and scored arbitrage opportunities. A console task (`arbitrage`) runs it against a scrape job's existing taxonomy and listings. No new DB tables — output to console for validation. DB persistence is a follow-up.

**Tech Stack:** C#, EF Core (read-only queries), existing TaxonomyPersistenceService, existing Listing model

---

### Task 1: Define ICellPricingService and records

**Files:**
- Create: `AIOMarketMaker.Core/Services/Taxonomy/ICellPricingService.cs`

**Step 1: Write the interface and records**

```csharp
namespace AIOMarketMaker.Core.Services.Taxonomy;

public record PricedListing(int ListingId, string Title, decimal Price, bool IsSold, int ListingIndex);

public record CellPricingResult(
    IEnumerable<CellPricing> Cells,
    IEnumerable<ArbitrageOpportunity> Opportunities,
    int TotalListings,
    int PricedListings,
    int CoveredListings);

public record CellPricing(
    string CellKey,
    IReadOnlyDictionary<string, string> Cell,
    int ActiveCount,
    int SoldCount,
    decimal? MedianActivePrice,
    decimal? MedianSoldPrice,
    decimal? Spread);

public record ArbitrageOpportunity(
    int ListingId,
    string Title,
    decimal AskPrice,
    decimal MedianSoldPrice,
    decimal EstimatedProfit,
    double MarginPercent,
    int SoldComps,
    string CellKey);

public interface ICellPricingService
{
    CellPricingResult Compute(
        TaxonomyResult taxonomy,
        IEnumerable<PricedListing> listings,
        double feePercent,
        int minComps);
}
```

**Step 2: Commit**

```
feat: add ICellPricingService interface and pricing records
```

---

### Task 2: Write failing tests for CellPricingService

**Files:**
- Create: `AIOMarketMaker.Tests.Unit/Taxonomy/CellPricingService_UnitTests.cs`

**Step 1: Write the tests**

```csharp
using AIOMarketMaker.Core.Services.Taxonomy;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class CellPricingService_UnitTests
{
    private CellPricingService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new CellPricingService();
    }

    [Test]
    public void Should_compute_median_sold_price_per_cell()
    {
        // 3 sold listings in same cell at £100, £200, £300 → median = £200
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("Axis 0", new[] { "digital", "disc" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (1, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (2, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
            });

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital", 100m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "PS5 Digital", 200m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "PS5 Digital", 300m, IsSold: true, ListingIndex: 2),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 13.25, minComps: 1);
        var cell = result.Cells.Single();

        Assert.That(cell.MedianSoldPrice, Is.EqualTo(200m));
    }

    [Test]
    public void Should_flag_underpriced_active_listing_as_opportunity()
    {
        // Sold at £200 median, active at £100, fees 13.25% → profit = £200 - £100 - £26.50 = £73.50
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("Axis 0", new[] { "digital", "disc" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (1, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (2, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (3, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
            });

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital Sold 1", 150m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "PS5 Digital Sold 2", 200m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "PS5 Digital Sold 3", 250m, IsSold: true, ListingIndex: 2),
            new PricedListing(4, "PS5 Digital Cheap", 100m, IsSold: false, ListingIndex: 3),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 13.25, minComps: 3);

        Assert.That(result.Opportunities.Count(), Is.EqualTo(1));
        var opp = result.Opportunities.Single();
        Assert.Multiple(() =>
        {
            Assert.That(opp.ListingId, Is.EqualTo(4));
            Assert.That(opp.AskPrice, Is.EqualTo(100m));
            Assert.That(opp.MedianSoldPrice, Is.EqualTo(200m));
            Assert.That(opp.EstimatedProfit, Is.EqualTo(73.50m));
        });
    }

    [Test]
    public void Should_not_flag_overpriced_active_listing()
    {
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("Axis 0", new[] { "digital", "disc" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (1, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (2, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
            });

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital Sold", 200m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "PS5 Digital Sold", 200m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "PS5 Digital Active", 250m, IsSold: false, ListingIndex: 2),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 13.25, minComps: 1);

        Assert.That(result.Opportunities.Count(), Is.EqualTo(0));
    }

    [Test]
    public void Should_skip_cells_with_fewer_than_min_comps()
    {
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("Axis 0", new[] { "digital", "disc" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (1, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
            });

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital Sold", 200m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "PS5 Digital Active", 50m, IsSold: false, ListingIndex: 1),
        };

        // minComps=3 but only 1 sold → no opportunity
        var result = _service.Compute(taxonomy, listings, feePercent: 13.25, minComps: 3);

        Assert.That(result.Opportunities.Count(), Is.EqualTo(0));
    }

    [Test]
    public void Should_handle_unassigned_listings()
    {
        // Listing at index 1 has empty cell → should be excluded from pricing
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("Axis 0", new[] { "digital", "disc" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (1, new Dictionary<string, string>()),
            });

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital Sold", 200m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "Random Uncovered", 10m, IsSold: false, ListingIndex: 1),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 13.25, minComps: 1);

        Assert.That(result.Opportunities.Count(), Is.EqualTo(0));
        Assert.That(result.Cells.Count(), Is.EqualTo(1));
    }

    [Test]
    public void Should_separate_pricing_across_different_cells()
    {
        // Digital sells for £200, Disc sells for £400
        // Active Digital at £150 → opportunity. Active Disc at £350 → opportunity.
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("Axis 0", new[] { "digital", "disc" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (1, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (2, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (3, new Dictionary<string, string> { ["Axis 0"] = "disc" }),
                (4, new Dictionary<string, string> { ["Axis 0"] = "disc" }),
                (5, new Dictionary<string, string> { ["Axis 0"] = "disc" }),
            });

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital Sold", 200m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "PS5 Digital Sold", 200m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "PS5 Digital Active", 150m, IsSold: false, ListingIndex: 2),
            new PricedListing(4, "PS5 Disc Sold", 400m, IsSold: true, ListingIndex: 3),
            new PricedListing(5, "PS5 Disc Sold", 400m, IsSold: true, ListingIndex: 4),
            new PricedListing(6, "PS5 Disc Active", 350m, IsSold: false, ListingIndex: 5),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 0, minComps: 2);

        Assert.That(result.Opportunities.Count(), Is.EqualTo(2));
        var digital = result.Opportunities.First(o => o.CellKey.Contains("digital"));
        var disc = result.Opportunities.First(o => o.CellKey.Contains("disc"));
        Assert.Multiple(() =>
        {
            Assert.That(digital.MedianSoldPrice, Is.EqualTo(200m));
            Assert.That(disc.MedianSoldPrice, Is.EqualTo(400m));
        });
    }

    // -- Helpers --

    private static TaxonomyResult BuildTaxonomy(
        (string Name, string[] Values)[] axes,
        (int ListingIndex, Dictionary<string, string> Cell)[] assignments)
    {
        var taxAxes = axes.Select(a =>
            new Axis(a.Name, a.Values.Select(v =>
                new AxisValue(v, new[] { new Ngram(v, new[] { v }, 10) })))).ToList();

        var taxAssignments = assignments.Select(a =>
            new CellAssignment(a.ListingIndex, a.Cell, false)).ToList();

        return new TaxonomyResult(taxAxes, taxAssignments, Enumerable.Empty<CellStats>(), 100.0, 0.0);
    }
}
```

**Step 2: Run to verify they fail**

```bash
dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~CellPricingService" -v n
```

Expected: compilation error (CellPricingService doesn't exist yet)

**Step 3: Commit**

```
test: add CellPricingService unit tests
```

---

### Task 3: Implement CellPricingService

**Files:**
- Create: `AIOMarketMaker.Core/Services/Taxonomy/CellPricingService.cs`

**Step 1: Implement the service**

```csharp
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

        // Group listings by cell key
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

        // Compute per-cell pricing
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

            // Find active listings priced below (median sold - fees)
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
```

**Step 2: Run tests**

```bash
dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~CellPricingService" -v n
```

Expected: all 6 tests pass

**Step 3: Commit**

```
feat: implement CellPricingService with cell-based arbitrage detection
```

---

### Task 4: Create ArbitrageTask console command

**Files:**
- Create: `AIOMarketMaker.Console/Tasks/ArbitrageTask.cs`
- Modify: `AIOMarketMaker.Console/Startup.cs` (add DI registration)

**Step 1: Write the task**

The task loads listings + taxonomy for a given job from the DB, runs CellPricingService, and prints results.

```csharp
using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Console.Tasks;

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

        // Load listings with prices
        var listings = await db.Listings
            .AsNoTracking()
            .Where(l => l.ScrapeJobId == jobId && l.Title != null && l.Price != null)
            .OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.Title, l.Price, l.ListingStatus })
            .ToListAsync(ct);

        if (listings.Count == 0)
        {
            System.Console.WriteLine("No priced listings found.");
            return 1;
        }

        System.Console.WriteLine($"Loaded {listings.Count} priced listings.");

        // Run taxonomy
        var titles = listings.Select(l => l.Title!).ToList();
        System.Console.WriteLine("Running taxonomy...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var taxonomy = await _taxonomyService.Generate(titles, ct);
        sw.Stop();
        System.Console.WriteLine($"Taxonomy: {taxonomy.Axes.Count()} axes, " +
            $"{taxonomy.CoveragePercent:F1}% coverage in {sw.Elapsed.TotalSeconds:F1}s");
        System.Console.WriteLine();

        // Build priced listings
        var pricedListings = listings.Select((l, idx) => new PricedListing(
            l.Id, l.Title!, l.Price!.Value,
            IsSold: l.ListingStatus == "Sold",
            ListingIndex: idx));

        // Compute pricing
        var result = _pricingService.Compute(taxonomy, pricedListings, feePercent, minComps);

        // Print cell summary
        var cellsList = result.Cells.OrderByDescending(c => c.SoldCount).ToList();
        System.Console.WriteLine($"=== CELL PRICING ({cellsList.Count} cells) ===");
        System.Console.WriteLine();
        System.Console.WriteLine($"{"Cell",-50} {"Active",7} {"Sold",7} {"Med.Active",11} {"Med.Sold",11} {"Spread",9}");
        System.Console.WriteLine(new string('-', 95));

        foreach (var cell in cellsList.Take(20))
        {
            var cellName = cell.CellKey.Length > 48 ? cell.CellKey[..45] + "..." : cell.CellKey;
            var medActive = cell.MedianActivePrice.HasValue ? $"{cell.MedianActivePrice:C0}" : "-";
            var medSold = cell.MedianSoldPrice.HasValue ? $"{cell.MedianSoldPrice:C0}" : "-";
            var spread = cell.Spread.HasValue ? $"{cell.Spread:C0}" : "-";

            System.Console.WriteLine($"{cellName,-50} {cell.ActiveCount,7} {cell.SoldCount,7} {medActive,11} {medSold,11} {spread,9}");
        }
        System.Console.WriteLine();

        // Print opportunities
        var opps = result.Opportunities.ToList();
        System.Console.WriteLine($"=== ARBITRAGE OPPORTUNITIES ({opps.Count} found) ===");
        System.Console.WriteLine();

        if (opps.Count == 0)
        {
            System.Console.WriteLine("No arbitrage opportunities found.");
            return 0;
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
        System.Console.WriteLine($"Summary: {result.TotalListings} listings, " +
            $"{result.CoveredListings} covered by taxonomy, " +
            $"{opps.Count} opportunities");

        return 0;
    }
}
```

**Step 2: Add `CommandHelpers` methods if missing**

Check if `CommandHelpers` already has `GetDoubleFlag` and `GetIntFlag`. If not, add them:

```csharp
// In AIOMarketMaker.Console/Tasks/Infrastructure/CommandHelpers.cs

public static double GetDoubleFlag(string[] args, string flag, double defaultValue)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == flag && double.TryParse(args[i + 1], out var value))
        {
            return value;
        }
    }
    return defaultValue;
}

public static int GetIntFlag(string[] args, string flag, int defaultValue)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == flag && int.TryParse(args[i + 1], out var value))
        {
            return value;
        }
    }
    return defaultValue;
}
```

**Step 3: Register in DI**

In `AIOMarketMaker.Console/Startup.cs`, add after the taxonomy registrations:

```csharp
services.AddSingleton<ICellPricingService, CellPricingService>();
```

And in the task section:

```csharp
services.AddTask<ArbitrageTask>();
```

**Step 4: Build**

```bash
dotnet build AIOMarketMaker/AIOMarketMaker.sln
```

**Step 5: Commit**

```
feat: add arbitrage console task for cell-based opportunity detection
```

---

### Task 5: Run against real data

**Step 1: Find a job with enough listings**

```bash
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "SELECT j.Id, j.SearchTerm, COUNT(l.Id) as Listings FROM ScrapeJobs j LEFT JOIN Listings l ON l.ScrapeJobId = j.Id WHERE j.IsEnabled = 1 GROUP BY j.Id, j.SearchTerm ORDER BY Listings DESC" -W
```

**Step 2: Run the arbitrage task**

```bash
dotnet run --project AIOMarketMaker/AIOMarketMaker.Console -- arbitrage <jobId>
```

**Step 3: Evaluate output**

Check:
- Do cells make sense? (e.g., separate PS5 Digital vs Disc)
- Are sold price medians reasonable?
- Are flagged opportunities real arbitrage or false positives?
- What percentage of listings have coverage?

This is a manual validation step — inspect the output before deciding next steps.
