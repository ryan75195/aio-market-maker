# Opportunities Enrichment Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Show the most profitable arbitrage opportunities by computing average sold price and estimated time to sell from similar sold listings, ordered by potential profit.

**Architecture:** New `ListingPricingComparables` junction table links active listings to similar sold listings (discovered via Pinecone). A new ETL phase after each scrape run populates this table. The API endpoint computes average price, time to sell, and profit via SQL JOINs at query time — always fresh.

**Tech Stack:** .NET 8, EF Core, Pinecone .NET Client v4.0.2, SQL Server/SQLite, NUnit 3.14, Moq

**Design doc:** `docs/plans/2026-02-04-opportunities-enrichment-design.md`

---

## Task 1: Create `ListingPricingComparables` model + migration

**Files:**
- Create: `AIOMarketMaker.Core/Data/Models/ListingPricingComparable.cs`
- Modify: `AIOMarketMaker.Core/Data/EtlDbContext.cs`
- Create: `AIOMarketMaker.Core/Data/Migrations/SqlServer/032_CreateListingPricingComparablesTable.sql`

**Context:**
- Junction table linking an active listing to its similar sold listings
- Two FK columns pointing to `Listings.Id`
- EF Core entity configuration follows existing patterns in `OnModelCreating` (line 35+)
- Next migration sequence number: 032
- Migrations are embedded resources (`.csproj` line 23: `<EmbeddedResource Include="Data\Migrations\**\*.sql" />`)
- After adding migration file, rebuild Core project to embed it

**Step 1: Create the model**

Create `AIOMarketMaker.Core/Data/Models/ListingPricingComparable.cs`:

```csharp
namespace AIOMarketMaker.Core.Data.Models;

public class ListingPricingComparable
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public int ComparableListingId { get; set; }
    public double SimilarityScore { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Listing Listing { get; set; } = null!;
    public Listing ComparableListing { get; set; } = null!;
}
```

**Step 2: Add DbSet and configure entity in EtlDbContext**

In `AIOMarketMaker.Core/Data/EtlDbContext.cs`:

Add DbSet after line 25 (after `ScrapeRunIssues`):
```csharp
public DbSet<ListingPricingComparable> ListingPricingComparables { get; set; } = null!;
```

Add entity configuration after the `ScrapeRunIssue` block (after line 142), before the closing `}` of `OnModelCreating`:

```csharp
modelBuilder.Entity<ListingPricingComparable>(entity =>
{
    entity.ToTable("ListingPricingComparables");
    entity.HasKey(e => e.Id);

    entity.Property(e => e.SimilarityScore).IsRequired();
    entity.Property(e => e.CreatedUtc).HasDefaultValueSql(dateDefaultSql);

    entity.HasIndex(e => e.ListingId);
    entity.HasIndex(e => e.ComparableListingId);

    entity.HasOne(e => e.Listing)
        .WithMany()
        .HasForeignKey(e => e.ListingId)
        .OnDelete(DeleteBehavior.NoAction);

    entity.HasOne(e => e.ComparableListing)
        .WithMany()
        .HasForeignKey(e => e.ComparableListingId)
        .OnDelete(DeleteBehavior.NoAction);
});
```

Note: `DeleteBehavior.NoAction` because both FKs point to the same table — SQL Server doesn't allow multiple cascade paths.

**Step 3: Create SQL migration**

Create `AIOMarketMaker.Core/Data/Migrations/SqlServer/032_CreateListingPricingComparablesTable.sql`:

```sql
-- Migration: 032_CreateListingPricingComparablesTable
-- Description: Junction table linking listings to similar sold listings for pricing
-- Date: 2026-02-04

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ListingPricingComparables')
BEGIN
    CREATE TABLE ListingPricingComparables (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ListingId INT NOT NULL,
        ComparableListingId INT NOT NULL,
        SimilarityScore FLOAT NOT NULL,
        CreatedUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_ListingPricingComparables_Listing FOREIGN KEY (ListingId) REFERENCES Listings(Id),
        CONSTRAINT FK_ListingPricingComparables_ComparableListing FOREIGN KEY (ComparableListingId) REFERENCES Listings(Id)
    );
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ListingPricingComparables_ListingId')
BEGIN
    CREATE INDEX IX_ListingPricingComparables_ListingId ON ListingPricingComparables (ListingId);
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ListingPricingComparables_ComparableListingId')
BEGIN
    CREATE INDEX IX_ListingPricingComparables_ComparableListingId ON ListingPricingComparables (ComparableListingId);
END
```

**Step 4: Build to embed migration**

```bash
cd <REPO_ROOT>/worktrees/AIOMarketMaker/etl-embedding-stage
dotnet build AIOMarketMaker.Core/AIOMarketMaker.Core.csproj
```

Expected: Build succeeds.

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Data/Models/ListingPricingComparable.cs AIOMarketMaker.Core/Data/EtlDbContext.cs AIOMarketMaker.Core/Data/Migrations/SqlServer/032_CreateListingPricingComparablesTable.sql
git commit -m "feat: add ListingPricingComparables table for opportunity pricing"
```

---

## Task 2: Add metadata filter support to `FindSimilar`

**Files:**
- Modify: `AIOMarketMaker.Core/Services/SemanticSearchService.cs` (interface + implementation)
- Modify: `AIOMarketMaker.Tests/UnitTests/SemanticSearchServiceTests.cs`

**Context:**
- `FindSimilar` (line 166) currently only supports `filterToListingIds` via `BuildIdFilter`
- We need a `Metadata? metadataFilter` parameter to pass Pinecone metadata filters (e.g., `listingStatus == "Sold"`)
- The filter needs to merge with the existing ID filter if both are provided
- Pinecone filter syntax: `new Metadata { ["listingStatus"] = new Metadata { ["$eq"] = "Sold" } }`
- The `QueryRequest.Filter` accepts a single `Metadata` object — multiple conditions are ANDed by placing them in the same object

**Step 1: Update interface**

In `AIOMarketMaker.Core/Services/SemanticSearchService.cs`, update the `FindSimilar` signature in the interface (line 19-23):

```csharp
Task<SemanticSearchResult> FindSimilar(
    string listingId,
    IEnumerable<string>? filterToListingIds = null,
    Metadata? metadataFilter = null,
    int? topK = null,
    CancellationToken ct = default);
```

Also add a `using Pinecone;` to the top of the file if not already present (the `Metadata` type is from the Pinecone namespace). Check existing usings — the implementation already imports it but the interface section may need it.

**Step 2: Update implementation**

Update the `FindSimilar` implementation (line 166) to accept and merge the filter:

```csharp
public async Task<SemanticSearchResult> FindSimilar(
    string listingId,
    IEnumerable<string>? filterToListingIds = null,
    Metadata? metadataFilter = null,
    int? topK = null,
    CancellationToken ct = default)
{
    var filterIds = filterToListingIds?.ToList();
    _logger.LogDebug("Finding similar to: {ListingId} (filter to {Count} IDs)",
        listingId, filterIds?.Count ?? -1);

    ct.ThrowIfCancellationRequested();

    var request = new QueryRequest
    {
        Id = listingId,
        TopK = (uint)((topK ?? _config.TopK) + 1),
        IncludeMetadata = false,
        IncludeValues = false,
        Filter = MergeFilters(BuildIdFilter(filterIds), metadataFilter)
    };

    var response = await _index.Query(request);

    var hits = response.Matches?
        .Where(m => m.Id != listingId)
        .Where(m => m.Score >= _config.SimilarityThreshold)
        .Take(topK ?? _config.TopK)
        .Select(m => new SemanticSearchHit(m.Id, m.Score ?? 0f))
        .ToList() ?? [];

    return new SemanticSearchResult(hits);
}
```

**Step 3: Add `MergeFilters` helper**

Add after `BuildIdFilter` (line 245):

```csharp
private static Metadata? MergeFilters(Metadata? idFilter, Metadata? metadataFilter)
{
    if (idFilter == null && metadataFilter == null)
    {
        return null;
    }

    if (idFilter == null)
    {
        return metadataFilter;
    }

    if (metadataFilter == null)
    {
        return idFilter;
    }

    // Merge: copy all keys from metadataFilter into idFilter
    foreach (var kvp in metadataFilter)
    {
        idFilter[kvp.Key] = kvp.Value;
    }

    return idFilter;
}
```

**Step 4: Update existing tests**

In `SemanticSearchServiceTests.cs`, find the test(s) that call `FindSimilar` and verify they still compile. The new parameter has a default value (`null`) so existing call sites should be unaffected. Build and run:

```bash
cd <REPO_ROOT>/worktrees/AIOMarketMaker/etl-embedding-stage
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~SemanticSearchServiceTests" -v n
```

Expected: All existing tests pass.

**Step 5: Add test for metadata filter**

Add to `SemanticSearchServiceTests.cs`:

```csharp
[Test]
public async Task Should_pass_metadata_filter_to_pinecone_query()
{
    var metadataFilter = new Metadata { ["listingStatus"] = new Metadata { ["$eq"] = "Sold" } };

    _mockPinecone
        .Setup(p => p.Query(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new QueryResponse
        {
            Matches = new List<ScoredVector>
            {
                new() { Id = "other1", Score = 0.95f }
            }
        });

    var result = await _service.FindSimilar("listing1", metadataFilter: metadataFilter, topK: 10);

    _mockPinecone.Verify(p => p.Query(
        It.Is<QueryRequest>(r => r.Filter != null && r.Filter.ContainsKey("listingStatus")),
        It.IsAny<CancellationToken>()), Times.Once);

    Assert.That(result.Hits, Has.Count.EqualTo(1));
}
```

Note: Check the test file's existing mock variable names and setup patterns. The mock for `IPineconeIndexClient` may be named `_mockPinecone` or `_pineconeMock` — adapt accordingly.

**Step 6: Run, then commit**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~SemanticSearchServiceTests" -v n
```

```bash
git add AIOMarketMaker.Core/Services/SemanticSearchService.cs AIOMarketMaker.Tests/UnitTests/SemanticSearchServiceTests.cs
git commit -m "feat: add metadata filter support to FindSimilar"
```

---

## Task 3: Create `ComparablesRefreshService` with tests

**Files:**
- Create: `AIOMarketMaker.Core/Services/ComparablesRefreshService.cs`
- Create: `AIOMarketMaker.Tests/Unit/Services/ComparablesRefreshService_UnitTests.cs`

**Context:**
- This service takes a list of active listings, queries Pinecone for similar sold listings, resolves IDs, and writes to `ListingPricingComparables`
- Dependencies: `ISemanticSearchService`, `EtlDbContext`, `ILogger<ComparablesRefreshService>`
- Uses `SemaphoreSlim(10)` for parallel Pinecone queries
- The Pinecone `FindSimilar` returns string `ListingId` values — we need to resolve those to integer `Listing.Id` via DB lookup
- Delete old comparables for each listing before inserting new ones
- Metadata filter: `new Metadata { ["listingStatus"] = new Metadata { ["$eq"] = "Sold" } }`

**Step 1: Write failing test — Should_find_and_store_comparables_for_active_listing**

Create `AIOMarketMaker.Tests/Unit/Services/ComparablesRefreshService_UnitTests.cs`:

```csharp
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Pinecone;
using AIOMarketMaker.Tests.Utils;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ComparablesRefreshService_UnitTests
{
    private EtlDbContext _dbContext = null!;
    private Mock<ISemanticSearchService> _searchMock = null!;
    private Mock<ILogger<ComparablesRefreshService>> _loggerMock = null!;
    private ComparablesRefreshService _service = null!;

    [SetUp]
    public void Setup()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _searchMock = new Mock<ISemanticSearchService>();
        _loggerMock = new Mock<ILogger<ComparablesRefreshService>>();
        _service = new ComparablesRefreshService(_searchMock.Object, _dbContext, _loggerMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task Should_find_and_store_comparables_for_active_listing()
    {
        var activeListing = new Listing
        {
            ListingId = "ACTIVE1", ScrapeJobId = 1, Title = "PS5 Console",
            ListingStatus = "Active", Price = 350m
        };
        var soldListing = new Listing
        {
            ListingId = "SOLD1", ScrapeJobId = 1, Title = "PS5 Console Used",
            ListingStatus = "Sold", Price = 400m
        };
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "PS5" });
        _dbContext.Listings.AddRange(activeListing, soldListing);
        await _dbContext.SaveChangesAsync();

        _searchMock
            .Setup(s => s.FindSimilar(
                "ACTIVE1", null, It.IsAny<Metadata?>(), 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResult(new List<SemanticSearchHit>
            {
                new("SOLD1", 0.92f)
            }));

        var result = await _service.Refresh(new[] { activeListing });

        Assert.Multiple(() =>
        {
            Assert.That(result.ListingsProcessed, Is.EqualTo(1));
            Assert.That(result.ComparablesFound, Is.EqualTo(1));
        });

        var comparables = await _dbContext.ListingPricingComparables.ToListAsync();
        Assert.That(comparables, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(comparables[0].ListingId, Is.EqualTo(activeListing.Id));
            Assert.That(comparables[0].ComparableListingId, Is.EqualTo(soldListing.Id));
            Assert.That(comparables[0].SimilarityScore, Is.EqualTo(0.92).Within(0.01));
        });
    }

    [Test]
    public async Task Should_replace_old_comparables_on_refresh()
    {
        var activeListing = new Listing
        {
            ListingId = "ACTIVE1", ScrapeJobId = 1, Title = "PS5",
            ListingStatus = "Active", Price = 350m
        };
        var oldSold = new Listing
        {
            ListingId = "OLD_SOLD", ScrapeJobId = 1, Title = "PS5 Old",
            ListingStatus = "Sold", Price = 380m
        };
        var newSold = new Listing
        {
            ListingId = "NEW_SOLD", ScrapeJobId = 1, Title = "PS5 New",
            ListingStatus = "Sold", Price = 420m
        };
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "PS5" });
        _dbContext.Listings.AddRange(activeListing, oldSold, newSold);
        await _dbContext.SaveChangesAsync();

        // Seed an old comparable
        _dbContext.ListingPricingComparables.Add(new ListingPricingComparable
        {
            ListingId = activeListing.Id,
            ComparableListingId = oldSold.Id,
            SimilarityScore = 0.85
        });
        await _dbContext.SaveChangesAsync();

        _searchMock
            .Setup(s => s.FindSimilar(
                "ACTIVE1", null, It.IsAny<Metadata?>(), 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResult(new List<SemanticSearchHit>
            {
                new("NEW_SOLD", 0.95f)
            }));

        await _service.Refresh(new[] { activeListing });

        var comparables = await _dbContext.ListingPricingComparables.ToListAsync();
        Assert.That(comparables, Has.Count.EqualTo(1));
        Assert.That(comparables[0].ComparableListingId, Is.EqualTo(newSold.Id));
    }

    [Test]
    public async Task Should_skip_listings_with_no_similar_results()
    {
        var activeListing = new Listing
        {
            ListingId = "LONELY1", ScrapeJobId = 1, Title = "Rare Item",
            ListingStatus = "Active", Price = 999m
        };
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "rare" });
        _dbContext.Listings.Add(activeListing);
        await _dbContext.SaveChangesAsync();

        _searchMock
            .Setup(s => s.FindSimilar(
                "LONELY1", null, It.IsAny<Metadata?>(), 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResult(new List<SemanticSearchHit>()));

        var result = await _service.Refresh(new[] { activeListing });

        Assert.Multiple(() =>
        {
            Assert.That(result.ListingsProcessed, Is.EqualTo(1));
            Assert.That(result.ComparablesFound, Is.EqualTo(0));
        });

        var comparables = await _dbContext.ListingPricingComparables.ToListAsync();
        Assert.That(comparables, Has.Count.EqualTo(0));
    }

    [Test]
    public async Task Should_skip_pinecone_hits_not_found_in_database()
    {
        var activeListing = new Listing
        {
            ListingId = "ACTIVE1", ScrapeJobId = 1, Title = "PS5",
            ListingStatus = "Active", Price = 350m
        };
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "PS5" });
        _dbContext.Listings.Add(activeListing);
        await _dbContext.SaveChangesAsync();

        // Pinecone returns a listing ID that doesn't exist in our DB
        _searchMock
            .Setup(s => s.FindSimilar(
                "ACTIVE1", null, It.IsAny<Metadata?>(), 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResult(new List<SemanticSearchHit>
            {
                new("GHOST_LISTING", 0.90f)
            }));

        var result = await _service.Refresh(new[] { activeListing });

        Assert.That(result.ComparablesFound, Is.EqualTo(0));
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
cd <REPO_ROOT>/worktrees/AIOMarketMaker/etl-embedding-stage
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ComparablesRefreshService_UnitTests" -v n
```

Expected: FAIL — `ComparablesRefreshService` doesn't exist yet.

**Step 3: Implement ComparablesRefreshService**

Create `AIOMarketMaker.Core/Services/ComparablesRefreshService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pinecone;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Core.Services;

public interface IComparablesRefreshService
{
    Task<ComparablesRefreshResult> Refresh(
        IEnumerable<Listing> activeListings,
        CancellationToken ct = default);
}

public record ComparablesRefreshResult(int ListingsProcessed, int ComparablesFound);

public class ComparablesRefreshService : IComparablesRefreshService
{
    private const int TopK = 50;
    private const int MaxConcurrency = 10;

    private static readonly Metadata SoldFilter = new()
    {
        ["listingStatus"] = new Metadata { ["$eq"] = "Sold" }
    };

    private readonly ISemanticSearchService _searchService;
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<ComparablesRefreshService> _logger;

    public ComparablesRefreshService(
        ISemanticSearchService searchService,
        EtlDbContext dbContext,
        ILogger<ComparablesRefreshService> logger)
    {
        _searchService = searchService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ComparablesRefreshResult> Refresh(
        IEnumerable<Listing> activeListings,
        CancellationToken ct = default)
    {
        var listings = activeListings.ToList();
        var totalComparables = 0;

        var semaphore = new SemaphoreSlim(MaxConcurrency);
        var results = new List<(Listing Listing, SemanticSearchResult Result)>();

        // Query Pinecone in parallel
        var tasks = listings.Select(async listing =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var result = await _searchService.FindSimilar(
                    listing.ListingId, metadataFilter: SoldFilter, topK: TopK, ct: ct);
                lock (results)
                {
                    results.Add((listing, result));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to find similar listings for {ListingId}, skipping",
                    listing.ListingId);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Collect all hit listing IDs for batch DB lookup
        var allHitListingIds = results
            .SelectMany(r => r.Result.Hits.Select(h => h.ListingId))
            .Distinct()
            .ToList();

        var listingIdToDbId = await _dbContext.Listings
            .Where(l => allHitListingIds.Contains(l.ListingId))
            .ToDictionaryAsync(l => l.ListingId, l => l.Id, ct);

        // Delete old comparables for all processed listings
        var processedListingIds = results.Select(r => r.Listing.Id).ToList();
        var oldComparables = await _dbContext.ListingPricingComparables
            .Where(c => processedListingIds.Contains(c.ListingId))
            .ToListAsync(ct);
        _dbContext.ListingPricingComparables.RemoveRange(oldComparables);

        // Insert new comparables
        foreach (var (listing, result) in results)
        {
            foreach (var hit in result.Hits)
            {
                if (!listingIdToDbId.TryGetValue(hit.ListingId, out var comparableDbId))
                {
                    continue;
                }

                _dbContext.ListingPricingComparables.Add(new ListingPricingComparable
                {
                    ListingId = listing.Id,
                    ComparableListingId = comparableDbId,
                    SimilarityScore = hit.Score
                });
                totalComparables++;
            }
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Refreshed comparables: {ListingsProcessed} listings, {ComparablesFound} comparables",
            listings.Count, totalComparables);

        return new ComparablesRefreshResult(listings.Count, totalComparables);
    }
}
```

**Step 4: Run tests**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ComparablesRefreshService_UnitTests" -v n
```

Expected: All 4 tests pass.

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/ComparablesRefreshService.cs AIOMarketMaker.Tests/Unit/Services/ComparablesRefreshService_UnitTests.cs
git commit -m "feat: add ComparablesRefreshService for finding similar sold listings"
```

---

## Task 4: Integrate comparables refresh into `ScrapeJobProcessor`

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs`
- Modify: `AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs`

**Context:**
- `ScrapeJobProcessor` constructor currently takes: `ILogger`, `EtlDbContext`, `IWebscraperClient`, `ISearchParser`, `IEbayUrlBuilder`, `IListingIndexingService`
- Add `IComparablesRefreshService` as a new dependency
- The `RunScrape` method (line 81) has the main pipeline. Add a comparables refresh step before `MarkCompleted`
- We need to load all active listings for the job and pass them to `Refresh`
- The refresh should run after both summary updates and scrape enqueueing, so it has the latest data
- When there are listings to scrape, `MarkCompleted` is NOT called (individual listing processing handles completion). But comparables refresh should still run for existing active listings.

**Step 1: Write failing test**

Add to `ScrapeJobProcessor_UnitTests.cs`:

Field:
```csharp
private Mock<IComparablesRefreshService> _comparablesRefreshMock = null!;
```

In `Setup()`:
```csharp
_comparablesRefreshMock = new Mock<IComparablesRefreshService>();
_comparablesRefreshMock
    .Setup(c => c.Refresh(It.IsAny<IEnumerable<Listing>>(), It.IsAny<CancellationToken>()))
    .ReturnsAsync(new ComparablesRefreshResult(0, 0));
```

Update `CreateProcessor()`:
```csharp
private ScrapeJobProcessor CreateProcessor() => new(
    _loggerMock.Object, _dbContext, _webscraperClientMock.Object,
    _searchParserMock.Object, _urlBuilderMock.Object,
    _indexingServiceMock.Object, _comparablesRefreshMock.Object);
```

Test:
```csharp
[Test]
public async Task Should_refresh_comparables_for_active_listings()
{
    CreateAndSeedScrapeRun();

    _dbContext.Listings.Add(new Listing
    {
        ListingId = "COMP1", ScrapeJobId = 1,
        Title = "Active Item", ListingStatus = "Active",
        Price = 100m, Condition = "USED", ShippingCost = 5m
    });
    await _dbContext.SaveChangesAsync();

    var summary = CreateSummary("COMP1", price: 90m, isSold: false);

    var callCount = 0;
    _searchParserMock
        .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
        .Returns(() =>
        {
            callCount++;
            return callCount == 2
                ? new[] { summary }
                : Enumerable.Empty<IEbayProductSummary>();
        });

    var message = new ScrapeJobMessage(1, 1, "Test", "Manual");

    await CreateProcessor().Process(message);

    _comparablesRefreshMock.Verify(c => c.Refresh(
        It.Is<IEnumerable<Listing>>(listings =>
            listings.Any(l => l.ListingId == "COMP1")),
        It.IsAny<CancellationToken>()), Times.Once);
}
```

**Step 2: Run to see it fail**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~Should_refresh_comparables_for_active_listings" -v n
```

Expected: FAIL — constructor mismatch.

**Step 3: Add dependency and refresh phase to ScrapeJobProcessor**

In `ScrapeJobProcessor.cs`:

Add field:
```csharp
private readonly IComparablesRefreshService _comparablesRefreshService;
```

Update constructor to add parameter after `indexingService`:
```csharp
public ScrapeJobProcessor(
    ILogger<ScrapeJobProcessor> logger,
    EtlDbContext dbContext,
    IWebscraperClient webscraperClient,
    ISearchParser searchParser,
    IEbayUrlBuilder urlBuilder,
    IListingIndexingService indexingService,
    IComparablesRefreshService comparablesRefreshService)
```

Assign:
```csharp
_comparablesRefreshService = comparablesRefreshService;
```

Add a new private method:
```csharp
private async Task RefreshComparables(ScrapeRun scrapeRun, int jobId)
{
    await SetPhase(scrapeRun, "Refreshing comparables");

    var activeListings = await _dbContext.Listings
        .Where(l => l.ScrapeJobId == jobId && l.ListingStatus == "Active")
        .ToListAsync();

    if (activeListings.Count == 0)
    {
        _logger.LogInformation("No active listings to refresh comparables for job {JobId}", jobId);
        return;
    }

    var result = await _comparablesRefreshService.Refresh(activeListings);
    _logger.LogInformation(
        "Refreshed comparables for job {JobId}: {Processed} listings, {Found} comparables",
        jobId, result.ListingsProcessed, result.ComparablesFound);
}
```

Modify `RunScrape` to call it. The revised flow:

```csharp
private async Task RunScrape(ScrapeRun scrapeRun, int jobId, string searchTerm)
{
    const int maxPages = 100;

    var soldSummaries = await SearchSoldListings(scrapeRun, searchTerm, maxPages);
    var activeSummaries = await SearchActiveListings(scrapeRun, searchTerm, maxPages);
    var classified = await ClassifyListings(scrapeRun, activeSummaries, soldSummaries, jobId);

    if (classified.ToUpdateFromSummary.Count > 0)
    {
        await UpdateListingsFromSummary(scrapeRun, classified.ToUpdateFromSummary, classified.ExistingListings);
    }

    if (classified.ToScrape.Count > 0)
    {
        await EnqueueListingsForScrape(scrapeRun, classified.ToScrape, jobId);
    }

    await RefreshComparables(scrapeRun, jobId);

    if (classified.ToScrape.Count == 0)
    {
        await MarkCompleted(scrapeRun);
    }
}
```

Note: This restructures `RunScrape` slightly — the early return for "no listings" is removed. `RefreshComparables` always runs. `MarkCompleted` only runs when there's nothing enqueued for scraping (same as before, but expressed differently).

**Step 4: Run all ScrapeJobProcessor tests**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ScrapeJobProcessor_UnitTests" -v n
```

Expected: All tests pass (existing + new).

**Step 5: Commit**

```bash
git add AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs
git commit -m "feat: add comparables refresh phase to ScrapeJobProcessor"
```

---

## Task 5: Register `IComparablesRefreshService` in DI

**Files:**
- Modify: `AIOMarketMaker.Etl/Program.cs`

**Context:**
- `ComparablesRefreshService` depends on `ISemanticSearchService` (Singleton, conditionally registered) and `EtlDbContext` (Scoped)
- Since it depends on a Scoped service, register as Scoped
- When Pinecone isn't configured, `ISemanticSearchService` isn't registered — need a null fallback or conditional registration
- Simplest: only register `ComparablesRefreshService` when Pinecone is configured. Add a `NullComparablesRefreshService` fallback.

**Step 1: Add NullComparablesRefreshService**

In `AIOMarketMaker.Core/Services/ComparablesRefreshService.cs`, add after the main class:

```csharp
public class NullComparablesRefreshService : IComparablesRefreshService
{
    public Task<ComparablesRefreshResult> Refresh(
        IEnumerable<Listing> activeListings, CancellationToken ct = default)
        => Task.FromResult(new ComparablesRefreshResult(0, 0));
}
```

**Step 2: Register in Program.cs**

In `AIOMarketMaker.Etl/Program.cs`, after the `IListingIndexingService` registration block (after line 162), add:

```csharp
// Comparables refresh service - requires Pinecone for similarity search
if (!string.IsNullOrEmpty(pineconeApiKey))
{
    services.AddScoped<IComparablesRefreshService, ComparablesRefreshService>();
}
else
{
    services.AddSingleton<IComparablesRefreshService, NullComparablesRefreshService>();
}
```

**Step 3: Build and run all unit tests**

```bash
cd <REPO_ROOT>/worktrees/AIOMarketMaker/etl-embedding-stage
dotnet build AIOMarketMaker.sln
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter Category=Unit -v n
```

Expected: Build succeeds, all unit tests pass.

**Step 4: Commit**

```bash
git add AIOMarketMaker.Core/Services/ComparablesRefreshService.cs AIOMarketMaker.Etl/Program.cs
git commit -m "feat: register IComparablesRefreshService in DI with null fallback"
```

---

## Task 6: Update `GetActiveListings` endpoint with enrichment query

**Files:**
- Modify: `AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs`

**Context:**
- Current endpoint (line 400) does a simple LINQ query returning anonymous type
- Need to replace with a query that JOINs `ListingPricingComparables` and `ListingStatusHistory` to compute averages
- CLAUDE.md says "No anonymous types" — use a named record
- The query needs GROUP BY which is complex in EF Core — consider using `FromSqlRaw` or a view, but EF Core LINQ with GroupBy should work for this
- Actually, EF Core's LINQ GroupBy has limitations. The cleanest approach: use two queries — one for listings, one for aggregates — or use raw SQL.
- Recommend: raw SQL via `FromSqlRaw` for the aggregate, then project into a record.

**Step 1: Create the response record**

Add to `ScrapeJobsApi.cs` (above the class, per coding standards):

```csharp
public record OpportunityListing(
    int Id,
    string ListingId,
    string? Title,
    decimal? Price,
    string? Currency,
    decimal? ShippingCost,
    string? Url,
    string? Condition,
    string? ListingStatus,
    string? Location,
    DateTime? EndDateUtc,
    DateTime CreatedUtc,
    string? SearchTerm,
    string? FirstImage,
    decimal? AverageSoldPrice,
    int SimilarSoldCount,
    int? EstimatedDaysToSell,
    decimal? PotentialProfit);
```

**Step 2: Replace the GetActiveListings implementation**

Replace the method body (lines 403-431):

```csharp
[Function("GetActiveListings")]
public async Task<HttpResponseData> GetActiveListings(
    [HttpTrigger(AuthorizationLevel.Function, "get", Route = "listings/active")] HttpRequestData req)
{
    var listings = await _dbContext.Listings
        .Include(l => l.ScrapeJob)
        .Where(l => l.ListingStatus == "Active")
        .Select(l => new
        {
            Listing = l,
            SearchTerm = l.ScrapeJob != null ? l.ScrapeJob.SearchTerm : null,
            Comparables = _dbContext.ListingPricingComparables
                .Where(c => c.ListingId == l.Id)
                .Join(
                    _dbContext.Listings,
                    c => c.ComparableListingId,
                    comp => comp.Id,
                    (c, comp) => new { comp.Price, comp.Id })
                .ToList(),
            AvgDaysToSell = _dbContext.ListingPricingComparables
                .Where(c => c.ListingId == l.Id)
                .Join(
                    _dbContext.ListingStatusHistory.Where(h => h.SoldDateUtc != null),
                    c => c.ComparableListingId,
                    h => h.ListingId,
                    (c, h) => EF.Functions.DateDiffDay(
                        _dbContext.Listings.Where(comp => comp.Id == c.ComparableListingId)
                            .Select(comp => comp.CreatedUtc).FirstOrDefault(),
                        h.SoldDateUtc))
                .DefaultIfEmpty()
                .Average(d => d)
        })
        .ToListAsync();

    var results = listings
        .Select(l =>
        {
            var pricesWithValue = l.Comparables.Where(c => c.Price.HasValue).ToList();
            var avgSoldPrice = pricesWithValue.Count > 0
                ? pricesWithValue.Average(c => c.Price!.Value)
                : (decimal?)null;

            return new OpportunityListing(
                l.Listing.Id,
                l.Listing.ListingId,
                l.Listing.Title,
                l.Listing.Price,
                l.Listing.Currency,
                l.Listing.ShippingCost,
                l.Listing.Url,
                l.Listing.Condition,
                l.Listing.ListingStatus,
                l.Listing.Location,
                l.Listing.EndDateUtc,
                l.Listing.CreatedUtc,
                l.SearchTerm,
                l.Listing.Images,
                avgSoldPrice,
                pricesWithValue.Count,
                l.AvgDaysToSell.HasValue ? (int?)Math.Round(l.AvgDaysToSell.Value) : null,
                avgSoldPrice.HasValue && l.Listing.Price.HasValue
                    ? avgSoldPrice.Value - l.Listing.Price.Value
                    : null);
        })
        .OrderByDescending(o => o.PotentialProfit ?? decimal.MinValue)
        .Take(100)
        .ToList();

    var response = req.CreateResponse(HttpStatusCode.OK);
    await response.WriteAsJsonAsync(results);
    return response;
}
```

**Important note:** The EF Core sub-query for `AvgDaysToSell` may not translate cleanly to SQL, especially with `DateDiffDay` and the nested sub-select. If it doesn't work, fall back to computing it in memory:

Alternative approach (compute in memory — simpler, works with both SQLite and SQL Server):

```csharp
[Function("GetActiveListings")]
public async Task<HttpResponseData> GetActiveListings(
    [HttpTrigger(AuthorizationLevel.Function, "get", Route = "listings/active")] HttpRequestData req)
{
    var activeListings = await _dbContext.Listings
        .Include(l => l.ScrapeJob)
        .Where(l => l.ListingStatus == "Active")
        .ToListAsync();

    var activeListingIds = activeListings.Select(l => l.Id).ToList();

    var comparables = await _dbContext.ListingPricingComparables
        .Where(c => activeListingIds.Contains(c.ListingId))
        .Join(
            _dbContext.Listings,
            c => c.ComparableListingId,
            comp => comp.Id,
            (c, comp) => new { c.ListingId, ComparableListingId = comp.Id, comp.Price, comp.CreatedUtc })
        .ToListAsync();

    var soldDates = await _dbContext.ListingStatusHistory
        .Where(h => h.SoldDateUtc != null)
        .Where(h => comparables.Select(c => c.ComparableListingId).Contains(h.ListingId))
        .GroupBy(h => h.ListingId)
        .Select(g => new { ListingId = g.Key, SoldDateUtc = g.Max(h => h.SoldDateUtc) })
        .ToDictionaryAsync(x => x.ListingId, x => x.SoldDateUtc);

    var grouped = comparables
        .GroupBy(c => c.ListingId)
        .ToDictionary(
            g => g.Key,
            g =>
            {
                var withPrice = g.Where(c => c.Price.HasValue).ToList();
                var avgPrice = withPrice.Count > 0 ? withPrice.Average(c => c.Price!.Value) : (decimal?)null;
                var daysToSell = g
                    .Select(c => soldDates.TryGetValue(c.ComparableListingId, out var soldDate) && soldDate.HasValue
                        ? (int?)(soldDate.Value - c.CreatedUtc).Days
                        : null)
                    .Where(d => d.HasValue)
                    .ToList();
                var avgDays = daysToSell.Count > 0 ? (int?)Math.Round(daysToSell.Average(d => d!.Value)) : null;

                return new { AvgPrice = avgPrice, Count = withPrice.Count, AvgDaysToSell = avgDays };
            });

    var results = activeListings
        .Select(l =>
        {
            grouped.TryGetValue(l.Id, out var agg);

            return new OpportunityListing(
                l.Id,
                l.ListingId,
                l.Title,
                l.Price,
                l.Currency,
                l.ShippingCost,
                l.Url,
                l.Condition,
                l.ListingStatus,
                l.Location,
                l.EndDateUtc,
                l.CreatedUtc,
                l.ScrapeJob?.SearchTerm,
                l.Images,
                agg?.AvgPrice,
                agg?.Count ?? 0,
                agg?.AvgDaysToSell,
                agg?.AvgPrice != null && l.Price.HasValue
                    ? agg.AvgPrice.Value - l.Price.Value
                    : null);
        })
        .OrderByDescending(o => o.PotentialProfit ?? decimal.MinValue)
        .Take(100)
        .ToList();

    var response = req.CreateResponse(HttpStatusCode.OK);
    await response.WriteAsJsonAsync(results);
    return response;
}
```

Use this in-memory approach — it's more reliable across SQLite (tests) and SQL Server (production).

**Step 3: Build and verify**

```bash
cd <REPO_ROOT>/worktrees/AIOMarketMaker/etl-embedding-stage
dotnet build AIOMarketMaker.sln
```

Expected: Build succeeds.

**Step 4: Commit**

```bash
git add AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs
git commit -m "feat: enrich GetActiveListings with average sold price, time to sell, and profit"
```

---

## Task 7: Update UI to display enrichment data

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/index.html`
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js`

**Context:**
- The Opportunities table (line 84-115 in `index.html`) currently has: Image, Title, Price, Condition, Search Term, Discovered, Actions
- Add: Avg Sold Price, Potential Profit, Est. Time to Sell
- Remove Condition and Search Term columns to make room (or keep — depends on space)
- Format profit with color: green for positive, red for negative
- Format time to sell as "~X days"
- The data comes from the API response which now includes `averageSoldPrice`, `similarSoldCount`, `estimatedDaysToSell`, `potentialProfit`

**Step 1: Update table headers in index.html**

Replace the thead section (lines 86-94):

```html
<thead>
  <tr>
    <th>Image</th>
    <th>Title</th>
    <th>Listed Price</th>
    <th>Avg Sold Price</th>
    <th>Potential Profit</th>
    <th>Est. Time to Sell</th>
    <th>Condition</th>
    <th>Actions</th>
  </tr>
</thead>
```

**Step 2: Update table body in index.html**

Replace the tbody rows (lines 97-110):

```html
<tr v-for="listing in opportunities" :key="listing.id">
  <td class="image-cell">
    <img v-if="getFirstImage(listing.firstImage)" :src="getFirstImage(listing.firstImage)" class="listing-thumb" />
    <span v-else class="no-image">-</span>
  </td>
  <td class="title-cell" :title="listing.title">{{ truncate(listing.title, 50) }}</td>
  <td class="price-cell">{{ formatPrice(listing.price, listing.currency) }}</td>
  <td class="price-cell">
    <span v-if="listing.averageSoldPrice != null">
      {{ formatPrice(listing.averageSoldPrice, listing.currency) }}
      <small>({{ listing.similarSoldCount }} sold)</small>
    </span>
    <span v-else>-</span>
  </td>
  <td class="price-cell">
    <span v-if="listing.potentialProfit != null"
          :style="{ color: listing.potentialProfit >= 0 ? '#22c55e' : '#ef4444' }">
      {{ listing.potentialProfit >= 0 ? '+' : '' }}{{ formatPrice(listing.potentialProfit, listing.currency) }}
    </span>
    <span v-else>-</span>
  </td>
  <td>
    <span v-if="listing.estimatedDaysToSell != null">~{{ listing.estimatedDaysToSell }} days</span>
    <span v-else>-</span>
  </td>
  <td>{{ listing.condition || '-' }}</td>
  <td>
    <a v-if="listing.url" :href="listing.url" target="_blank" class="btn small">View</a>
  </td>
</tr>
<tr v-if="opportunities.length === 0">
  <td colspan="8" class="empty">No active listings found</td>
</tr>
```

Note: colspan changed from 7 to 8.

**Step 3: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/index.html
git commit -m "feat: display avg sold price, profit, and time to sell in opportunities UI"
```

---

## Task 8: Full build + all unit tests verification

**Files:** None (verification only)

**Step 1: Clean build**

```bash
cd <REPO_ROOT>/worktrees/AIOMarketMaker/etl-embedding-stage
dotnet build AIOMarketMaker.sln
```

Expected: 0 errors.

**Step 2: Run all unit tests**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter Category=Unit -v n
```

Expected: All tests pass (except 2 pre-existing failures in `ScrapeJobsApi_GetHistoryIssues_Tests`).

**Step 3: Review git log**

```bash
git log --oneline -15
```

Verify all commits are present and coherent.
