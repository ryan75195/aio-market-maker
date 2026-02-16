# Overview Dashboard Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add an Overview dashboard tab to the Electron desktop UI that shows KPI cards, Chart.js charts (cumulative growth, listings by job, profit distribution), top opportunities table, and recent scrape runs.

**Architecture:** New `GET /api/overview` endpoint returns all dashboard data in one response. Frontend adds Chart.js via CDN and renders the Overview as the first/default tab. The API endpoint uses raw SQL for aggregations (same pattern as `ListingEndpoints`), and the frontend creates Chart.js instances on data load.

**Tech Stack:** ASP.NET Core minimal API, EF Core + raw SQL, Chart.js 4.x (CDN), Vue 3

---

### Task 1: Create the Overview API Endpoint (records + route registration)

**Files:**
- Create: `AIOMarketMaker/AIOMarketMaker.Api/Endpoints/OverviewEndpoints.cs`
- Modify: `AIOMarketMaker/AIOMarketMaker.Api/Program.cs:158-162`

**Step 1: Create `OverviewEndpoints.cs` with response records and empty handler**

```csharp
using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Api.Endpoints;

public record OverviewResponse(
    int TotalListings,
    int ActiveListings,
    int SoldListings,
    int EndedListings,
    int Opportunities,
    decimal AggregateProfit,
    LastScrapeResponse? LastScrape,
    IEnumerable<CumulativeGrowthEntry> CumulativeGrowth,
    IEnumerable<ListingsByJobEntry> ListingsByJob,
    ProfitDistributionResponse ProfitDistribution,
    IEnumerable<TopOpportunityResponse> TopOpportunities,
    IEnumerable<RecentRunResponse> RecentRuns);

public record LastScrapeResponse(
    DateTime StartedUtc, string? Status, string? JobSearchTerm,
    int ListingsAddedActive, int ListingsAddedSold);

public record CumulativeGrowthEntry(string Date, int Cumulative);

public record ListingsByJobEntry(int JobId, string SearchTerm, int Count);

public record ProfitDistributionResponse(
    int Range0to25, int Range25to50, int Range50to100, int Range100plus);

public record TopOpportunityResponse(
    string ListingId, string? Title, decimal? Price, string? Currency,
    decimal? AverageSoldPrice, decimal? PotentialProfit,
    int SimilarSoldCount, string? Condition, string? Url);

public record RecentRunResponse(
    int Id, DateTime? StartedUtc, string? JobSearchTerm,
    string? Status, int ListingsAddedActive, int ListingsAddedSold,
    int ListingsFailed);

public static class OverviewEndpoints
{
    public static void MapOverviewEndpoints(this WebApplication app)
    {
        app.MapGet("/api/overview", GetOverview);
    }

    private static async Task<IResult> GetOverview(
        EtlDbContext db,
        int minComps = 3,
        decimal feePercent = 13.25m,
        bool matchCondition = true)
    {
        // Step 1: Listing counts by status
        var statusCounts = await db.Listings
            .GroupBy(l => l.ListingStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        var totalListings = statusCounts.Sum(s => s.Count);
        var activeListings = statusCounts.Where(s => s.Status == "Active").Sum(s => s.Count);
        var soldListings = statusCounts.Where(s => s.Status == "Sold").Sum(s => s.Count);
        var endedListings = statusCounts.Where(s => s.Status == "Ended" || s.Status == "OutOfStock").Sum(s => s.Count);

        // Step 2: Opportunities + aggregate profit using CTE
        var (opportunities, aggregateProfit, topOpportunities) = await GetOpportunityStats(
            db, minComps, feePercent, matchCondition);

        // Step 3: Last scrape run
        var lastRun = await db.ScrapeRuns
            .OrderByDescending(r => r.StartedUtc)
            .Select(r => new LastScrapeResponse(
                r.StartedUtc,
                r.Status,
                r.JobId != null
                    ? db.ScrapeJobs.Where(j => j.Id == r.JobId).Select(j => j.SearchTerm).FirstOrDefault()
                    : null,
                r.ListingsAddedActive,
                r.ListingsAddedSold))
            .FirstOrDefaultAsync();

        // Step 4: Cumulative growth by date
        var cumulativeGrowth = await GetCumulativeGrowth(db);

        // Step 5: Listings by job
        var listingsByJob = await db.Listings
            .Where(l => l.ScrapeJobId != null)
            .GroupBy(l => new { l.ScrapeJobId, l.ScrapeJob!.SearchTerm })
            .Select(g => new ListingsByJobEntry(g.Key.ScrapeJobId!.Value, g.Key.SearchTerm, g.Count()))
            .OrderByDescending(e => e.Count)
            .ToListAsync();

        // Step 6: Profit distribution
        var profitDistribution = await GetProfitDistribution(db, minComps, feePercent, matchCondition);

        // Step 7: Recent runs (last 5)
        var recentRuns = await db.ScrapeRuns
            .OrderByDescending(r => r.StartedUtc)
            .Take(5)
            .Select(r => new RecentRunResponse(
                r.Id,
                r.StartedUtc,
                r.JobId != null
                    ? db.ScrapeJobs.Where(j => j.Id == r.JobId).Select(j => j.SearchTerm).FirstOrDefault()
                    : null,
                r.Status,
                r.ListingsAddedActive,
                r.ListingsAddedSold,
                r.ListingsFailed))
            .ToListAsync();

        return Results.Ok(new OverviewResponse(
            totalListings, activeListings, soldListings, endedListings,
            opportunities, aggregateProfit, lastRun,
            cumulativeGrowth, listingsByJob, profitDistribution,
            topOpportunities, recentRuns));
    }

    private static async Task<IEnumerable<CumulativeGrowthEntry>> GetCumulativeGrowth(EtlDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        // SQLite uses DATE(), SQL Server uses CAST(... AS DATE).
        // Detect provider to use correct syntax.
        var isSqlite = conn.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);

        string sql;
        if (isSqlite)
        {
            sql = @"
                SELECT DATE(CreatedUtc) AS IndexDate, COUNT(*) AS DailyCount
                FROM Listings
                GROUP BY DATE(CreatedUtc)
                ORDER BY IndexDate ASC";
        }
        else
        {
            sql = @"
                SELECT CAST(CreatedUtc AS DATE) AS IndexDate, COUNT(*) AS DailyCount
                FROM Listings
                GROUP BY CAST(CreatedUtc AS DATE)
                ORDER BY IndexDate ASC";
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();

        var entries = new List<CumulativeGrowthEntry>();
        var cumulative = 0;
        while (await reader.ReadAsync())
        {
            var date = reader.GetString(0);
            var dailyCount = reader.GetInt32(1);
            cumulative += dailyCount;
            entries.Add(new CumulativeGrowthEntry(date, cumulative));
        }

        return entries;
    }

    private static async Task<(int Count, decimal Profit, IEnumerable<TopOpportunityResponse> Top)> GetOpportunityStats(
        EtlDbContext db, int minComps, decimal feePercent, bool matchCondition)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        // Check if SQLite (for tests) - return empty results since CTE uses SQL Server syntax
        if (conn.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return (0, 0m, Enumerable.Empty<TopOpportunityResponse>());
        }

        var fee = feePercent.ToString(CultureInfo.InvariantCulture);
        var mc = minComps.ToString(CultureInfo.InvariantCulture);

        var conditionFilter = matchCondition
            ? "AND active.[Condition] = sold.[Condition]"
            : "";

        var profitExpr = feePercent > 0
            ? $"AVG(sp.SoldPrice) * (1.0 - {fee} / 100.0) - active.Price - ISNULL(active.ShippingCost, 0)"
            : "AVG(sp.SoldPrice) - active.Price";

        var cte = $@";WITH ComparableSoldNeighbors AS (
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
        SoldPrices AS (
            SELECT csn.ActiveListingId, csn.SoldListingId,
                COALESCE(
                    (SELECT TOP 1 h.Price FROM ListingStatusHistory h
                     WHERE h.ListingId = csn.SoldListingId AND h.ListingStatus = 'Sold'
                     ORDER BY h.RecordedUtc DESC),
                    sold.Price
                ) AS SoldPrice
            FROM ComparableSoldNeighbors csn
            INNER JOIN Listings sold ON sold.Id = csn.SoldListingId
        ),
        FilteredPredictions AS (
            SELECT active.Id AS ListingId,
                COUNT(*) AS SimilarSoldCount,
                AVG(sp.SoldPrice) AS AverageSoldPrice,
                {profitExpr} AS PotentialProfit
            FROM SoldPrices sp
            INNER JOIN Listings active ON active.Id = sp.ActiveListingId
            WHERE sp.SoldPrice > 0
            GROUP BY active.Id, active.Price, active.ShippingCost
            HAVING COUNT(*) >= {mc}
                AND {profitExpr} > 0
        )";

        // Count + aggregate profit
        var countSql = $@"{cte}
            SELECT COUNT(*) AS Cnt, ISNULL(SUM(PotentialProfit), 0) AS TotalProfit
            FROM FilteredPredictions";

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = countSql;
        await using var countReader = await countCmd.ExecuteReaderAsync();

        var count = 0;
        var profit = 0m;
        if (await countReader.ReadAsync())
        {
            count = countReader.GetInt32(0);
            profit = countReader.GetDecimal(1);
        }
        await countReader.CloseAsync();

        // Top 10 opportunities
        var topSql = $@"{cte}
            SELECT l.ListingId, l.Title, l.Price, l.Currency,
                   p.AverageSoldPrice, p.PotentialProfit, p.SimilarSoldCount,
                   l.[Condition], l.Url
            FROM FilteredPredictions p
            INNER JOIN Listings l ON l.Id = p.ListingId
            ORDER BY p.PotentialProfit DESC
            OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY";

        await using var topCmd = conn.CreateCommand();
        topCmd.CommandText = topSql;
        await using var topReader = await topCmd.ExecuteReaderAsync();

        var topOpps = new List<TopOpportunityResponse>();
        while (await topReader.ReadAsync())
        {
            topOpps.Add(new TopOpportunityResponse(
                topReader.GetString(0),
                topReader.IsDBNull(1) ? null : topReader.GetString(1),
                topReader.IsDBNull(2) ? null : topReader.GetDecimal(2),
                topReader.IsDBNull(3) ? null : topReader.GetString(3),
                topReader.IsDBNull(4) ? null : topReader.GetDecimal(4),
                topReader.IsDBNull(5) ? null : topReader.GetDecimal(5),
                topReader.GetInt32(6),
                topReader.IsDBNull(7) ? null : topReader.GetString(7),
                topReader.IsDBNull(8) ? null : topReader.GetString(8)));
        }

        return (count, profit, topOpps);
    }

    private static async Task<ProfitDistributionResponse> GetProfitDistribution(
        EtlDbContext db, int minComps, decimal feePercent, bool matchCondition)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        if (conn.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return new ProfitDistributionResponse(0, 0, 0, 0);
        }

        var fee = feePercent.ToString(CultureInfo.InvariantCulture);
        var mc = minComps.ToString(CultureInfo.InvariantCulture);

        var conditionFilter = matchCondition
            ? "AND active.[Condition] = sold.[Condition]"
            : "";

        var profitExpr = feePercent > 0
            ? $"AVG(sp.SoldPrice) * (1.0 - {fee} / 100.0) - active.Price - ISNULL(active.ShippingCost, 0)"
            : "AVG(sp.SoldPrice) - active.Price";

        var sql = $@";WITH ComparableSoldNeighbors AS (
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
        SoldPrices AS (
            SELECT csn.ActiveListingId, csn.SoldListingId,
                COALESCE(
                    (SELECT TOP 1 h.Price FROM ListingStatusHistory h
                     WHERE h.ListingId = csn.SoldListingId AND h.ListingStatus = 'Sold'
                     ORDER BY h.RecordedUtc DESC),
                    sold.Price
                ) AS SoldPrice
            FROM ComparableSoldNeighbors csn
            INNER JOIN Listings sold ON sold.Id = csn.SoldListingId
        ),
        FilteredPredictions AS (
            SELECT active.Id AS ListingId,
                {profitExpr} AS PotentialProfit
            FROM SoldPrices sp
            INNER JOIN Listings active ON active.Id = sp.ActiveListingId
            WHERE sp.SoldPrice > 0
            GROUP BY active.Id, active.Price, active.ShippingCost
            HAVING COUNT(*) >= {mc}
                AND {profitExpr} > 0
        )
        SELECT
            SUM(CASE WHEN PotentialProfit < 25 THEN 1 ELSE 0 END),
            SUM(CASE WHEN PotentialProfit >= 25 AND PotentialProfit < 50 THEN 1 ELSE 0 END),
            SUM(CASE WHEN PotentialProfit >= 50 AND PotentialProfit < 100 THEN 1 ELSE 0 END),
            SUM(CASE WHEN PotentialProfit >= 100 THEN 1 ELSE 0 END)
        FROM FilteredPredictions";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();

        if (await reader.ReadAsync())
        {
            return new ProfitDistributionResponse(
                reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt32(3));
        }

        return new ProfitDistributionResponse(0, 0, 0, 0);
    }
}
```

**Step 2: Register the endpoint in `Program.cs`**

In `AIOMarketMaker/AIOMarketMaker.Api/Program.cs`, add after line 161 (`app.MapScrapeEndpoints();`):

```csharp
app.MapOverviewEndpoints();
```

**Step 3: Build to verify it compiles**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Api/AIOMarketMaker.Api.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Api/Endpoints/OverviewEndpoints.cs AIOMarketMaker/AIOMarketMaker.Api/Program.cs
git commit -m "feat: add GET /api/overview endpoint for dashboard data"
```

---

### Task 2: Write unit tests for Overview endpoint

**Files:**
- Create: `AIOMarketMaker/AIOMarketMaker.Tests/UnitTests/Endpoints/OverviewEndpoints_UnitTests.cs`

The CTE-based queries (opportunities, profit distribution) use SQL Server syntax and won't work in SQLite, so those return empty results in tests. The tests focus on what **can** be tested with SQLite: listing counts, cumulative growth, listings by job, last scrape, recent runs.

**Step 1: Write the test file**

```csharp
using AIOMarketMaker.Api.Endpoints;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Tests.Utils;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AIOMarketMaker.Tests.UnitTests.Endpoints;

[TestFixture]
[Category("Unit")]
public class OverviewEndpoints_UnitTests
{
    private EtlDbContext _db = null!;

    [SetUp]
    public void SetUp()
    {
        _db = InMemoryDbContextFactory.Create();
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    [Test]
    public async Task Should_return_correct_listing_counts_by_status()
    {
        _db.ScrapeJobs.Add(new ScrapeJob { SearchTerm = "test" });
        await _db.SaveChangesAsync();

        _db.Listings.AddRange(
            new Listing { ListingId = "1", ListingStatus = "Active", CreatedUtc = DateTime.UtcNow, ScrapeJobId = 1 },
            new Listing { ListingId = "2", ListingStatus = "Active", CreatedUtc = DateTime.UtcNow, ScrapeJobId = 1 },
            new Listing { ListingId = "3", ListingStatus = "Sold", CreatedUtc = DateTime.UtcNow, ScrapeJobId = 1 },
            new Listing { ListingId = "4", ListingStatus = "Ended", CreatedUtc = DateTime.UtcNow, ScrapeJobId = 1 });
        await _db.SaveChangesAsync();

        var result = await CallOverview();

        Assert.Multiple(() =>
        {
            Assert.That(result.TotalListings, Is.EqualTo(4));
            Assert.That(result.ActiveListings, Is.EqualTo(2));
            Assert.That(result.SoldListings, Is.EqualTo(1));
            Assert.That(result.EndedListings, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Should_return_empty_overview_when_no_data()
    {
        var result = await CallOverview();

        Assert.Multiple(() =>
        {
            Assert.That(result.TotalListings, Is.EqualTo(0));
            Assert.That(result.ActiveListings, Is.EqualTo(0));
            Assert.That(result.Opportunities, Is.EqualTo(0));
            Assert.That(result.AggregateProfit, Is.EqualTo(0));
            Assert.That(result.LastScrape, Is.Null);
            Assert.That(result.CumulativeGrowth, Is.Empty);
            Assert.That(result.ListingsByJob, Is.Empty);
            Assert.That(result.TopOpportunities, Is.Empty);
            Assert.That(result.RecentRuns, Is.Empty);
        });
    }

    [Test]
    public async Task Should_return_cumulative_growth_by_date()
    {
        _db.ScrapeJobs.Add(new ScrapeJob { SearchTerm = "test" });
        await _db.SaveChangesAsync();

        _db.Listings.AddRange(
            new Listing { ListingId = "1", CreatedUtc = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc), ScrapeJobId = 1 },
            new Listing { ListingId = "2", CreatedUtc = new DateTime(2026, 1, 10, 14, 0, 0, DateTimeKind.Utc), ScrapeJobId = 1 },
            new Listing { ListingId = "3", CreatedUtc = new DateTime(2026, 1, 11, 10, 0, 0, DateTimeKind.Utc), ScrapeJobId = 1 });
        await _db.SaveChangesAsync();

        var result = await CallOverview();
        var growth = result.CumulativeGrowth.ToList();

        Assert.Multiple(() =>
        {
            Assert.That(growth, Has.Count.EqualTo(2));
            Assert.That(growth[0].Cumulative, Is.EqualTo(2), "Day 1: 2 listings");
            Assert.That(growth[1].Cumulative, Is.EqualTo(3), "Day 2: cumulative 3");
        });
    }

    [Test]
    public async Task Should_return_listings_grouped_by_job()
    {
        _db.ScrapeJobs.AddRange(
            new ScrapeJob { SearchTerm = "PS5" },
            new ScrapeJob { SearchTerm = "Xbox" });
        await _db.SaveChangesAsync();

        _db.Listings.AddRange(
            new Listing { ListingId = "1", CreatedUtc = DateTime.UtcNow, ScrapeJobId = 1 },
            new Listing { ListingId = "2", CreatedUtc = DateTime.UtcNow, ScrapeJobId = 1 },
            new Listing { ListingId = "3", CreatedUtc = DateTime.UtcNow, ScrapeJobId = 2 });
        await _db.SaveChangesAsync();

        var result = await CallOverview();
        var byJob = result.ListingsByJob.ToList();

        Assert.Multiple(() =>
        {
            Assert.That(byJob, Has.Count.EqualTo(2));
            Assert.That(byJob[0].SearchTerm, Is.EqualTo("PS5"), "PS5 first (more listings)");
            Assert.That(byJob[0].Count, Is.EqualTo(2));
            Assert.That(byJob[1].SearchTerm, Is.EqualTo("Xbox"));
            Assert.That(byJob[1].Count, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Should_return_last_scrape_run()
    {
        _db.ScrapeJobs.Add(new ScrapeJob { SearchTerm = "PS5" });
        await _db.SaveChangesAsync();

        _db.ScrapeRuns.AddRange(
            new ScrapeRun
            {
                StartedUtc = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc),
                Status = "Completed", JobId = 1,
                ListingsAddedActive = 10, ListingsAddedSold = 5
            },
            new ScrapeRun
            {
                StartedUtc = new DateTime(2026, 1, 11, 12, 0, 0, DateTimeKind.Utc),
                Status = "Completed", JobId = 1,
                ListingsAddedActive = 20, ListingsAddedSold = 8
            });
        await _db.SaveChangesAsync();

        var result = await CallOverview();

        Assert.Multiple(() =>
        {
            Assert.That(result.LastScrape, Is.Not.Null);
            Assert.That(result.LastScrape!.ListingsAddedActive, Is.EqualTo(20), "Should be the latest run");
            Assert.That(result.LastScrape.JobSearchTerm, Is.EqualTo("PS5"));
        });
    }

    [Test]
    public async Task Should_return_at_most_5_recent_runs()
    {
        for (var i = 0; i < 8; i++)
        {
            _db.ScrapeRuns.Add(new ScrapeRun
            {
                StartedUtc = DateTime.UtcNow.AddHours(-i),
                Status = "Completed"
            });
        }
        await _db.SaveChangesAsync();

        var result = await CallOverview();

        Assert.That(result.RecentRuns.Count(), Is.EqualTo(5));
    }

    private async Task<OverviewResponse> CallOverview()
    {
        // Use reflection to call the private static method, or make it internal+InternalsVisibleTo.
        // Simpler: call through the IResult pattern used by minimal APIs.
        var method = typeof(OverviewEndpoints).GetMethod("GetOverview",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var resultTask = (Task<IResult>)method!.Invoke(null, new object[] { _db, 3, 13.25m, true })!;
        var result = await resultTask;

        var okResult = (Ok<OverviewResponse>)result;
        return okResult.Value!;
    }
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~OverviewEndpoints_UnitTests"`
Expected: All 5 tests pass

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/UnitTests/Endpoints/OverviewEndpoints_UnitTests.cs
git commit -m "test: add unit tests for overview endpoint"
```

---

### Task 3: Add Chart.js CDN and Overview HTML template

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/index.html`

**Step 1: Add Chart.js script tag**

In `index.html`, add after line 8 (`<script src="https://unpkg.com/vue@3/dist/vue.global.js"></script>`):

```html
  <script src="https://unpkg.com/chart.js@4/dist/chart.umd.js"></script>
```

**Step 2: Add Overview as the first nav button**

Replace line 17 (the Jobs nav button) with Overview first:

```html
          <button :class="{ active: currentView === 'overview' }" @click="currentView = 'overview'; loadOverview()">Overview</button>
          <button :class="{ active: currentView === 'jobs' }" @click="currentView = 'jobs'">Jobs</button>
```

**Step 3: Add Overview view HTML**

After line 34 (`<!-- Toast notifications -->` section ends at line 33), insert the Overview view before the Jobs view. Add after the closing `</div>` of the toast (line 33), before the Jobs View comment (line 35):

```html
        <!-- Overview View -->
        <div v-if="currentView === 'overview'" class="view">
          <div class="view-header">
            <h1>Overview</h1>
            <button class="btn" @click="loadOverview">Refresh</button>
          </div>

          <!-- KPI Cards -->
          <div class="kpi-grid">
            <div class="kpi-card">
              <div class="kpi-value">{{ overviewData.totalListings.toLocaleString() }}</div>
              <div class="kpi-label">Total Listings</div>
              <div class="kpi-detail">{{ overviewData.activeListings }} active / {{ overviewData.soldListings }} sold</div>
            </div>
            <div class="kpi-card">
              <div class="kpi-value">{{ overviewData.activeListings.toLocaleString() }}</div>
              <div class="kpi-label">Active Listings</div>
            </div>
            <div class="kpi-card">
              <div class="kpi-value">{{ overviewData.opportunities.toLocaleString() }}</div>
              <div class="kpi-label">Opportunities</div>
              <div class="kpi-detail">&ge;{{ settings.opportunities.minComps }} comps</div>
            </div>
            <div class="kpi-card">
              <div class="kpi-value profit">{{ formatPrice(overviewData.aggregateProfit, 'GBP') }}</div>
              <div class="kpi-label">Total Potential Profit</div>
            </div>
            <div class="kpi-card">
              <div v-if="overviewData.lastScrape" class="kpi-value small">{{ timeAgo(overviewData.lastScrape.startedUtc) }}</div>
              <div v-else class="kpi-value small">Never</div>
              <div class="kpi-label">Last Scrape</div>
              <div v-if="overviewData.lastScrape" class="kpi-detail">
                <span class="status-badge" :class="overviewData.lastScrape.status?.toLowerCase()">{{ overviewData.lastScrape.status }}</span>
              </div>
            </div>
          </div>

          <!-- Cumulative Growth Chart -->
          <div class="chart-section">
            <h3>Cumulative Listings Over Time</h3>
            <div class="chart-container">
              <canvas id="cumulativeGrowthChart"></canvas>
            </div>
          </div>

          <!-- Two-column charts -->
          <div class="chart-row">
            <div class="chart-section half">
              <h3>Listings by Job</h3>
              <div class="chart-container">
                <canvas id="listingsByJobChart"></canvas>
              </div>
            </div>
            <div class="chart-section half">
              <h3>Profit Distribution</h3>
              <div class="chart-container">
                <canvas id="profitDistributionChart"></canvas>
              </div>
            </div>
          </div>

          <!-- Top Opportunities Table -->
          <div class="overview-section">
            <h3>Top 10 Opportunities</h3>
            <table class="data-table">
              <thead>
                <tr>
                  <th>Title</th>
                  <th>Price</th>
                  <th>Avg Sold</th>
                  <th>Profit</th>
                  <th>Comps</th>
                  <th>Condition</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="opp in overviewData.topOpportunities" :key="opp.listingId">
                  <td class="title-cell" :title="opp.title">{{ truncate(opp.title, 45) }}</td>
                  <td class="price-cell">{{ formatPrice(opp.price, opp.currency) }}</td>
                  <td class="price-cell">{{ formatPrice(opp.averageSoldPrice, opp.currency) }}</td>
                  <td class="price-cell" style="color: #22c55e;">+{{ formatPrice(opp.potentialProfit, opp.currency) }}</td>
                  <td>{{ opp.similarSoldCount }}</td>
                  <td>{{ opp.condition || '-' }}</td>
                  <td><a v-if="opp.url" :href="opp.url" target="_blank" class="btn small">View</a></td>
                </tr>
                <tr v-if="overviewData.topOpportunities.length === 0">
                  <td colspan="7" class="empty">No opportunities found</td>
                </tr>
              </tbody>
            </table>
          </div>

          <!-- Recent Runs Table -->
          <div class="overview-section">
            <h3>Recent Scrape Runs</h3>
            <table class="data-table">
              <thead>
                <tr>
                  <th>Time</th>
                  <th>Job</th>
                  <th>Status</th>
                  <th>+Active</th>
                  <th>+Sold</th>
                  <th>Failed</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="run in overviewData.recentRuns" :key="run.id">
                  <td>{{ timeAgo(run.startedUtc) }}</td>
                  <td>{{ run.jobSearchTerm || 'All Jobs' }}</td>
                  <td><span class="status-badge" :class="run.status?.toLowerCase()">{{ run.status }}</span></td>
                  <td class="number">{{ run.listingsAddedActive }}</td>
                  <td class="number">{{ run.listingsAddedSold }}</td>
                  <td class="number" :class="{ 'failed-count': run.listingsFailed > 0 }">{{ run.listingsFailed }}</td>
                </tr>
                <tr v-if="overviewData.recentRuns.length === 0">
                  <td colspan="6" class="empty">No scrape runs yet</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
```

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/index.html
git commit -m "feat: add Overview tab HTML template with Chart.js CDN"
```

---

### Task 4: Add Vue data, methods, and Chart.js rendering

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/app.js`

**Step 1: Change default view to overview**

In `app.js` line 6, change:
```javascript
      currentView: 'jobs',
```
to:
```javascript
      currentView: 'overview',
```

**Step 2: Add overview data properties**

After `showJobDropdown: false` (line 69), add:
```javascript
      ,
      overviewData: {
        totalListings: 0,
        activeListings: 0,
        soldListings: 0,
        endedListings: 0,
        opportunities: 0,
        aggregateProfit: 0,
        lastScrape: null,
        cumulativeGrowth: [],
        listingsByJob: [],
        profitDistribution: { range0to25: 0, range25to50: 0, range50to100: 0, range100plus: 0 },
        topOpportunities: [],
        recentRuns: []
      },
      overviewCharts: {}
```

**Step 3: Load overview on mount**

In `mounted()` (line 179-185), change:
```javascript
  async mounted() {
    await this.loadConfig();
    if (!this.configError) {
      await this.loadJobs();
    }
    this.startAutoRefresh();
  },
```
to:
```javascript
  async mounted() {
    await this.loadConfig();
    if (!this.configError) {
      await this.loadJobs();
      await this.loadOverview();
    }
    this.startAutoRefresh();
  },
```

**Step 4: Add `loadOverview()` and chart rendering methods**

Add these methods in the `methods: {` section, after the `loadConfig()` method (after line 207):

```javascript
    async loadOverview() {
      try {
        const opp = this.settings?.opportunities || {};
        const params = new URLSearchParams();
        if (opp.minComps > 0) {
          params.set('minComps', opp.minComps);
        }
        if (opp.feePercent > 0) {
          params.set('feePercent', opp.feePercent);
        }
        if (opp.matchCondition) {
          params.set('matchCondition', 'true');
        }
        const data = await this.apiCall(`/overview?${params.toString()}`);
        this.overviewData = this.toCamelCase(data);
        this.$nextTick(() => this.renderCharts());
      } catch (err) {
        this.showToast(`Failed to load overview: ${err.message}`, 'error');
      }
    },

    renderCharts() {
      this.renderCumulativeGrowthChart();
      this.renderListingsByJobChart();
      this.renderProfitDistributionChart();
    },

    renderCumulativeGrowthChart() {
      const canvas = document.getElementById('cumulativeGrowthChart');
      if (!canvas) { return; }

      if (this.overviewCharts.cumulativeGrowth) {
        this.overviewCharts.cumulativeGrowth.destroy();
      }

      const data = this.overviewData.cumulativeGrowth || [];
      this.overviewCharts.cumulativeGrowth = new Chart(canvas, {
        type: 'line',
        data: {
          labels: data.map(d => d.date),
          datasets: [{
            label: 'Total Listings',
            data: data.map(d => d.cumulative),
            borderColor: '#4a9eff',
            backgroundColor: 'rgba(74, 158, 255, 0.1)',
            fill: true,
            tension: 0.3,
            pointRadius: data.length > 30 ? 0 : 3
          }]
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            legend: { display: false }
          },
          scales: {
            x: {
              ticks: { color: '#808080', maxTicksLimit: 10 },
              grid: { color: '#3c3c3c' }
            },
            y: {
              ticks: { color: '#808080' },
              grid: { color: '#3c3c3c' },
              beginAtZero: true
            }
          }
        }
      });
    },

    renderListingsByJobChart() {
      const canvas = document.getElementById('listingsByJobChart');
      if (!canvas) { return; }

      if (this.overviewCharts.listingsByJob) {
        this.overviewCharts.listingsByJob.destroy();
      }

      const data = this.overviewData.listingsByJob || [];
      const colors = ['#4a9eff', '#22c55e', '#f59e0b', '#ef4444', '#a855f7', '#06b6d4', '#ec4899', '#84cc16'];
      this.overviewCharts.listingsByJob = new Chart(canvas, {
        type: 'bar',
        data: {
          labels: data.map(d => d.searchTerm),
          datasets: [{
            data: data.map(d => d.count),
            backgroundColor: data.map((_, i) => colors[i % colors.length]),
            borderRadius: 4
          }]
        },
        options: {
          indexAxis: 'y',
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            legend: { display: false }
          },
          scales: {
            x: {
              ticks: { color: '#808080' },
              grid: { color: '#3c3c3c' },
              beginAtZero: true
            },
            y: {
              ticks: { color: '#e0e0e0' },
              grid: { display: false }
            }
          }
        }
      });
    },

    renderProfitDistributionChart() {
      const canvas = document.getElementById('profitDistributionChart');
      if (!canvas) { return; }

      if (this.overviewCharts.profitDistribution) {
        this.overviewCharts.profitDistribution.destroy();
      }

      const dist = this.overviewData.profitDistribution || {};
      this.overviewCharts.profitDistribution = new Chart(canvas, {
        type: 'bar',
        data: {
          labels: ['$0-25', '$25-50', '$50-100', '$100+'],
          datasets: [{
            data: [dist.range0to25 || 0, dist.range25to50 || 0, dist.range50to100 || 0, dist.range100plus || 0],
            backgroundColor: ['#4a9eff', '#22c55e', '#f59e0b', '#ef4444'],
            borderRadius: 4
          }]
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            legend: { display: false }
          },
          scales: {
            x: {
              ticks: { color: '#e0e0e0' },
              grid: { display: false }
            },
            y: {
              ticks: { color: '#808080' },
              grid: { color: '#3c3c3c' },
              beginAtZero: true
            }
          }
        }
      });
    },

    timeAgo(dateStr) {
      if (!dateStr) { return 'Never'; }
      const diff = this.now - new Date(dateStr).getTime();
      const minutes = Math.floor(diff / 60000);
      if (minutes < 1) { return 'Just now'; }
      if (minutes < 60) { return `${minutes}m ago`; }
      const hours = Math.floor(minutes / 60);
      if (hours < 24) { return `${hours}h ago`; }
      const days = Math.floor(hours / 24);
      return `${days}d ago`;
    },
```

**Step 5: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/app.js
git commit -m "feat: add Overview data loading, Chart.js rendering, and timeAgo helper"
```

---

### Task 5: Add CSS styles for the Overview dashboard

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/styles.css`

**Step 1: Append dashboard styles to end of `styles.css`**

```css
/* Overview Dashboard */
.kpi-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 16px;
  margin-bottom: 24px;
}

.kpi-card {
  background: #252526;
  border: 1px solid #3c3c3c;
  border-radius: 8px;
  padding: 20px;
  text-align: center;
}

.kpi-value {
  font-size: 28px;
  font-weight: 600;
  color: #4a9eff;
  font-variant-numeric: tabular-nums;
}

.kpi-value.profit {
  color: #22c55e;
}

.kpi-value.small {
  font-size: 20px;
}

.kpi-label {
  font-size: 12px;
  text-transform: uppercase;
  color: #808080;
  margin-top: 4px;
}

.kpi-detail {
  font-size: 12px;
  color: #606060;
  margin-top: 4px;
}

.chart-section {
  background: #252526;
  border: 1px solid #3c3c3c;
  border-radius: 8px;
  padding: 20px;
  margin-bottom: 20px;
}

.chart-section h3 {
  font-size: 14px;
  font-weight: 500;
  color: #b0b0b0;
  margin-bottom: 16px;
}

.chart-container {
  position: relative;
  height: 280px;
}

.chart-row {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 20px;
  margin-bottom: 20px;
}

.chart-section.half {
  margin-bottom: 0;
}

.overview-section {
  margin-bottom: 20px;
}

.overview-section h3 {
  font-size: 14px;
  font-weight: 500;
  color: #b0b0b0;
  margin-bottom: 12px;
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/styles.css
git commit -m "feat: add Overview dashboard CSS styles"
```

---

### Task 6: Build, test, and verify end-to-end

**Step 1: Build the API project**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Api/AIOMarketMaker.Api.csproj`
Expected: Build succeeded

**Step 2: Run all tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit"`
Expected: All tests pass (including new OverviewEndpoints tests)

**Step 3: Manual verification**

1. Start the API: `dotnet run --project AIOMarketMaker/AIOMarketMaker.Api/AIOMarketMaker.Api.csproj`
2. Verify endpoint: `curl http://localhost:7071/api/overview`
3. Start the Electron app and verify the Overview tab loads as the default view

**Step 4: Final commit**

```bash
git add -A
git commit -m "feat: Overview dashboard with KPI cards, Chart.js charts, and tables"
```
