# Listing Prediction Service Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Extract a shared `ListingPredictionService` so the opportunities table, listing detail view, and overview dashboard all compute predictions identically via the same CTE-based SQL.

**Architecture:** A single `ListingPredictionService` class in Core owns the CTE SQL generation and all prediction queries. Endpoints become thin wrappers that parse HTTP params, call the service, and map results. The `vw_ListingPredictions` view and its EF Core model are dropped.

**Tech Stack:** .NET 8.0, EF Core (raw SQL via `DbConnection`), NUnit + Moq, SQL Server LocalDB

**Design doc:** `docs/plans/2026-02-21-listing-prediction-service-design.md`

---

## Important Context

### SQL Server vs SQLite Limitation
The CTE uses SQL Server syntax (`DATEDIFF`, `ISNULL`, `BETWEEN`, column aliases with `[]`). SQLite in-memory tests **cannot execute this SQL**. The testing strategy uses two layers:

1. **Unit tests (SQLite)**: Test the service's EF Core LINQ queries (GetComparables, filtering logic) and the endpoint wiring. For CTE-dependent methods, verify they return empty/null gracefully on SQLite (same pattern as `OverviewEndpoints.cs:121-131`).
2. **Integration tests (LocalDB)**: One focused test that verifies the CTE returns correct numbers against real data. Run with `dotnet test --filter Category=Integration`.

### Files Overview

| File | Action |
|------|--------|
| `Core/Services/ListingPredictionService.cs` | **Create** — interface, records, implementation |
| `Api/Endpoints/ListingEndpoints.cs` | **Modify** — replace CTE + in-memory code with service calls |
| `Api/Endpoints/OverviewEndpoints.cs` | **Modify** — replace CTE + temp table with service call |
| `Api/Program.cs` | **Modify** — register DI |
| `Core/Data/EtlDbContext.cs` | **Modify** — remove `ListingPrediction` DbSet + view mapping |
| `Core/Data/Models/ListingPrediction.cs` | **Delete** |
| `Core/Data/Migrations/SqlServer/041_DropListingPredictionsView.sql` | **Create** |
| `Tests.Unit/Services/ListingPredictionService_UnitTests.cs` | **Create** |
| `Tests.Unit/Endpoints/ListingEndpoints_UnitTests.cs` | **Modify** — update reflection calls for new signatures |

### Current Code Paths Being Replaced

1. **`ListingEndpoints.BuildFilteredPredictionsCte`** (lines 215-267) — CTE with priceBand, feePercent, minComps, matchCondition
2. **`ListingEndpoints.GetListingDetail`** (lines 319-416) — in-memory filtering + `vw_ListingPredictions` fallback
3. **`OverviewEndpoints.BuildFilteredPredictionsCte`** (lines 394-439) — duplicate CTE without priceBand
4. **`OverviewEndpoints.GetOpportunityData`** (lines 112-295) — materializes CTE into temp table, runs 5 aggregate queries

---

## Task 1: Create Service Interface, Records, and Stub

**Files:**
- Create: `AIOMarketMaker.Core/Services/ListingPredictionService.cs`

**Step 1: Write the service file with interface, records, and stub class**

```csharp
using System.Data;
using System.Data.Common;
using System.Globalization;
using AIOMarketMaker.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Core.Services;

public record PredictionFilters(
    decimal PriceBand = 0,
    decimal FeePercent = 0,
    bool MatchCondition = true,
    int MinComps = 0);

public record ListingPredictionResult(
    int ListingId,
    int SimilarSoldCount,
    decimal AverageSoldPrice,
    decimal PotentialProfit,
    int? EstimatedDaysToSell);

public record ComparableSoldListing(
    int RelationshipId,
    int SoldListingId,
    string? ListingId,
    string? Title,
    string? Description,
    decimal? Price,
    string? Condition,
    string? Url,
    string? Images,
    DateTime? SoldDateUtc,
    double SimilarityScore,
    string Explanation);

public record PagedPredictions(
    IEnumerable<ListingPredictionResult> Items,
    IEnumerable<int> OrderedListingIds,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages);

public record PredictionAggregates(
    int Opportunities,
    decimal AggregateProfit,
    IEnumerable<TopOpportunity> TopOpportunities,
    IEnumerable<TopJobOpportunity> TopJobsByOpportunities,
    IEnumerable<ConditionProfit> AvgProfitByCondition,
    IEnumerable<DaysToSell> AvgDaysToSellByJob,
    IEnumerable<PriceVsProfit> PriceVsProfitPoints);

public record TopOpportunity(
    string ListingId, string? Title, decimal? Price, string? Currency,
    decimal? AverageSoldPrice, decimal? PotentialProfit,
    int SimilarSoldCount, string? Condition, string? Url);

public record TopJobOpportunity(int JobId, string? SearchTerm, int OpportunityCount, decimal TotalProfit);
public record ConditionProfit(string Condition, decimal AvgProfit, int Count);
public record DaysToSell(int JobId, string? SearchTerm, decimal? AvgDaysToSell);
public record PriceVsProfit(decimal Price, decimal PotentialProfit, string? Condition);

public interface IListingPredictionService
{
    Task<ListingPredictionResult?> GetPrediction(int listingId, PredictionFilters filters);
    Task<IEnumerable<ComparableSoldListing>> GetComparables(int listingId, PredictionFilters filters);
    Task<PagedPredictions> GetPredictions(
        PredictionFilters filters, IEnumerable<int>? jobIds,
        string sortBy, string sortDir, int page, int pageSize);
    Task<PredictionAggregates> GetAggregates(PredictionFilters filters);
}

public class ListingPredictionService : IListingPredictionService
{
    private readonly EtlDbContext _db;

    public ListingPredictionService(EtlDbContext db)
    {
        _db = db;
    }

    public Task<ListingPredictionResult?> GetPrediction(int listingId, PredictionFilters filters)
    {
        throw new NotImplementedException();
    }

    public Task<IEnumerable<ComparableSoldListing>> GetComparables(int listingId, PredictionFilters filters)
    {
        throw new NotImplementedException();
    }

    public Task<PagedPredictions> GetPredictions(
        PredictionFilters filters, IEnumerable<int>? jobIds,
        string sortBy, string sortDir, int page, int pageSize)
    {
        throw new NotImplementedException();
    }

    public Task<PredictionAggregates> GetAggregates(PredictionFilters filters)
    {
        throw new NotImplementedException();
    }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Services/ListingPredictionService.cs
git commit -m "feat: add ListingPredictionService interface, records, and stub"
```

---

## Task 2: Register DI and Write Service Unit Tests (GetComparables)

**Files:**
- Modify: `AIOMarketMaker.Api/Program.cs:135` — add DI registration
- Create: `AIOMarketMaker.Tests.Unit/Services/ListingPredictionService_UnitTests.cs`

**Step 1: Register DI**

In `Program.cs`, after line 135 (`AddScoped<IComparablesEtlService>`), add:

```csharp
builder.Services.AddScoped<IListingPredictionService, ListingPredictionService>();
```

Add the using:
```csharp
using AIOMarketMaker.Core.Services;
```

**Step 2: Write failing tests for GetComparables**

```csharp
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Tests.Common;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ListingPredictionService_UnitTests
{
    private EtlDbContext _db = null!;
    private ListingPredictionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _db = InMemoryDbContextFactory.Create();
        _service = new ListingPredictionService(_db);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    // -- Helpers --

    private ScrapeJob AddJob(string searchTerm = "PS5")
    {
        var job = new ScrapeJob { SearchTerm = searchTerm };
        _db.ScrapeJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    private Listing AddListing(int jobId, string listingId, decimal? price,
        string condition, string status)
    {
        var listing = new Listing
        {
            ListingId = listingId, Price = price, Condition = condition,
            ListingStatus = status, ScrapeJobId = jobId, Title = $"Item {listingId}"
        };
        _db.Listings.Add(listing);
        _db.SaveChanges();
        return listing;
    }

    private ListingRelationship AddRelationship(int idA, int idB,
        bool isComparable = true, double score = 0.9)
    {
        var rel = new ListingRelationship
        {
            ListingIdA = idA, ListingIdB = idB,
            IsComparable = isComparable, SimilarityScore = score,
            Explanation = "Test relationship"
        };
        _db.ListingRelationships.Add(rel);
        _db.SaveChanges();
        return rel;
    }

    // -- GetComparables tests --

    [Test]
    public async Task GetComparables_should_return_empty_when_no_relationships()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");

        var result = await _service.GetComparables(active.Id, new PredictionFilters());

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetComparables_should_return_sold_comps_bidirectionally()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");
        var soldA = AddListing(job.Id, "222", 380m, "New", "Sold");
        var soldB = AddListing(job.Id, "333", 400m, "New", "Sold");

        // A-side relationship
        AddRelationship(active.Id, soldA.Id);
        // B-side relationship
        AddRelationship(soldB.Id, active.Id);

        var result = (await _service.GetComparables(active.Id, new PredictionFilters())).ToList();

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(c => c.ListingId), Is.EquivalentTo(new[] { "222", "333" }));
    }

    [Test]
    public async Task GetComparables_should_exclude_non_comparable_relationships()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");
        var sold = AddListing(job.Id, "222", 380m, "New", "Sold");

        AddRelationship(active.Id, sold.Id, isComparable: false);

        var result = await _service.GetComparables(active.Id, new PredictionFilters());

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetComparables_should_filter_by_condition_when_enabled()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");
        var soldSame = AddListing(job.Id, "222", 380m, "New", "Sold");
        var soldDiff = AddListing(job.Id, "333", 320m, "Used", "Sold");

        AddRelationship(active.Id, soldSame.Id);
        AddRelationship(active.Id, soldDiff.Id);

        var result = (await _service.GetComparables(active.Id,
            new PredictionFilters(MatchCondition: true))).ToList();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].ListingId, Is.EqualTo("222"));
    }

    [Test]
    public async Task GetComparables_should_return_all_conditions_when_match_disabled()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");
        var soldSame = AddListing(job.Id, "222", 380m, "New", "Sold");
        var soldDiff = AddListing(job.Id, "333", 320m, "Used", "Sold");

        AddRelationship(active.Id, soldSame.Id);
        AddRelationship(active.Id, soldDiff.Id);

        var result = (await _service.GetComparables(active.Id,
            new PredictionFilters(MatchCondition: false))).ToList();

        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetComparables_should_filter_by_price_band()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 100m, "New", "Active");
        var soldInBand = AddListing(job.Id, "222", 150m, "New", "Sold");    // 100*2=200, 100/2=50 → in band
        var soldOutBand = AddListing(job.Id, "333", 250m, "New", "Sold");   // > 200 → out of band

        AddRelationship(active.Id, soldInBand.Id);
        AddRelationship(active.Id, soldOutBand.Id);

        var result = (await _service.GetComparables(active.Id,
            new PredictionFilters(PriceBand: 2.0m, MatchCondition: false))).ToList();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].ListingId, Is.EqualTo("222"));
    }

    [Test]
    public async Task GetComparables_should_skip_price_band_when_zero()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 100m, "New", "Active");
        var sold = AddListing(job.Id, "222", 500m, "New", "Sold");

        AddRelationship(active.Id, sold.Id);

        var result = (await _service.GetComparables(active.Id,
            new PredictionFilters(PriceBand: 0, MatchCondition: false))).ToList();

        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetComparables_should_only_return_sold_listings()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");
        var otherActive = AddListing(job.Id, "222", 360m, "New", "Active");
        var ended = AddListing(job.Id, "333", 370m, "New", "Ended");
        var sold = AddListing(job.Id, "444", 380m, "New", "Sold");

        AddRelationship(active.Id, otherActive.Id);
        AddRelationship(active.Id, ended.Id);
        AddRelationship(active.Id, sold.Id);

        var result = (await _service.GetComparables(active.Id,
            new PredictionFilters(MatchCondition: false))).ToList();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].ListingId, Is.EqualTo("444"));
    }
}
```

**Step 3: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~ListingPredictionService" -v n`
Expected: All 7 tests FAIL with `NotImplementedException`

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Api/Program.cs \
       AIOMarketMaker/AIOMarketMaker.Tests.Unit/Services/ListingPredictionService_UnitTests.cs
git commit -m "test: add failing GetComparables tests for ListingPredictionService"
```

---

## Task 3: Implement GetComparables

**Files:**
- Modify: `AIOMarketMaker.Core/Services/ListingPredictionService.cs`

**Step 1: Implement GetComparables using EF Core LINQ**

Replace the `GetComparables` stub with:

```csharp
public async Task<IEnumerable<ComparableSoldListing>> GetComparables(
    int listingId, PredictionFilters filters)
{
    var listing = await _db.Listings.FindAsync(listingId);
    if (listing == null)
    {
        return Enumerable.Empty<ComparableSoldListing>();
    }

    var relationships = await _db.ListingRelationships
        .Include(r => r.ListingA)
        .Include(r => r.ListingB)
        .Where(r => r.IsComparable && (r.ListingIdA == listingId || r.ListingIdB == listingId))
        .Where(r => r.ListingIdA == listingId
            ? r.ListingB.ListingStatus == "Sold"
            : r.ListingA.ListingStatus == "Sold")
        .ToListAsync();

    var comparables = relationships.Select(r =>
    {
        var comp = r.ListingIdA == listingId ? r.ListingB : r.ListingA;
        return new ComparableSoldListing(
            r.Id, comp.Id, comp.ListingId, comp.Title, comp.Description,
            comp.Price, comp.Condition, comp.Url, comp.Images,
            comp.EndDateUtc, r.SimilarityScore, r.Explanation);
    }).ToList();

    if (filters.MatchCondition && listing.Condition != null)
    {
        comparables = comparables
            .Where(c => c.Condition == listing.Condition)
            .ToList();
    }

    if (filters.PriceBand > 0 && listing.Price.HasValue && listing.Price.Value > 0)
    {
        var minPrice = listing.Price.Value / filters.PriceBand;
        var maxPrice = listing.Price.Value * filters.PriceBand;
        comparables = comparables
            .Where(c => c.Price.HasValue && c.Price.Value >= minPrice && c.Price.Value <= maxPrice)
            .ToList();
    }

    return comparables;
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~ListingPredictionService" -v n`
Expected: All 7 tests PASS

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Services/ListingPredictionService.cs
git commit -m "feat: implement GetComparables with condition and price band filtering"
```

---

## Task 4: Write Tests and Implement GetPrediction

**Files:**
- Modify: `AIOMarketMaker.Tests.Unit/Services/ListingPredictionService_UnitTests.cs`
- Modify: `AIOMarketMaker.Core/Services/ListingPredictionService.cs`

**Step 1: Write failing tests for GetPrediction**

Add to the test file:

```csharp
// -- GetPrediction tests --

[Test]
public async Task GetPrediction_should_return_null_when_no_comps()
{
    var job = AddJob();
    var active = AddListing(job.Id, "111", 350m, "New", "Active");

    var result = await _service.GetPrediction(active.Id, new PredictionFilters());

    Assert.That(result, Is.Null);
}

[Test]
public async Task GetPrediction_should_compute_count_and_avg_from_sold_comps()
{
    var job = AddJob();
    var active = AddListing(job.Id, "111", 350m, "New", "Active");
    var sold1 = AddListing(job.Id, "222", 380m, "New", "Sold");
    var sold2 = AddListing(job.Id, "333", 400m, "New", "Sold");

    AddRelationship(active.Id, sold1.Id);
    AddRelationship(active.Id, sold2.Id);

    var result = await _service.GetPrediction(active.Id,
        new PredictionFilters(MatchCondition: false));

    Assert.Multiple(() =>
    {
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SimilarSoldCount, Is.EqualTo(2));
        Assert.That(result.AverageSoldPrice, Is.EqualTo(390m));
        Assert.That(result.PotentialProfit, Is.EqualTo(40m)); // 390 - 350
    });
}

[Test]
public async Task GetPrediction_should_apply_fee_percent()
{
    var job = AddJob();
    var active = AddListing(job.Id, "111", 100m, "New", "Active");
    var sold = AddListing(job.Id, "222", 200m, "New", "Sold");

    AddRelationship(active.Id, sold.Id);

    var result = await _service.GetPrediction(active.Id,
        new PredictionFilters(FeePercent: 10m, MatchCondition: false));

    Assert.Multiple(() =>
    {
        Assert.That(result, Is.Not.Null);
        // Profit = 200 * (1 - 10/100) - 100 = 180 - 100 = 80
        Assert.That(result!.PotentialProfit, Is.EqualTo(80m));
    });
}

[Test]
public async Task GetPrediction_should_deduct_shipping_cost_with_fees()
{
    var job = AddJob();
    var listing = new Listing
    {
        ListingId = "111", Price = 100m, ShippingCost = 10m,
        Condition = "New", ListingStatus = "Active", ScrapeJobId = job.Id,
        Title = "Item 111"
    };
    _db.Listings.Add(listing);
    _db.SaveChanges();

    var sold = AddListing(job.Id, "222", 200m, "New", "Sold");
    AddRelationship(listing.Id, sold.Id);

    var result = await _service.GetPrediction(listing.Id,
        new PredictionFilters(FeePercent: 10m, MatchCondition: false));

    Assert.Multiple(() =>
    {
        Assert.That(result, Is.Not.Null);
        // Profit = 200 * (1 - 10/100) - 100 - 10 = 180 - 110 = 70
        Assert.That(result!.PotentialProfit, Is.EqualTo(70m));
    });
}

[Test]
public async Task GetPrediction_should_respect_price_band_filter()
{
    var job = AddJob();
    var active = AddListing(job.Id, "111", 100m, "New", "Active");
    var soldInBand = AddListing(job.Id, "222", 150m, "New", "Sold");    // in 2x band
    var soldOutBand = AddListing(job.Id, "333", 250m, "New", "Sold");   // out of 2x band

    AddRelationship(active.Id, soldInBand.Id);
    AddRelationship(active.Id, soldOutBand.Id);

    var result = await _service.GetPrediction(active.Id,
        new PredictionFilters(PriceBand: 2.0m, MatchCondition: false));

    Assert.Multiple(() =>
    {
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SimilarSoldCount, Is.EqualTo(1));
        Assert.That(result.AverageSoldPrice, Is.EqualTo(150m));
    });
}

[Test]
public async Task GetPrediction_should_exclude_zero_price_comps()
{
    var job = AddJob();
    var active = AddListing(job.Id, "111", 100m, "New", "Active");
    var sold1 = AddListing(job.Id, "222", 200m, "New", "Sold");
    var sold2 = AddListing(job.Id, "333", 0m, "New", "Sold");

    AddRelationship(active.Id, sold1.Id);
    AddRelationship(active.Id, sold2.Id);

    var result = await _service.GetPrediction(active.Id,
        new PredictionFilters(MatchCondition: false));

    Assert.Multiple(() =>
    {
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SimilarSoldCount, Is.EqualTo(1));
        Assert.That(result.AverageSoldPrice, Is.EqualTo(200m));
    });
}

[Test]
public async Task GetPrediction_should_be_consistent_with_GetComparables()
{
    var job = AddJob();
    var active = AddListing(job.Id, "111", 100m, "New", "Active");
    var sold1 = AddListing(job.Id, "222", 150m, "New", "Sold");
    var sold2 = AddListing(job.Id, "333", 180m, "Used", "Sold");

    AddRelationship(active.Id, sold1.Id);
    AddRelationship(active.Id, sold2.Id);

    var filters = new PredictionFilters(PriceBand: 2.0m, MatchCondition: true);

    var comps = (await _service.GetComparables(active.Id, filters)).ToList();
    var prediction = await _service.GetPrediction(active.Id, filters);

    Assert.Multiple(() =>
    {
        // Only sold1 matches (New condition + in price band)
        Assert.That(comps, Has.Count.EqualTo(1));
        Assert.That(prediction, Is.Not.Null);
        Assert.That(prediction!.SimilarSoldCount, Is.EqualTo(comps.Count));

        var expectedAvg = comps.Where(c => c.Price > 0).Average(c => c.Price!.Value);
        Assert.That(prediction.AverageSoldPrice, Is.EqualTo(expectedAvg));
    });
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~GetPrediction" -v n`
Expected: All 7 new tests FAIL with `NotImplementedException`

**Step 3: Implement GetPrediction**

Replace the `GetPrediction` stub with:

```csharp
public async Task<ListingPredictionResult?> GetPrediction(
    int listingId, PredictionFilters filters)
{
    var comps = (await GetComparables(listingId, filters)).ToList();
    var listing = await _db.Listings.FindAsync(listingId);
    if (listing == null)
    {
        return null;
    }

    var pricedComps = comps
        .Where(c => c.Price.HasValue && c.Price.Value > 0)
        .ToList();

    if (pricedComps.Count == 0)
    {
        return null;
    }

    var avgSoldPrice = pricedComps.Average(c => c.Price!.Value);
    var profit = filters.FeePercent > 0
        ? avgSoldPrice * (1.0m - filters.FeePercent / 100.0m)
            - listing.Price!.Value - (listing.ShippingCost ?? 0)
        : avgSoldPrice - listing.Price!.Value;

    int? estimatedDays = null;
    var compsWithDates = comps
        .Where(c => c.SoldDateUtc.HasValue)
        .ToList();
    if (compsWithDates.Count > 0)
    {
        // Load CreatedUtc for sold listings to compute days to sell
        var soldIds = compsWithDates.Select(c => c.SoldListingId).ToList();
        var soldListings = await _db.Listings
            .Where(l => soldIds.Contains(l.Id) && l.EndDateUtc > l.CreatedUtc)
            .Select(l => new { l.Id, l.CreatedUtc, l.EndDateUtc })
            .ToListAsync();

        if (soldListings.Count > 0)
        {
            var avgDays = soldListings
                .Average(l => (l.EndDateUtc!.Value - l.CreatedUtc).TotalDays);
            estimatedDays = (int)Math.Round(avgDays);
        }
    }

    return new ListingPredictionResult(
        listingId, pricedComps.Count, avgSoldPrice, profit, estimatedDays);
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~ListingPredictionService" -v n`
Expected: All 14 tests PASS

**Step 5: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Services/ListingPredictionService.cs \
       AIOMarketMaker/AIOMarketMaker.Tests.Unit/Services/ListingPredictionService_UnitTests.cs
git commit -m "feat: implement GetPrediction with fee, shipping, and price band support"
```

---

## Task 5: Implement GetPredictions (Paginated, for Opportunities Table)

This method uses raw SQL CTEs for performance (262K listings). It replaces `ListingEndpoints.GetActiveListings` lines 76-213.

**Files:**
- Modify: `AIOMarketMaker.Core/Services/ListingPredictionService.cs`

**Step 1: Add the BuildCte private method and SQL helpers**

Move the CTE from `ListingEndpoints.BuildFilteredPredictionsCte` (lines 215-267) into the service. Also add `ExecuteQuery` and `ExecuteScalar` helpers:

```csharp
// Inside ListingPredictionService class:

private static string BuildCte(PredictionFilters filters)
{
    var pb = filters.PriceBand.ToString(CultureInfo.InvariantCulture);
    var fee = filters.FeePercent.ToString(CultureInfo.InvariantCulture);
    var mc = filters.MinComps.ToString(CultureInfo.InvariantCulture);

    var conditionFilter = filters.MatchCondition
        ? "AND active.[Condition] = sold.[Condition]"
        : "";

    var priceBandFilter = filters.PriceBand > 0
        ? $@"AND active.Price > 0
               AND sold.Price BETWEEN active.Price / {pb} AND active.Price * {pb}"
        : "";

    var profitExpr = filters.FeePercent > 0
        ? $"AVG(sold.Price) * (1.0 - {fee} / 100.0) - active.Price - ISNULL(active.ShippingCost, 0)"
        : "AVG(sold.Price) - active.Price";

    return $@";WITH ComparableSoldNeighbors AS (
        SELECT r.ListingIdA AS ActiveListingId, r.ListingIdB AS SoldListingId
        FROM ListingRelationships r
        INNER JOIN Listings active ON active.Id = r.ListingIdA AND active.ListingStatus = 'Active'
        INNER JOIN Listings sold ON sold.Id = r.ListingIdB AND sold.ListingStatus = 'Sold'
        WHERE r.IsComparable = 1
        {conditionFilter}
        UNION ALL
        SELECT r.ListingIdB AS ActiveListingId, r.ListingIdA AS SoldListingId
        FROM ListingRelationships r
        INNER JOIN Listings active ON active.Id = r.ListingIdB AND active.ListingStatus = 'Active'
        INNER JOIN Listings sold ON sold.Id = r.ListingIdA AND sold.ListingStatus = 'Sold'
        WHERE r.IsComparable = 1
        {conditionFilter}
    ),
    FilteredPredictions AS (
        SELECT active.Id AS ListingId,
            COUNT(*) AS SimilarSoldCount,
            AVG(sold.Price) AS AverageSoldPrice,
            {profitExpr} AS PotentialProfit,
            AVG(CASE WHEN sold.EndDateUtc > sold.CreatedUtc
                     THEN DATEDIFF(day, sold.CreatedUtc, sold.EndDateUtc)
                END) AS EstimatedDaysToSell
        FROM ComparableSoldNeighbors csn
        INNER JOIN Listings sold ON sold.Id = csn.SoldListingId
        INNER JOIN Listings active ON active.Id = csn.ActiveListingId
        WHERE sold.Price > 0
        {priceBandFilter}
        GROUP BY active.Id, active.Price, active.ShippingCost
        HAVING COUNT(*) >= {mc}
            AND {profitExpr} > 0
    )";
}

private async Task<object?> ExecuteScalar(string sql)
{
    var conn = _db.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open)
    {
        await conn.OpenAsync();
    }

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return await cmd.ExecuteScalarAsync();
}

private async Task<List<T>> ExecuteQuery<T>(string sql, Func<DbDataReader, T> map)
{
    var conn = _db.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open)
    {
        await conn.OpenAsync();
    }

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await using var reader = await cmd.ExecuteReaderAsync();

    var results = new List<T>();
    while (await reader.ReadAsync())
    {
        results.Add(map(reader));
    }

    return results;
}

private bool IsSqlite()
{
    return _db.Database.GetDbConnection().GetType().Name.Contains("Sqlite");
}
```

**Step 2: Implement GetPredictions**

Replace the stub with:

```csharp
private static readonly HashSet<string> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
{
    "title", "price", "averageSoldPrice", "potentialProfit",
    "similarSoldCount", "estimatedDaysToSell", "condition",
    "createdUtc", "searchTerm"
};

public async Task<PagedPredictions> GetPredictions(
    PredictionFilters filters, IEnumerable<int>? jobIds,
    string sortBy, string sortDir, int page, int pageSize)
{
    if (IsSqlite())
    {
        return new PagedPredictions(
            Enumerable.Empty<ListingPredictionResult>(),
            Enumerable.Empty<int>(), 0, page, pageSize, 0);
    }

    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 200);
    sortDir = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
    if (!AllowedSortColumns.Contains(sortBy))
    {
        sortBy = "potentialProfit";
    }

    var jobIdList = jobIds?.ToList() ?? new List<int>();
    var cte = BuildCte(filters);
    var joinType = filters.MinComps > 0 ? "INNER JOIN" : "LEFT JOIN";

    // Count
    int totalCount;
    if (filters.MinComps > 0)
    {
        var jobFilterClauseCount = jobIdList.Count > 0
            ? $"AND l.ScrapeJobId IN ({string.Join(",", jobIdList)})"
            : "";
        var countSql = $@"
            {cte}
            SELECT COUNT(*)
            FROM Listings l
            INNER JOIN FilteredPredictions p ON p.ListingId = l.Id
            WHERE l.ListingStatus = 'Active'
            {jobFilterClauseCount}";
        totalCount = (int)(await ExecuteScalar(countSql))!;
    }
    else
    {
        var countQuery = _db.Listings.Where(l => l.ListingStatus == "Active");
        if (jobIdList.Count > 0)
        {
            countQuery = countQuery.Where(l => jobIdList.Contains(l.ScrapeJobId));
        }
        totalCount = await countQuery.CountAsync();
    }

    if (totalCount == 0)
    {
        return new PagedPredictions(
            Enumerable.Empty<ListingPredictionResult>(),
            Enumerable.Empty<int>(), 0, page, pageSize, 0);
    }

    var orderByColumn = sortBy.ToLowerInvariant() switch
    {
        "title" => "l.Title",
        "price" => "l.Price",
        "averagedsoldprice" or "averagesoldprice" => "p.AverageSoldPrice",
        "potentialprofit" => "p.PotentialProfit",
        "similarsoldcount" => "p.SimilarSoldCount",
        "estimateddaystosell" => "p.EstimatedDaysToSell",
        "condition" => "l.[Condition]",
        "createdutc" => "l.CreatedUtc",
        "searchterm" => "sj.SearchTerm",
        _ => "p.PotentialProfit"
    };

    var nullsLast = $"CASE WHEN {orderByColumn} IS NULL THEN 1 ELSE 0 END";
    var orderClause = $"{nullsLast}, {orderByColumn} {sortDir.ToUpperInvariant()}";
    var offset = (page - 1) * pageSize;

    var jobFilterClause = jobIdList.Count > 0
        ? $"AND l.ScrapeJobId IN ({string.Join(",", jobIdList)})"
        : "";

    var sql = $@"
        {cte}
        SELECT l.Id, p.AverageSoldPrice, p.SimilarSoldCount, p.PotentialProfit, p.EstimatedDaysToSell
        FROM Listings l
        {joinType} FilteredPredictions p ON p.ListingId = l.Id
        LEFT JOIN ScrapeJobs sj ON sj.Id = l.ScrapeJobId
        WHERE l.ListingStatus = 'Active'
        {jobFilterClause}
        ORDER BY {orderClause}
        OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY";

    var rows = await ExecuteQuery(sql, reader => new ListingPredictionResult(
        reader.GetInt32(0),
        reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
        reader.IsDBNull(1) ? 0 : reader.GetDecimal(1),
        reader.IsDBNull(3) ? 0 : reader.GetDecimal(3),
        reader.IsDBNull(4) ? null : reader.GetInt32(4)));

    var orderedIds = rows.Select(r => r.ListingId).ToList();
    var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

    return new PagedPredictions(rows, orderedIds, totalCount, page, pageSize, totalPages);
}
```

**Step 3: Verify it compiles**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Services/ListingPredictionService.cs
git commit -m "feat: implement GetPredictions with CTE, sorting, and pagination"
```

---

## Task 6: Implement GetAggregates (for Overview Dashboard)

This replaces `OverviewEndpoints.GetOpportunityData` (lines 112-295) + its `BuildFilteredPredictionsCte` (lines 394-439).

**Files:**
- Modify: `AIOMarketMaker.Core/Services/ListingPredictionService.cs`

**Step 1: Implement GetAggregates**

Replace the stub with:

```csharp
public async Task<PredictionAggregates> GetAggregates(PredictionFilters filters)
{
    if (IsSqlite())
    {
        return new PredictionAggregates(
            0, 0m,
            Enumerable.Empty<TopOpportunity>(),
            Enumerable.Empty<TopJobOpportunity>(),
            Enumerable.Empty<ConditionProfit>(),
            Enumerable.Empty<DaysToSell>(),
            Enumerable.Empty<PriceVsProfit>());
    }

    var conn = _db.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open)
    {
        await conn.OpenAsync();
    }

    var cte = BuildCte(filters);

    // Materialize CTE into temp table (scans 738K+ rows once instead of 3x)
    var materializeSql = $@"
        {cte}
        SELECT fp.ListingId, fp.SimilarSoldCount, fp.AverageSoldPrice,
               fp.PotentialProfit, fp.EstimatedDaysToSell
        INTO #Predictions
        FROM FilteredPredictions fp";

    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = materializeSql;
        await cmd.ExecuteNonQueryAsync();
    }

    // Aggregate totals
    int opportunities = 0;
    decimal aggregateProfit = 0m;
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"
            SELECT COUNT(*), ISNULL(SUM(PotentialProfit), 0)
            FROM #Predictions";
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            opportunities = reader.GetInt32(0);
            aggregateProfit = reader.GetDecimal(1);
        }
    }

    // Top 10 opportunities
    var topOpportunities = new List<TopOpportunity>();
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"
            SELECT TOP 10
                l.ListingId, l.Title, l.Price, l.Currency,
                p.AverageSoldPrice, p.PotentialProfit, p.SimilarSoldCount,
                l.[Condition], l.Url
            FROM #Predictions p
            INNER JOIN Listings l ON l.Id = p.ListingId
            ORDER BY p.PotentialProfit DESC";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            topOpportunities.Add(new TopOpportunity(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
    }

    // Top 10 jobs by opportunity count
    var topJobs = new List<TopJobOpportunity>();
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"
            SELECT TOP 10
                l.ScrapeJobId, sj.SearchTerm,
                COUNT(*) AS OpportunityCount,
                SUM(p.PotentialProfit) AS TotalProfit
            FROM #Predictions p
            INNER JOIN Listings l ON l.Id = p.ListingId
            LEFT JOIN ScrapeJobs sj ON sj.Id = l.ScrapeJobId
            GROUP BY l.ScrapeJobId, sj.SearchTerm
            ORDER BY COUNT(*) DESC";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            topJobs.Add(new TopJobOpportunity(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? 0m : reader.GetDecimal(3)));
        }
    }

    // Avg profit by condition
    var avgProfitByCondition = new List<ConditionProfit>();
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"
            SELECT ISNULL(l.[Condition], 'Unknown'), AVG(p.PotentialProfit), COUNT(*)
            FROM #Predictions p
            INNER JOIN Listings l ON l.Id = p.ListingId
            GROUP BY l.[Condition]
            ORDER BY AVG(p.PotentialProfit) DESC";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            avgProfitByCondition.Add(new ConditionProfit(
                reader.GetString(0),
                reader.GetDecimal(1),
                reader.GetInt32(2)));
        }
    }

    // Avg days to sell by job
    var avgDaysToSell = new List<DaysToSell>();
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"
            SELECT TOP 10
                l.ScrapeJobId, sj.SearchTerm,
                AVG(p.EstimatedDaysToSell)
            FROM #Predictions p
            INNER JOIN Listings l ON l.Id = p.ListingId
            LEFT JOIN ScrapeJobs sj ON sj.Id = l.ScrapeJobId
            WHERE p.EstimatedDaysToSell IS NOT NULL
            GROUP BY l.ScrapeJobId, sj.SearchTerm
            ORDER BY AVG(p.EstimatedDaysToSell) ASC";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            avgDaysToSell.Add(new DaysToSell(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : (decimal?)reader.GetInt32(2)));
        }
    }

    // Price vs profit scatter
    var priceVsProfit = new List<PriceVsProfit>();
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"
            SELECT TOP 100
                l.Price, p.PotentialProfit, l.[Condition]
            FROM #Predictions p
            INNER JOIN Listings l ON l.Id = p.ListingId
            WHERE l.Price IS NOT NULL
            ORDER BY p.PotentialProfit DESC";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            priceVsProfit.Add(new PriceVsProfit(
                reader.GetDecimal(0),
                reader.GetDecimal(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
    }

    // Clean up temp table
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "DROP TABLE #Predictions";
        await cmd.ExecuteNonQueryAsync();
    }

    return new PredictionAggregates(
        opportunities, aggregateProfit, topOpportunities, topJobs,
        avgProfitByCondition, avgDaysToSell, priceVsProfit);
}
```

**Step 2: Verify it compiles**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Services/ListingPredictionService.cs
git commit -m "feat: implement GetAggregates with temp table materialization"
```

---

## Task 7: Refactor ListingEndpoints to Use Service

**Files:**
- Modify: `AIOMarketMaker.Api/Endpoints/ListingEndpoints.cs`
- Modify: `AIOMarketMaker.Tests.Unit/Endpoints/ListingEndpoints_UnitTests.cs`

**Step 1: Update existing unit tests for new signatures**

The `GetListingDetail` and `DismissComparable` methods now accept `IListingPredictionService` as a parameter. Update the reflection calls:

```csharp
// Replace CallGetListingDetail:
private async Task<IResult> CallGetListingDetail(int id,
    decimal priceBand = 0, decimal feePercent = 0, bool matchCondition = true)
{
    var method = typeof(ListingEndpoints).GetMethod(
        "GetListingDetail",
        BindingFlags.NonPublic | BindingFlags.Static);

    var service = new ListingPredictionService(_db);
    var filters = new PredictionFilters(priceBand, feePercent, matchCondition);
    var resultTask = (Task<IResult>)method!.Invoke(null,
        new object[] { _db, service, id, priceBand, feePercent, matchCondition })!;
    return await resultTask;
}

// Replace CallDismissComparable:
private async Task<IResult> CallDismissComparable(int listingId, int relationshipId,
    decimal priceBand = 0, decimal feePercent = 0, bool matchCondition = true)
{
    var method = typeof(ListingEndpoints).GetMethod(
        "DismissComparable",
        BindingFlags.NonPublic | BindingFlags.Static);

    var service = new ListingPredictionService(_db);
    var resultTask = (Task<IResult>)method!.Invoke(null,
        new object[] { _db, service, listingId, relationshipId, priceBand, feePercent, matchCondition })!;
    return await resultTask;
}
```

Add the using:
```csharp
using AIOMarketMaker.Core.Services;
```

**Step 2: Refactor GetActiveListings**

Replace lines 76-213 with a thin wrapper that calls `IListingPredictionService.GetPredictions`:

```csharp
private static async Task<IResult> GetActiveListings(
    EtlDbContext db,
    IListingPredictionService predictionService,
    int page = 1,
    int pageSize = 50,
    string sortBy = "potentialProfit",
    string sortDir = "desc",
    string? jobIds = null,
    int minComps = 0,
    decimal priceBand = 0,
    decimal feePercent = 0,
    bool matchCondition = true)
{
    var filters = new PredictionFilters(priceBand, feePercent, matchCondition, minComps);
    var jobIdList = ParseJobIds(jobIds);

    var paged = await predictionService.GetPredictions(
        filters, jobIdList.Count > 0 ? jobIdList : null,
        sortBy, sortDir, page, pageSize);

    if (paged.TotalCount == 0)
    {
        return Results.Ok(new PagedResponse<OpportunityListing>(
            Enumerable.Empty<OpportunityListing>(), 0, page, pageSize, 0));
    }

    var ids = paged.OrderedListingIds.ToList();
    var predictions = paged.Items
        .Where(r => r.AverageSoldPrice > 0)
        .ToDictionary(
            r => r.ListingId,
            r => new PricingAggregate(r.AverageSoldPrice, r.SimilarSoldCount, r.EstimatedDaysToSell));

    Dictionary<int, decimal>? profitOverrides = null;
    if (feePercent > 0 || priceBand > 0)
    {
        profitOverrides = paged.Items
            .Where(r => r.PotentialProfit != 0)
            .ToDictionary(r => r.ListingId, r => r.PotentialProfit);
    }

    var listings = await db.Listings
        .Include(l => l.ScrapeJob)
        .Where(l => ids.Contains(l.Id))
        .ToListAsync();

    var idOrder = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
    listings.Sort((a, b) => idOrder.GetValueOrDefault(a.Id, int.MaxValue)
        .CompareTo(idOrder.GetValueOrDefault(b.Id, int.MaxValue)));

    var items = listings.Select(l => ToOpportunityListing(l, predictions, profitOverrides));

    return Results.Ok(new PagedResponse<OpportunityListing>(
        items, paged.TotalCount, paged.Page, paged.PageSize, paged.TotalPages));
}
```

**Step 3: Refactor GetListingDetail**

Replace lines 319-416:

```csharp
private static async Task<IResult> GetListingDetail(
    EtlDbContext db, IListingPredictionService predictionService, int id,
    decimal priceBand = 0, decimal feePercent = 0, bool matchCondition = true)
{
    var listing = await db.Listings
        .Include(l => l.ScrapeJob)
        .FirstOrDefaultAsync(l => l.Id == id);

    if (listing == null)
    {
        return Results.NotFound();
    }

    var filters = new PredictionFilters(priceBand, feePercent, matchCondition);
    var comps = await predictionService.GetComparables(id, filters);
    var prediction = await predictionService.GetPrediction(id, filters);

    var comparables = comps.Select(c => new ComparableListing(
        c.RelationshipId, c.ListingId!, c.Title,
        c.Description, c.Price, c.Condition,
        c.Url, c.Images,
        c.SoldDateUtc, c.SimilarityScore, c.Explanation));

    var detail = new ListingDetail(
        listing.Id, listing.ListingId, listing.Title, listing.Description,
        listing.Price, listing.Currency, listing.ShippingCost,
        listing.Condition, listing.Url, listing.Images,
        listing.ListingStatus, listing.ScrapeJob?.SearchTerm,
        listing.CreatedUtc,
        prediction?.AverageSoldPrice, prediction?.SimilarSoldCount ?? 0,
        prediction?.EstimatedDaysToSell, prediction?.PotentialProfit);

    return Results.Ok(new ListingDetailResponse(detail, comparables));
}
```

**Step 4: Refactor DismissComparable**

Replace lines 418-437:

```csharp
private static async Task<IResult> DismissComparable(
    EtlDbContext db, IListingPredictionService predictionService,
    int id, int relationshipId,
    decimal priceBand = 0, decimal feePercent = 0, bool matchCondition = true)
{
    var relationship = await db.ListingRelationships
        .FirstOrDefaultAsync(r =>
            r.Id == relationshipId &&
            (r.ListingIdA == id || r.ListingIdB == id));

    if (relationship == null)
    {
        return Results.NotFound();
    }

    db.ListingRelationships.Remove(relationship);
    await db.SaveChangesAsync();

    return await GetListingDetail(db, predictionService, id, priceBand, feePercent, matchCondition);
}
```

**Step 5: Remove old code from ListingEndpoints**

Delete these methods/members:
- `BuildFilteredPredictionsCte` (lines 215-267)
- `ExecuteScalar` (lines 269-280)
- `ExecuteQuery` (lines 282-302)
- `ListingIdWithPrediction` record (lines 65-67)

Add the using:
```csharp
using AIOMarketMaker.Core.Services;
```

**Step 6: Run all tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj -v n`
Expected: All tests pass (including updated ListingEndpoints tests)

**Step 7: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Api/Endpoints/ListingEndpoints.cs \
       AIOMarketMaker/AIOMarketMaker.Tests.Unit/Endpoints/ListingEndpoints_UnitTests.cs
git commit -m "refactor: replace ListingEndpoints CTE with ListingPredictionService"
```

---

## Task 8: Refactor OverviewEndpoints to Use Service

**Files:**
- Modify: `AIOMarketMaker.Api/Endpoints/OverviewEndpoints.cs`

**Step 1: Refactor GetOverview to accept IListingPredictionService**

Replace `GetOverview` (lines 52-81):

```csharp
private static async Task<IResult> GetOverview(
    EtlDbContext db,
    IListingPredictionService predictionService,
    int minComps = 3,
    decimal feePercent = 13.25m,
    bool matchCondition = true)
{
    var filters = new PredictionFilters(FeePercent: feePercent, MatchCondition: matchCondition, MinComps: minComps);
    var statusCounts = await GetStatusCounts(db);
    var agg = await predictionService.GetAggregates(filters);
    var lastScrape = await GetLastScrape(db);
    var cumulativeGrowth = await GetCumulativeGrowth(db);
    var recentRuns = await GetRecentRuns(db);

    var response = new OverviewResponse(
        TotalListings: statusCounts.Total,
        ActiveListings: statusCounts.Active,
        SoldListings: statusCounts.Sold,
        EndedListings: statusCounts.Ended,
        Opportunities: agg.Opportunities,
        AggregateProfit: agg.AggregateProfit,
        LastScrape: lastScrape,
        CumulativeGrowth: cumulativeGrowth,
        TopJobsByOpportunities: agg.TopJobsByOpportunities
            .Select(j => new TopJobOpportunityEntry(j.JobId, j.SearchTerm, j.OpportunityCount, j.TotalProfit)),
        AvgProfitByCondition: agg.AvgProfitByCondition
            .Select(c => new ConditionProfitEntry(c.Condition, c.AvgProfit, c.Count)),
        AvgDaysToSellByJob: agg.AvgDaysToSellByJob
            .Select(d => new DaysToSellEntry(d.JobId, d.SearchTerm, d.AvgDaysToSell)),
        PriceVsProfitPoints: agg.PriceVsProfitPoints
            .Select(p => new PriceVsProfitEntry(p.Price, p.PotentialProfit, p.Condition)),
        TopOpportunities: agg.TopOpportunities
            .Select(o => new TopOpportunityResponse(
                o.ListingId, o.Title, o.Price, o.Currency,
                o.AverageSoldPrice, o.PotentialProfit, o.SimilarSoldCount,
                o.Condition, o.Url)),
        RecentRuns: recentRuns);

    return Results.Ok(response);
}
```

**Step 2: Remove old code from OverviewEndpoints**

Delete these methods:
- `GetOpportunityData` (lines 112-295) — replaced by `predictionService.GetAggregates`
- `OpportunityData` record (lines 104-110) — no longer needed
- `BuildFilteredPredictionsCte` (lines 394-439) — duplicate, now in service
- `ExecuteQuery` (lines 441-461) — duplicate, now in service

Add the using:
```csharp
using AIOMarketMaker.Core.Services;
```

**Step 3: Verify it compiles**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Api/AIOMarketMaker.Api.csproj`
Expected: Build succeeded

**Step 4: Run all unit tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj -v n`
Expected: All tests pass

**Step 5: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Api/Endpoints/OverviewEndpoints.cs
git commit -m "refactor: replace OverviewEndpoints CTE with ListingPredictionService"
```

---

## Task 9: Drop vw_ListingPredictions View and Remove EF Model

**Files:**
- Create: `AIOMarketMaker.Core/Data/Migrations/SqlServer/041_DropListingPredictionsView.sql`
- Delete: `AIOMarketMaker.Core/Data/Models/ListingPrediction.cs`
- Modify: `AIOMarketMaker.Core/Data/EtlDbContext.cs` — remove DbSet and view mapping

**Step 1: Create the migration**

```sql
-- Migration: 041_DropListingPredictionsView
-- Description: Drop vw_ListingPredictions view — replaced by ListingPredictionService CTE queries
-- Date: 2026-02-21

IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_ListingPredictions')
BEGIN
    DROP VIEW vw_ListingPredictions;
END
```

**Step 2: Remove the ListingPrediction model**

Delete file: `AIOMarketMaker.Core/Data/Models/ListingPrediction.cs`

**Step 3: Remove DbSet and view mapping from EtlDbContext**

In `EtlDbContext.cs`:

- Delete line 28: `public DbSet<ListingPrediction> ListingPredictions { get; set; } = null!;`
- Delete lines 176-183 (the `ListingPrediction` entity mapping block)

**Step 4: Fix the Etl/Program.cs reference**

In `AIOMarketMaker.Etl/Program.cs`, change line 274 from:
```
Console.WriteLine("Predictions are computed live via vw_ListingPredictions view.");
```
to:
```
Console.WriteLine("Predictions are computed live via ListingPredictionService.");
```

**Step 5: Fix Functions ScrapeJobsApi.cs (if it still compiles)**

The Functions project references `_dbContext.ListingPredictions` at `ScrapeJobsApi.cs:393`. Since the Functions project is being replaced by the API, this needs a minimal fix to keep it compiling. Replace the `ListingPredictions` query (lines 392-395) with an empty dictionary:

```csharp
// ListingPrediction view removed — predictions are computed by ListingPredictionService in API
var predictions = new Dictionary<int, PricingAggregate>();
```

**Step 6: Build the Core project to embed the migration**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

**Step 7: Build the entire solution**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`
Expected: Build succeeded (no references to `ListingPrediction` remain)

**Step 8: Run all tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.sln -v n`
Expected: All tests pass

**Step 9: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Data/Migrations/SqlServer/041_DropListingPredictionsView.sql \
       AIOMarketMaker/AIOMarketMaker.Core/Data/EtlDbContext.cs \
       AIOMarketMaker/AIOMarketMaker.Etl/Program.cs \
       AIOMarketMaker/AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs
git rm AIOMarketMaker/AIOMarketMaker.Core/Data/Models/ListingPrediction.cs
git commit -m "chore: drop vw_ListingPredictions view and remove ListingPrediction model"
```

---

## Task 10: End-to-End Smoke Test Against LocalDB

**Files:** None (manual verification)

**Step 1: Restart the local environment**

Run: `/setup-local-env restart`

**Step 2: Verify the API is running new code**

Check that the DLL was rebuilt after our changes:
```bash
powershell -Command "(Get-Item '<REPO_ROOT>/AIOMarketMaker/AIOMarketMaker.Api/bin/Debug/net8.0/AIOMarketMaker.Api.dll').LastWriteTime"
```

**Step 3: Test listing detail endpoint**

```bash
curl -s "http://localhost:5000/api/listings/83156?priceBand=2&feePercent=13.25&matchCondition=true" | python -m json.tool | head -20
```

Verify: `SimilarSoldCount` matches the value shown in the opportunities table for the same listing.

**Step 4: Test opportunities table endpoint**

```bash
curl -s "http://localhost:5000/api/listings/active?page=1&pageSize=5&priceBand=2&feePercent=13.25&matchCondition=true&minComps=3" | python -m json.tool | head -30
```

Find listing 83156 in the results and confirm `SimilarSoldCount` matches the detail view.

**Step 5: Test overview endpoint**

```bash
curl -s "http://localhost:5000/api/overview?minComps=3&feePercent=13.25&matchCondition=true" | python -m json.tool | head -20
```

Verify: returns valid aggregates (non-zero Opportunities count).

**Step 6: Verify in the Electron UI**

Open the desktop app. Navigate to an opportunity and check that the comps count in the table matches the comps count in the detail view.

**Step 7: Commit a verification note (optional)**

No code changes — verification is manual.
