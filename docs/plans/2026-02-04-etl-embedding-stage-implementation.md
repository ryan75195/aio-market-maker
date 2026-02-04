# ETL Embedding Stage Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** After listing processing, embed new listings in Pinecone with metadata, and update metadata for re-scraped listings without re-embedding.

**Architecture:** New `IListingIndexingService` in Core handles embed-or-update decision. Integrates into `ListingProcessorService` (after upsert) and `ScrapeJobProcessor.UpdateListingsFromSummary` (after DB save). Existing `IPineconeIndexClient`, `IEmbeddingService`, and `ISemanticSearchService` get `Async` suffix removed.

**Tech Stack:** .NET 8, OpenAI embeddings (text-embedding-3-large, 3072 dims), Pinecone .NET Client v4.0.2, NUnit 3.14, Moq

**Design doc:** `docs/plans/2026-02-04-etl-embedding-stage-design.md`

---

## Task 1: Rename `IPineconeIndexClient` methods (drop Async suffix) and add `Update`

**Files:**
- Modify: `AIOMarketMaker.Core/Services/IPineconeIndexClient.cs`
- Modify: `AIOMarketMaker.Core/Services/SemanticSearchService.cs` (all call sites)
- Modify: `AIOMarketMaker.Tests/UnitTests/SemanticSearchServiceTests.cs` (all mock setups/verifies)

**Context:**
- The interface has 4 methods: `UpsertAsync`, `QueryAsync`, `DeleteAsync`, `FetchAsync`
- The wrapper class delegates to SDK's `_index.*Async()` methods (those keep their names — it's the SDK)
- `SemanticSearchService` calls all 4 methods
- Tests mock all 4 methods with `Async` suffix in `Setup`/`Verify` calls
- Pinecone SDK v4.0.2 has `_index.UpdateAsync(UpdateRequest)` available

**Step 1: Rename interface and wrapper methods, add Update**

In `AIOMarketMaker.Core/Services/IPineconeIndexClient.cs`:

```csharp
public interface IPineconeIndexClient
{
    Task Upsert(UpsertRequest request, CancellationToken ct = default);
    Task<QueryResponse> Query(QueryRequest request, CancellationToken ct = default);
    Task Delete(DeleteRequest request, CancellationToken ct = default);
    Task<FetchResponse> Fetch(FetchRequest request, CancellationToken ct = default);
    Task Update(UpdateRequest request, CancellationToken ct = default);
}

public class PineconeIndexClientWrapper : IPineconeIndexClient
{
    private readonly IndexClient _index;

    public PineconeIndexClientWrapper(string apiKey, string indexName)
    {
        var client = new PineconeClient(apiKey);
        _index = client.Index(indexName);
    }

    public Task Upsert(UpsertRequest request, CancellationToken ct = default)
        => _index.UpsertAsync(request);

    public Task<QueryResponse> Query(QueryRequest request, CancellationToken ct = default)
        => _index.QueryAsync(request);

    public Task Delete(DeleteRequest request, CancellationToken ct = default)
        => _index.DeleteAsync(request);

    public Task<FetchResponse> Fetch(FetchRequest request, CancellationToken ct = default)
        => _index.FetchAsync(request);

    public Task Update(UpdateRequest request, CancellationToken ct = default)
        => _index.UpdateAsync(request);
}
```

**Step 2: Update all call sites in SemanticSearchService**

In `AIOMarketMaker.Core/Services/SemanticSearchService.cs`, rename every call:
- Line 106: `_index.UpsertAsync(...)` -> `_index.Upsert(...)`
- Line 156: `_index.QueryAsync(...)` -> `_index.Query(...)`
- Line 187: `_index.QueryAsync(...)` -> `_index.Query(...)`
- Line 210: `_index.DeleteAsync(...)` -> `_index.Delete(...)`
- Line 219: `_index.FetchAsync(...)` -> `_index.Fetch(...)`

**Step 3: Update all mock setups and verifies in tests**

In `AIOMarketMaker.Tests/UnitTests/SemanticSearchServiceTests.cs`, rename every `Setup`/`Verify` call:
- All `.Setup(x => x.UpsertAsync(...)` -> `.Setup(x => x.Upsert(...)`
- All `.Setup(x => x.QueryAsync(...)` -> `.Setup(x => x.Query(...)`
- All `.Setup(x => x.DeleteAsync(...)` -> `.Setup(x => x.Delete(...)`
- All `.Setup(x => x.FetchAsync(...)` -> `.Setup(x => x.Fetch(...)`
- Same for all `.Verify(...)` calls

**Step 4: Build and run tests**

```bash
cd <REPO_ROOT>/worktrees/AIOMarketMaker/etl-embedding-stage
dotnet build AIOMarketMaker.sln
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~SemanticSearchServiceTests"
```

Expected: All 18 tests pass.

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/IPineconeIndexClient.cs AIOMarketMaker.Core/Services/SemanticSearchService.cs AIOMarketMaker.Tests/UnitTests/SemanticSearchServiceTests.cs
git commit -m "refactor: drop Async suffix from IPineconeIndexClient, add Update method"
```

---

## Task 2: Rename `IEmbeddingService` methods (drop Async suffix)

**Files:**
- Modify: `AIOMarketMaker.Core/Services/EmbeddingService.cs` (interface + implementation)
- Modify: `AIOMarketMaker.Core/Services/SemanticSearchService.cs` (call sites)
- Modify: `AIOMarketMaker.Tests/UnitTests/SemanticSearchServiceTests.cs` (mock setups/verifies)
- Modify: `AIOMarketMaker.Tests/UnitTests/EmbeddingServiceTests.cs` (if it calls the methods)

**Context:**
- Interface has 2 methods: `GetEmbeddingAsync`, `GetEmbeddingsAsync`
- `SemanticSearchService` calls both (line 90: `GetEmbeddingsAsync`, line 143: `GetEmbeddingAsync`)
- Tests mock both methods

**Step 1: Rename interface and implementation**

In `AIOMarketMaker.Core/Services/EmbeddingService.cs`:
- Interface: `GetEmbeddingAsync` -> `GetEmbedding`, `GetEmbeddingsAsync` -> `GetEmbeddings`
- Class: same renames on the `public async Task<...>` method signatures

**Step 2: Update call sites in SemanticSearchService**

- Line 90: `_embeddingService.GetEmbeddingsAsync(...)` -> `_embeddingService.GetEmbeddings(...)`
- Line 143: `_embeddingService.GetEmbeddingAsync(...)` -> `_embeddingService.GetEmbedding(...)`

**Step 3: Update mock setups/verifies in tests**

In `SemanticSearchServiceTests.cs`:
- All `.Setup(x => x.GetEmbeddingsAsync(...)` -> `.Setup(x => x.GetEmbeddings(...)`
- All `.Setup(x => x.GetEmbeddingAsync(...)` -> `.Setup(x => x.GetEmbedding(...)`
- All `.Verify(x => x.GetEmbeddingsAsync(...)` -> `.Verify(x => x.GetEmbeddings(...)`

In `EmbeddingServiceTests.cs`: update any direct method calls if present.

**Step 4: Build and run tests**

```bash
cd <REPO_ROOT>/worktrees/AIOMarketMaker/etl-embedding-stage
dotnet build AIOMarketMaker.sln
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~SemanticSearchServiceTests or FullyQualifiedName~EmbeddingServiceTests"
```

Expected: All tests pass.

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/EmbeddingService.cs AIOMarketMaker.Core/Services/SemanticSearchService.cs AIOMarketMaker.Tests/UnitTests/SemanticSearchServiceTests.cs AIOMarketMaker.Tests/UnitTests/EmbeddingServiceTests.cs
git commit -m "refactor: drop Async suffix from IEmbeddingService methods"
```

---

## Task 3: Rename `ISemanticSearchService` methods (drop Async suffix)

**Files:**
- Modify: `AIOMarketMaker.Core/Services/SemanticSearchService.cs` (interface + implementation)
- Modify: `AIOMarketMaker.Tests/UnitTests/SemanticSearchServiceTests.cs` (test method calls)
- Search for any other callers of `ISemanticSearchService` across the codebase

**Context:**
- Interface has 5 methods: `IndexListingsAsync`, `SearchAsync`, `FindSimilarAsync`, `DeleteAsync`, `ExistsAsync`
- Tests call these directly on the `_service` instance
- May have other callers in Console/Etl projects

**Step 1: Search for all callers**

```bash
cd <REPO_ROOT>/worktrees/AIOMarketMaker/etl-embedding-stage
grep -rn "IndexListingsAsync\|SearchAsync\|FindSimilarAsync\|DeleteAsync\|ExistsAsync" --include="*.cs" | grep -v "obj/" | grep -v "bin/"
```

Update all call sites found.

**Step 2: Rename interface and implementation**

In `AIOMarketMaker.Core/Services/SemanticSearchService.cs`:
- Interface: Drop `Async` from all 5 method names
- Class: Same renames on implementation method signatures

**Step 3: Update test calls**

In `SemanticSearchServiceTests.cs`:
- `_service.IndexListingsAsync(...)` -> `_service.IndexListings(...)`
- `_service.SearchAsync(...)` -> `_service.Search(...)`
- `_service.FindSimilarAsync(...)` -> `_service.FindSimilar(...)`
- `_service.DeleteAsync(...)` -> `_service.Delete(...)`
- `_service.ExistsAsync(...)` -> `_service.Exists(...)`

**Step 4: Build and run all tests**

```bash
cd <REPO_ROOT>/worktrees/AIOMarketMaker/etl-embedding-stage
dotnet build AIOMarketMaker.sln
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter Category=Unit
```

Expected: All unit tests pass.

**Step 5: Commit**

```bash
git add -A
git commit -m "refactor: drop Async suffix from ISemanticSearchService methods"
```

---

## Task 4: Create `ListingIndexingService` with tests (new listing path)

**Files:**
- Create: `AIOMarketMaker.Core/Services/ListingIndexingService.cs`
- Create: `AIOMarketMaker.Tests/Unit/Services/ListingIndexingService_UnitTests.cs`

**Context:**
- This service decides: embed + upsert (new) or metadata-only update (existing)
- Dependencies: `IEmbeddingService`, `IPineconeIndexClient`, `ILogger<ListingIndexingService>`
- `Listing` entity is in `AIOMarketMaker.Core.Data.Models`
- Pinecone `Metadata` class supports indexer syntax: `new Metadata { ["key"] = value }`
- Pinecone `Vector` needs `Id`, `Values`, `Metadata`
- Pinecone `UpsertRequest` takes `Vectors` list
- Pinecone `UpdateRequest` takes `Id`, `SetMetadata`
- Embedding text = `Title + " " + Description` (skip both-null/empty -> Skipped)
- `soldDateUtc` comes from the most recent `ListingStatusHistory` with a non-null `SoldDateUtc` — but the `Listing` entity doesn't have this field directly. For simplicity, we pass `null` for `soldDateUtc` when indexing from `ListingProcessorService` (the sold date is on `ListingStatusHistory`, not `Listing`). The metadata field exists for future enrichment.

**Actually** — looking at `ListingStatusHistory`, the `SoldDateUtc` is on the history record, parsed from the listing page. The `ListingProcessorService.CreateStatusHistory` method writes `parsed.SoldDateUtc` to the history. But the `Listing` entity itself doesn't store `SoldDateUtc`.

**Design decision:** Store `soldDateUtc` as `null` in Pinecone metadata for now. When we add sold-date queries, we can enrich it from the most recent history record. Keeps this implementation simple.

**Step 1: Write failing test — Should_embed_and_upsert_when_new**

Create `AIOMarketMaker.Tests/Unit/Services/ListingIndexingService_UnitTests.cs`:

```csharp
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Pinecone;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ListingIndexingService_UnitTests
{
    private Mock<IEmbeddingService> _embeddingMock = null!;
    private Mock<IPineconeIndexClient> _pineconeMock = null!;
    private Mock<ILogger<ListingIndexingService>> _loggerMock = null!;
    private ListingIndexingService _service = null!;

    [SetUp]
    public void Setup()
    {
        _embeddingMock = new Mock<IEmbeddingService>();
        _pineconeMock = new Mock<IPineconeIndexClient>();
        _loggerMock = new Mock<ILogger<ListingIndexingService>>();
        _service = new ListingIndexingService(_embeddingMock.Object, _pineconeMock.Object, _loggerMock.Object);
    }

    [Test]
    public async Task Should_embed_and_upsert_when_new()
    {
        var listing = CreateListing(title: "PS5 Console", description: "Brand new PlayStation 5");
        _embeddingMock
            .Setup(e => e.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 0.1f, 0.2f, 0.3f });

        var result = await _service.Index(listing, isNew: true);

        Assert.That(result.Action, Is.EqualTo(IndexingAction.Embedded));
        _embeddingMock.Verify(e => e.GetEmbedding("PS5 Console Brand new PlayStation 5", It.IsAny<CancellationToken>()), Times.Once);
        _pineconeMock.Verify(p => p.Upsert(It.Is<UpsertRequest>(r =>
            r.Vectors.Count() == 1
            && r.Vectors.First().Id == "ABC123"
            && r.Vectors.First().Values.SequenceEqual(new float[] { 0.1f, 0.2f, 0.3f })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Listing CreateListing(
        string listingId = "ABC123", int scrapeJobId = 1,
        string? title = "Test Item", string? description = "Test description",
        decimal? price = 99.99m, decimal? shippingCost = 5m,
        string? condition = "NEW", string? listingStatus = "Active",
        string? purchaseFormat = "BuyItNow") =>
        new()
        {
            ListingId = listingId,
            ScrapeJobId = scrapeJobId,
            Title = title,
            Description = description,
            Price = price,
            ShippingCost = shippingCost,
            Condition = condition,
            ListingStatus = listingStatus,
            PurchaseFormat = purchaseFormat,
            CreatedUtc = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc)
        };
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ListingIndexingService_UnitTests.Should_embed_and_upsert_when_new" -v n
```

Expected: FAIL — `ListingIndexingService` class doesn't exist yet.

**Step 3: Write minimal implementation**

Create `AIOMarketMaker.Core/Services/ListingIndexingService.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Pinecone;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Core.Services;

public interface IListingIndexingService
{
    Task<IndexingResult> Index(Listing listing, bool isNew, CancellationToken ct = default);
}

public record IndexingResult(IndexingAction Action, string? Error = null);

public enum IndexingAction
{
    Embedded,
    MetadataUpdated,
    Skipped
}

public class ListingIndexingService : IListingIndexingService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IPineconeIndexClient _pinecone;
    private readonly ILogger<ListingIndexingService> _logger;

    public ListingIndexingService(
        IEmbeddingService embeddingService,
        IPineconeIndexClient pinecone,
        ILogger<ListingIndexingService> logger)
    {
        _embeddingService = embeddingService;
        _pinecone = pinecone;
        _logger = logger;
    }

    public async Task<IndexingResult> Index(Listing listing, bool isNew, CancellationToken ct = default)
    {
        var text = BuildEmbeddingText(listing);

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogDebug("Skipping indexing for {ListingId}: no title or description", listing.ListingId);
            return new IndexingResult(IndexingAction.Skipped);
        }

        var metadata = BuildMetadata(listing);

        if (isNew)
        {
            var embedding = await _embeddingService.GetEmbedding(text, ct);

            await _pinecone.Upsert(new UpsertRequest
            {
                Vectors = new[]
                {
                    new Vector
                    {
                        Id = listing.ListingId,
                        Values = embedding,
                        Metadata = metadata
                    }
                }
            }, ct);

            _logger.LogInformation("Embedded and indexed new listing {ListingId}", listing.ListingId);
            return new IndexingResult(IndexingAction.Embedded);
        }

        await _pinecone.Update(new UpdateRequest
        {
            Id = listing.ListingId,
            SetMetadata = metadata
        }, ct);

        _logger.LogInformation("Updated metadata for listing {ListingId}", listing.ListingId);
        return new IndexingResult(IndexingAction.MetadataUpdated);
    }

    private static string BuildEmbeddingText(Listing listing)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(listing.Title))
        {
            parts.Add(listing.Title);
        }

        if (!string.IsNullOrWhiteSpace(listing.Description))
        {
            parts.Add(listing.Description);
        }

        return string.Join(" ", parts);
    }

    private static Metadata BuildMetadata(Listing listing)
    {
        var metadata = new Metadata
        {
            ["listingId"] = listing.ListingId,
            ["scrapeJobId"] = (long)listing.ScrapeJobId,
            ["condition"] = listing.Condition ?? "",
            ["listingStatus"] = listing.ListingStatus ?? "",
            ["purchaseFormat"] = listing.PurchaseFormat ?? "",
            ["createdUtc"] = listing.CreatedUtc.ToString("O")
        };

        if (listing.Price.HasValue)
        {
            metadata["price"] = (double)listing.Price.Value;
        }

        if (listing.ShippingCost.HasValue)
        {
            metadata["shippingCost"] = (double)listing.ShippingCost.Value;
        }

        return metadata;
    }
}
```

**Step 4: Run test to verify it passes**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ListingIndexingService_UnitTests.Should_embed_and_upsert_when_new" -v n
```

Expected: PASS

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/ListingIndexingService.cs AIOMarketMaker.Tests/Unit/Services/ListingIndexingService_UnitTests.cs
git commit -m "feat: add ListingIndexingService with embed+upsert for new listings"
```

---

## Task 5: Add tests for metadata-update and skip paths

**Files:**
- Modify: `AIOMarketMaker.Tests/Unit/Services/ListingIndexingService_UnitTests.cs`

**Context:**
- `isNew: false` -> calls `_pinecone.Update(...)` with `SetMetadata`, does NOT call `_embeddingService.GetEmbedding`
- Both title and description null/empty -> returns `Skipped`, no external calls
- Use `TestCaseSource` for skip edge cases (both null, both empty, both whitespace)

**Step 1: Add metadata-update test**

```csharp
[Test]
public async Task Should_update_metadata_only_when_not_new()
{
    var listing = CreateListing(price: 150m, listingStatus: "Sold");

    var result = await _service.Index(listing, isNew: false);

    Assert.That(result.Action, Is.EqualTo(IndexingAction.MetadataUpdated));
    _embeddingMock.Verify(e => e.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    _pineconeMock.Verify(p => p.Update(It.Is<UpdateRequest>(r =>
        r.Id == "ABC123"
        && r.SetMetadata != null),
        It.IsAny<CancellationToken>()), Times.Once);
}
```

**Step 2: Add skip test cases**

```csharp
private static IEnumerable<TestCaseData> NoContentCases()
{
    yield return new TestCaseData(null, null).SetDescription("Both null");
    yield return new TestCaseData("", "").SetDescription("Both empty");
    yield return new TestCaseData("   ", "   ").SetDescription("Both whitespace");
    yield return new TestCaseData(null, "").SetDescription("Null title, empty description");
    yield return new TestCaseData("", null).SetDescription("Empty title, null description");
}

[TestCaseSource(nameof(NoContentCases))]
public async Task Should_skip_when_no_title_or_description(string? title, string? description)
{
    var listing = CreateListing(title: title, description: description);

    var result = await _service.Index(listing, isNew: true);

    Assert.That(result.Action, Is.EqualTo(IndexingAction.Skipped));
    _embeddingMock.Verify(e => e.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    _pineconeMock.Verify(p => p.Upsert(It.IsAny<UpsertRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    _pineconeMock.Verify(p => p.Update(It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
}
```

**Step 3: Add metadata fields test**

```csharp
[Test]
public async Task Should_include_all_metadata_fields_in_upsert()
{
    var listing = CreateListing(
        listingId: "META1", scrapeJobId: 42,
        price: 199.99m, shippingCost: 12.50m,
        condition: "GOOD", listingStatus: "Sold",
        purchaseFormat: "Auction");

    Metadata? capturedMetadata = null;
    _pineconeMock
        .Setup(p => p.Upsert(It.IsAny<UpsertRequest>(), It.IsAny<CancellationToken>()))
        .Callback<UpsertRequest, CancellationToken>((req, _) =>
            capturedMetadata = req.Vectors.First().Metadata);

    _embeddingMock
        .Setup(e => e.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new float[] { 0.1f });

    await _service.Index(listing, isNew: true);

    Assert.That(capturedMetadata, Is.Not.Null);
    Assert.Multiple(() =>
    {
        Assert.That(capturedMetadata!["listingId"].Value, Is.EqualTo("META1"));
        Assert.That(capturedMetadata["scrapeJobId"].Value, Is.EqualTo(42L));
        Assert.That(capturedMetadata["price"].Value, Is.EqualTo(199.99d));
        Assert.That(capturedMetadata["shippingCost"].Value, Is.EqualTo(12.50d));
        Assert.That(capturedMetadata["condition"].Value, Is.EqualTo("GOOD"));
        Assert.That(capturedMetadata["listingStatus"].Value, Is.EqualTo("Sold"));
        Assert.That(capturedMetadata["purchaseFormat"].Value, Is.EqualTo("Auction"));
        Assert.That(capturedMetadata["createdUtc"].Value, Is.Not.Null.And.Not.Empty);
    });
}
```

**Note on metadata value assertions:** The Pinecone `Metadata` class stores values as `MetadataValue` which has a `.Value` property returning `object?`. The exact assertion syntax may need adjustment based on how the SDK exposes values. If `.Value` doesn't work, try casting or checking string representations. Adapt as needed when implementing.

**Step 4: Run all ListingIndexingService tests**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ListingIndexingService_UnitTests" -v n
```

Expected: All tests pass.

**Step 5: Commit**

```bash
git add AIOMarketMaker.Tests/Unit/Services/ListingIndexingService_UnitTests.cs
git commit -m "test: add metadata-update, skip, and metadata-fields tests for ListingIndexingService"
```

---

## Task 6: Integrate `IListingIndexingService` into `ListingProcessorService`

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ListingProcessorService.cs`
- Modify: `AIOMarketMaker.Tests/Unit/Services/ListingProcessorService_UnitTests.cs`

**Context:**
- `ListingProcessorService` constructor currently takes: `BlobServiceClient`, `EtlDbContext`, `IListingParser`, `IScrapeRunCounterService`, `ILogger`
- Add `IListingIndexingService` as a new dependency
- Call `_indexingService.Index(result.Listing, isNew: !result.IsUpdate)` in `UpsertAndRecordHistory` after `CreateStatusHistory` and before `_counterService.Increment`
- The `UpsertResult` record already has `IsUpdate` field
- Existing test `Should_add_new_listing_and_create_initial_history` creates the service via `CreateService()` helper — must add the mock there

**Step 1: Write failing test — Should_index_new_listing_after_upsert**

Add to `ListingProcessorService_UnitTests.cs`:

```csharp
private Mock<IListingIndexingService> _indexingServiceMock = null!;
```

Update `Setup()`:
```csharp
_indexingServiceMock = new Mock<IListingIndexingService>();
_indexingServiceMock
    .Setup(i => i.Index(It.IsAny<Listing>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
    .ReturnsAsync(new IndexingResult(IndexingAction.Embedded));
```

Update `CreateService()`:
```csharp
private ListingProcessorService CreateService() =>
    new(_blobServiceMock.Object, _dbContext, _listingParserMock.Object,
        _counterServiceMock.Object, _indexingServiceMock.Object, _loggerMock.Object);
```

Add test:
```csharp
[Test]
public async Task Should_index_new_listing_after_upsert()
{
    _dbContext.ScrapeRuns.Add(new ScrapeRun { Id = 1, Status = "Running" });
    _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "test" });
    _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
    {
        ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "NEW1", Status = "Pending"
    });
    await _dbContext.SaveChangesAsync();

    SetupBlobWithContent("<html></html>");
    SetupParserWithListing("NEW1", "New Product", 49.99m, EbayListingStatus.Active);

    var request = new ProcessListingRequest(1, 0, "NEW1", 1, "1/NEW1/listing.html");

    await CreateService().Process(request);

    _indexingServiceMock.Verify(i => i.Index(
        It.Is<Listing>(l => l.ListingId == "NEW1"),
        true, It.IsAny<CancellationToken>()), Times.Once);
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ListingProcessorService_UnitTests.Should_index_new_listing_after_upsert" -v n
```

Expected: FAIL — constructor signature mismatch.

**Step 3: Add IListingIndexingService to ListingProcessorService**

In `AIOMarketMaker.Etl/Services/ListingProcessorService.cs`:

Add field:
```csharp
private readonly IListingIndexingService _indexingService;
```

Update constructor to add parameter (between `_counterService` and `_logger`):
```csharp
public ListingProcessorService(
    BlobServiceClient blobService,
    EtlDbContext dbContext,
    IListingParser listingParser,
    IScrapeRunCounterService counterService,
    IListingIndexingService indexingService,
    ILogger<ListingProcessorService> logger)
```

In `UpsertAndRecordHistory`, add after `CreateStatusHistory(result, parsedListing)` (line 97) and before `_counterService.Increment`:

```csharp
await _indexingService.Index(result.Listing, isNew: !result.IsUpdate);
```

**Step 4: Run tests**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ListingProcessorService_UnitTests" -v n
```

Expected: All tests pass (both existing and new).

**Step 5: Add test — Should_index_updated_listing**

```csharp
[Test]
public async Task Should_index_updated_listing_with_is_new_false()
{
    _dbContext.ScrapeRuns.Add(new ScrapeRun { Id = 1, Status = "Running" });
    _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "test" });
    _dbContext.Listings.Add(new Listing
    {
        ListingId = "UPD1", ScrapeJobId = 1, Title = "Existing Item",
        ListingStatus = "Active", Price = 100m
    });
    _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
    {
        ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "UPD1", Status = "Pending"
    });
    await _dbContext.SaveChangesAsync();

    SetupBlobWithContent("<html></html>");
    SetupParserWithListing("UPD1", "Updated Item", 85m, EbayListingStatus.Active);

    var request = new ProcessListingRequest(1, 0, "UPD1", 1, "1/UPD1/listing.html");

    await CreateService().Process(request);

    _indexingServiceMock.Verify(i => i.Index(
        It.Is<Listing>(l => l.ListingId == "UPD1"),
        false, It.IsAny<CancellationToken>()), Times.Once);
}
```

**Step 6: Run, then commit**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ListingProcessorService_UnitTests" -v n
```

```bash
git add AIOMarketMaker.Etl/Services/ListingProcessorService.cs AIOMarketMaker.Tests/Unit/Services/ListingProcessorService_UnitTests.cs
git commit -m "feat: integrate IListingIndexingService into ListingProcessorService"
```

---

## Task 7: Add test — indexing failure prevents marking listing as Complete

**Files:**
- Modify: `AIOMarketMaker.Tests/Unit/Services/ListingProcessorService_UnitTests.cs`

**Context:**
- When `_indexingService.Index(...)` throws, the method should propagate the exception
- `ScrapeRunListing.Status` should NOT be "Complete" (it was set to "Complete" before `SaveChangesAsync` on line 95, but then `Index` is called after `SaveChangesAsync`)
- Actually — looking at the code more carefully: `MarkScrapeRunListingComplete` (line 94) runs BEFORE `SaveChangesAsync` (line 95). Both happen before `CreateStatusHistory`. So the "Complete" status is already saved to DB before indexing runs.
- **This means the current design has a gap**: if indexing fails after `SaveChangesAsync`, the ScrapeRunListing is already marked Complete.
- **Fix**: Move `MarkScrapeRunListingComplete` + its `SaveChangesAsync` to AFTER the indexing call. Or restructure so the save happens once at the end.

**Important**: The current flow in `UpsertAndRecordHistory` is:
1. `UpsertListing` (entity changes tracked by EF)
2. `MarkScrapeRunListingComplete` (sets status to Complete)
3. `SaveChangesAsync` (persists listing + SRL status)
4. `CreateStatusHistory` (adds history + saves again)
5. NEW: `_indexingService.Index(...)`
6. `_counterService.Increment`

If step 5 fails, the SRL is already Complete. To fix: we need to save the listing first (so it gets an ID for history), then do indexing, then mark complete.

**Revised flow:**
1. `UpsertListing` (entity changes tracked)
2. `SaveChangesAsync` (persist listing — we need the ID for history)
3. `CreateStatusHistory` (adds history + saves)
4. `_indexingService.Index(...)` (embed/metadata update)
5. `MarkScrapeRunListingComplete` + `SaveChangesAsync`
6. `_counterService.Increment`

This way, if indexing fails, the listing and history are saved (good — we don't lose work), but the SRL stays in its previous status (Pending/Processing), so it's retryable.

**Step 1: Write the failing test**

```csharp
[Test]
public async Task Should_not_mark_complete_when_indexing_fails()
{
    _dbContext.ScrapeRuns.Add(new ScrapeRun { Id = 1, Status = "Running" });
    _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "test" });
    _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
    {
        ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "FAIL1", Status = "Pending"
    });
    await _dbContext.SaveChangesAsync();

    SetupBlobWithContent("<html></html>");
    SetupParserWithListing("FAIL1", "Failing Item", 50m, EbayListingStatus.Active);

    _indexingServiceMock
        .Setup(i => i.Index(It.IsAny<Listing>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
        .ThrowsAsync(new HttpRequestException("Pinecone timeout"));

    var request = new ProcessListingRequest(1, 0, "FAIL1", 1, "1/FAIL1/listing.html");

    Assert.ThrowsAsync<HttpRequestException>(() => CreateService().Process(request));

    var srl = await _dbContext.ScrapeRunListings.FirstAsync(s => s.ListingId == "FAIL1");
    Assert.That(srl.Status, Is.Not.EqualTo("Complete"),
        "ScrapeRunListing should NOT be marked Complete when indexing fails");

    // But the listing itself should still be saved (we don't lose parsed data)
    var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.ListingId == "FAIL1");
    Assert.That(listing, Is.Not.Null, "Listing should be persisted even when indexing fails");
}
```

**Step 2: Run test to see it fail**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~Should_not_mark_complete_when_indexing_fails" -v n
```

Expected: FAIL — SRL is marked Complete before indexing runs.

**Step 3: Restructure `UpsertAndRecordHistory`**

Rewrite `UpsertAndRecordHistory` in `ListingProcessorService.cs`:

```csharp
private async Task<ProcessListingResponse> UpsertAndRecordHistory(
    ProcessListingRequest request, ScrapeRunListing? scrapeRunListing, ExtractedEbayListing parsedListing)
{
    var existingListing = await _dbContext.Listings
        .FirstOrDefaultAsync(l => l.ListingId == request.ListingId
                               && l.ScrapeJobId == request.ScrapeJobId);

    var newStatus = parsedListing.listingStatus?.ToString();

    if (existingListing != null && !ListingStatusHelper.CanUpdateStatus(existingListing.ListingStatus, newStatus))
    {
        return await HandleInvalidTransition(request, scrapeRunListing, existingListing.ListingStatus, newStatus);
    }

    var result = UpsertListing(existingListing, parsedListing, request);
    await _dbContext.SaveChangesAsync();

    await CreateStatusHistory(result, parsedListing);
    await _indexingService.Index(result.Listing, isNew: !result.IsUpdate);

    MarkScrapeRunListingComplete(scrapeRunListing);
    await _dbContext.SaveChangesAsync();

    await _counterService.Increment(request.ScrapeRunId, result.Status, newStatus);

    _logger.LogInformation("Processed listing {ListingId} with status {Status}", request.ListingId, result.Status);
    return new ProcessListingResponse(true, result.Status, null);
}
```

**Step 4: Run all ListingProcessorService tests**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ListingProcessorService_UnitTests" -v n
```

Expected: All tests pass.

**Step 5: Commit**

```bash
git add AIOMarketMaker.Etl/Services/ListingProcessorService.cs AIOMarketMaker.Tests/Unit/Services/ListingProcessorService_UnitTests.cs
git commit -m "fix: move ScrapeRunListing completion after indexing to support retry on failure"
```

---

## Task 8: Integrate `IListingIndexingService` into `ScrapeJobProcessor.UpdateListingsFromSummary`

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs`
- Modify: `AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs`

**Context:**
- `ScrapeJobProcessor` constructor currently takes: `ILogger`, `EtlDbContext`, `IWebscraperClient`, `ISearchParser`, `IEbayUrlBuilder`
- Add `IListingIndexingService` as a new dependency
- After `SaveChangesAsync()` in `UpdateListingsFromSummary` (line 225), loop over updated listings and call `Index(listing, isNew: false)`
- Need to track which listings were actually updated (price/shipping changed) during the first loop
- Existing tests use `CreateProcessor()` helper — must add the mock there

**Step 1: Write failing test — Should_update_pinecone_metadata_for_changed_listings**

Add to `ScrapeJobProcessor_UnitTests.cs`:

```csharp
private Mock<IListingIndexingService> _indexingServiceMock = null!;
```

Update `Setup()`:
```csharp
_indexingServiceMock = new Mock<IListingIndexingService>();
_indexingServiceMock
    .Setup(i => i.Index(It.IsAny<Listing>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
    .ReturnsAsync(new IndexingResult(IndexingAction.MetadataUpdated));
```

Update `CreateProcessor()`:
```csharp
private ScrapeJobProcessor CreateProcessor() => new(
    _loggerMock.Object, _dbContext, _webscraperClientMock.Object,
    _searchParserMock.Object, _urlBuilderMock.Object, _indexingServiceMock.Object);
```

Add test:
```csharp
[Test]
public async Task Should_update_pinecone_metadata_for_changed_listings()
{
    CreateAndSeedScrapeRun();

    _dbContext.Listings.Add(new Listing
    {
        ListingId = "IDX1", ScrapeJobId = 1,
        Title = "Indexed Item", ListingStatus = "Active",
        Price = 100m, Condition = "USED", ShippingCost = 5m
    });
    await _dbContext.SaveChangesAsync();

    var summary = CreateSummary("IDX1", price: 80m, isSold: false);

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

    _indexingServiceMock.Verify(i => i.Index(
        It.Is<Listing>(l => l.ListingId == "IDX1"),
        false, It.IsAny<CancellationToken>()), Times.Once);
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~Should_update_pinecone_metadata_for_changed_listings" -v n
```

Expected: FAIL — constructor signature mismatch.

**Step 3: Add IListingIndexingService to ScrapeJobProcessor**

In `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs`:

Add field:
```csharp
private readonly IListingIndexingService _indexingService;
```

Update constructor:
```csharp
public ScrapeJobProcessor(
    ILogger<ScrapeJobProcessor> logger,
    EtlDbContext dbContext,
    IWebscraperClient webscraperClient,
    ISearchParser searchParser,
    IEbayUrlBuilder urlBuilder,
    IListingIndexingService indexingService)
```

Modify `UpdateListingsFromSummary` to track updated listings and index them after save:

```csharp
private async Task UpdateListingsFromSummary(
    ScrapeRun scrapeRun, List<IEbayProductSummary> summaries,
    Dictionary<string, Listing> existingListings)
{
    await SetPhase(scrapeRun, "Updating from summary");

    var updatedListings = new List<Listing>();

    foreach (var summary in summaries)
    {
        if (string.IsNullOrEmpty(summary.ListingId)) continue;
        if (!existingListings.TryGetValue(summary.ListingId, out var listing)) continue;

        var priceChanged = listing.Price != summary.Price;
        var shippingChanged = listing.ShippingCost != summary.ShippingCost;

        if (priceChanged || shippingChanged)
        {
            listing.Price = summary.Price;
            listing.ShippingCost = summary.ShippingCost;
            listing.UpdatedUtc = DateTime.UtcNow;

            if (priceChanged)
            {
                _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                {
                    ListingId = listing.Id,
                    ListingStatus = listing.ListingStatus ?? "Active",
                    Price = summary.Price,
                    RecordedUtc = DateTime.UtcNow,
                    Source = "SummaryUpdate"
                });
            }

            scrapeRun.ListingsUpdated++;
            updatedListings.Add(listing);
            _logger.LogInformation("Updated listing {ListingId} from summary (price: {Price}, shipping: {Shipping})",
                summary.ListingId, summary.Price, summary.ShippingCost);
        }
        else
        {
            scrapeRun.ListingsSkipped++;
        }

        scrapeRun.ListingsProcessed++;
    }

    await _dbContext.SaveChangesAsync();

    foreach (var listing in updatedListings)
    {
        await _indexingService.Index(listing, isNew: false);
    }

    _logger.LogInformation("Updated {Count} listings from summary data", summaries.Count);
}
```

**Step 4: Run all ScrapeJobProcessor tests**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ScrapeJobProcessor_UnitTests" -v n
```

Expected: All tests pass.

**Step 5: Add test — Should_not_update_pinecone_for_unchanged_listings**

```csharp
[Test]
public async Task Should_not_update_pinecone_for_unchanged_listings()
{
    CreateAndSeedScrapeRun();

    _dbContext.Listings.Add(new Listing
    {
        ListingId = "NOIDX1", ScrapeJobId = 1,
        Title = "Unchanged Item", ListingStatus = "Active",
        Price = 100m, Condition = "USED", ShippingCost = 5m
    });
    await _dbContext.SaveChangesAsync();

    var summary = CreateSummary("NOIDX1", price: 100m, isSold: false, shippingCost: 5m);

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

    _indexingServiceMock.Verify(i => i.Index(
        It.IsAny<Listing>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
}
```

**Step 6: Run, then commit**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ScrapeJobProcessor_UnitTests" -v n
```

```bash
git add AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs
git commit -m "feat: integrate IListingIndexingService into ScrapeJobProcessor summary updates"
```

---

## Task 9: Register `IListingIndexingService` in DI

**Files:**
- Modify: `AIOMarketMaker.Etl/Program.cs`

**Context:**
- `IListingIndexingService` depends on `IEmbeddingService` and `IPineconeIndexClient`
- Both are registered conditionally (only when API keys are configured)
- `ListingIndexingService` should also be conditional — only register when both OpenAI and Pinecone are configured
- `ListingProcessorService` and `ScrapeJobProcessor` are registered as Scoped
- `ListingIndexingService` should be Scoped too (it depends on Scoped via `ListingProcessorService`)
- Actually: `IEmbeddingService` and `IPineconeIndexClient` are Singleton. `ListingIndexingService` has no scoped dependencies itself, so Singleton is fine.
- **But**: `ListingProcessorService` takes `IListingIndexingService` and is Scoped. A Scoped service CAN depend on a Singleton. So register as Singleton.
- **Problem**: What happens when OpenAI/Pinecone keys aren't configured? `ListingProcessorService` requires `IListingIndexingService` in its constructor. If it's not registered, DI will throw at runtime.
- **Solution**: Create a `NullListingIndexingService` that returns `Skipped` for all calls. Register it as a fallback when keys aren't configured.

**Step 1: Add NullListingIndexingService**

In `AIOMarketMaker.Core/Services/ListingIndexingService.cs`, add after the `ListingIndexingService` class:

```csharp
public class NullListingIndexingService : IListingIndexingService
{
    public Task<IndexingResult> Index(Listing listing, bool isNew, CancellationToken ct = default)
        => Task.FromResult(new IndexingResult(IndexingAction.Skipped));
}
```

**Step 2: Register in Program.cs**

In `AIOMarketMaker.Etl/Program.cs`, inside the Pinecone `if` block (after `services.AddSingleton<ISemanticSearchService, SemanticSearchService>();` on line 151), add:

```csharp
services.AddSingleton<IListingIndexingService, ListingIndexingService>();
```

After the Pinecone `if` block closes (line 152), add an else for the fallback:

Actually, looking at the code, the Embedding and Pinecone `if` blocks are separate. `ListingIndexingService` needs BOTH. The cleanest approach: register after both blocks, checking if both are available:

After line 152 (end of Pinecone block), add:

```csharp
// Listing indexing service - requires both embedding and Pinecone
if (!string.IsNullOrEmpty(openAiKey) && !string.IsNullOrEmpty(pineconeApiKey))
{
    services.AddSingleton<IListingIndexingService, ListingIndexingService>();
}
else
{
    services.AddSingleton<IListingIndexingService, NullListingIndexingService>();
}
```

Note: `openAiKey` is declared on line 120, `pineconeApiKey` on line 137 — both are in scope.

**Step 3: Build and verify**

```bash
cd <REPO_ROOT>/worktrees/AIOMarketMaker/etl-embedding-stage
dotnet build AIOMarketMaker.sln
```

Expected: Clean build.

**Step 4: Run all unit tests**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter Category=Unit -v n
```

Expected: All unit tests pass.

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/ListingIndexingService.cs AIOMarketMaker.Etl/Program.cs
git commit -m "feat: register IListingIndexingService in DI with NullListingIndexingService fallback"
```

---

## Task 10: Full build + all unit tests

**Files:** None (verification only)

**Step 1: Clean build**

```bash
cd <REPO_ROOT>/worktrees/AIOMarketMaker/etl-embedding-stage
dotnet build AIOMarketMaker.sln --no-incremental
```

Expected: 0 errors, 0 warnings (or only pre-existing warnings).

**Step 2: Run all unit tests**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter Category=Unit -v n
```

Expected: All tests pass.

**Step 3: Run full test suite (excluding Explicit)**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj -v n
```

Expected: All non-explicit tests pass.

**Step 4: Review git log**

```bash
git log --oneline -10
```

Verify all commits are present and messages are clear.
