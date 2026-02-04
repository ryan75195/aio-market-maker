# Comparables Pricing ETL — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a standalone ETL that cross-matches listings via Pinecone semantic search + LLM pairwise verification, caching verdicts and pre-computing pricing predictions.

**Architecture:** Standalone CLI command (`--comparables` / `--comparables --dry-run`) loads listings from SQL, queries Pinecone for candidates, checks a verdict cache table, fires gpt-5-nano calls for uncached pairs, stores verdicts, then aggregates pricing predictions. Two new DB tables: `ListingRelationships` (verdict cache) and `ListingPredictions` (pre-computed aggregates).

**Tech Stack:** .NET 8, EF Core, OpenAI SDK v2.8.0 (ChatClient), Pinecone v4.0.2, NUnit + Moq

**Design doc:** `docs/plans/2026-02-04-comparables-pricing-design.md`

---

## Task 1: Database Migration — Drop Old Table, Create New Tables

**Files:**
- Create: `AIOMarketMaker.Core/Data/Migrations/SqlServer/033_DropComparablesCreateRelationshipsAndPredictions.sql`

**Step 1: Write the migration SQL**

```sql
-- Migration: 033_DropComparablesCreateRelationshipsAndPredictions
-- Description: Drops ListingPricingComparables, creates ListingRelationships and ListingPredictions
-- Date: 2026-02-04

-- Drop the superseded table
IF EXISTS (SELECT * FROM sys.tables WHERE name = 'ListingPricingComparables')
BEGIN
    DROP TABLE ListingPricingComparables;
END

-- Create ListingRelationships (LLM verdict cache)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ListingRelationships')
BEGIN
    CREATE TABLE ListingRelationships (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ListingIdA INT NOT NULL,
        ListingIdB INT NOT NULL,
        IsComparable BIT NOT NULL,
        Explanation NVARCHAR(500) NOT NULL,
        SimilarityScore FLOAT NOT NULL,
        CreatedUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_ListingRelationships_ListingA FOREIGN KEY (ListingIdA) REFERENCES Listings(Id),
        CONSTRAINT FK_ListingRelationships_ListingB FOREIGN KEY (ListingIdB) REFERENCES Listings(Id),
        CONSTRAINT UQ_ListingRelationships_Pair UNIQUE (ListingIdA, ListingIdB),
        CONSTRAINT CK_ListingRelationships_Order CHECK (ListingIdA < ListingIdB)
    );
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ListingRelationships_ListingIdA')
BEGIN
    CREATE INDEX IX_ListingRelationships_ListingIdA ON ListingRelationships (ListingIdA);
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ListingRelationships_ListingIdB')
BEGIN
    CREATE INDEX IX_ListingRelationships_ListingIdB ON ListingRelationships (ListingIdB);
END

-- Create ListingPredictions (pre-computed pricing aggregates)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ListingPredictions')
BEGIN
    CREATE TABLE ListingPredictions (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ListingId INT NOT NULL,
        AverageSoldPrice DECIMAL(18,2) NOT NULL,
        SimilarSoldCount INT NOT NULL,
        EstimatedDaysToSell INT NULL,
        PotentialProfit DECIMAL(18,2) NULL,
        ComputedUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_ListingPredictions_Listing FOREIGN KEY (ListingId) REFERENCES Listings(Id),
        CONSTRAINT UQ_ListingPredictions_ListingId UNIQUE (ListingId)
    );
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ListingPredictions_ListingId')
BEGIN
    CREATE INDEX IX_ListingPredictions_ListingId ON ListingPredictions (ListingId);
END
```

**Step 2: Rebuild Core project to embed the migration**

Run: `dotnet build AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

**Step 3: Verify migration applies locally**

Run: `dotnet run --project AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Startup logs show migration 033 applied. Then Ctrl+C to stop.

Verify with:
```bash
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "SELECT name FROM sys.tables WHERE name IN ('ListingRelationships', 'ListingPredictions', 'ListingPricingComparables')" -W
```
Expected: `ListingRelationships` and `ListingPredictions` present. `ListingPricingComparables` gone.

**Step 4: Commit**

```bash
git add AIOMarketMaker.Core/Data/Migrations/SqlServer/033_DropComparablesCreateRelationshipsAndPredictions.sql
git commit -m "feat: add migration 033 - drop ListingPricingComparables, create ListingRelationships and ListingPredictions"
```

---

## Task 2: EF Core Models and DbContext Configuration

**Files:**
- Create: `AIOMarketMaker.Core/Data/Models/ListingRelationship.cs`
- Create: `AIOMarketMaker.Core/Data/Models/ListingPrediction.cs`
- Modify: `AIOMarketMaker.Core/Data/EtlDbContext.cs`
- Delete content from: `AIOMarketMaker.Core/Data/Models/ListingPricingComparable.cs` (remove file)

**Step 1: Create ListingRelationship model**

Create `AIOMarketMaker.Core/Data/Models/ListingRelationship.cs`:
```csharp
namespace AIOMarketMaker.Core.Data.Models;

public class ListingRelationship
{
    public int Id { get; set; }
    public int ListingIdA { get; set; }
    public int ListingIdB { get; set; }
    public bool IsComparable { get; set; }
    public string Explanation { get; set; } = string.Empty;
    public double SimilarityScore { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public Listing ListingA { get; set; } = null!;
    public Listing ListingB { get; set; } = null!;
}
```

**Step 2: Create ListingPrediction model**

Create `AIOMarketMaker.Core/Data/Models/ListingPrediction.cs`:
```csharp
namespace AIOMarketMaker.Core.Data.Models;

public class ListingPrediction
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public decimal AverageSoldPrice { get; set; }
    public int SimilarSoldCount { get; set; }
    public int? EstimatedDaysToSell { get; set; }
    public decimal? PotentialProfit { get; set; }
    public DateTime ComputedUtc { get; set; } = DateTime.UtcNow;

    public Listing Listing { get; set; } = null!;
}
```

**Step 3: Update EtlDbContext**

In `AIOMarketMaker.Core/Data/EtlDbContext.cs`:

Replace the `ListingPricingComparables` DbSet (line 26) with:
```csharp
    public DbSet<ListingRelationship> ListingRelationships { get; set; }
    public DbSet<ListingPrediction> ListingPredictions { get; set; }
```

Replace the `ListingPricingComparables` entity configuration block (lines 145-165) with:
```csharp
        // ListingRelationships
        modelBuilder.Entity<ListingRelationship>(entity =>
        {
            entity.ToTable("ListingRelationships");
            entity.HasIndex(e => new { e.ListingIdA, e.ListingIdB }).IsUnique();
            entity.HasIndex(e => e.ListingIdA);
            entity.HasIndex(e => e.ListingIdB);
            entity.Property(e => e.Explanation).HasMaxLength(500);

            entity.HasOne(e => e.ListingA)
                .WithMany()
                .HasForeignKey(e => e.ListingIdA)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.ListingB)
                .WithMany()
                .HasForeignKey(e => e.ListingIdB)
                .OnDelete(DeleteBehavior.NoAction);
        });

        // ListingPredictions
        modelBuilder.Entity<ListingPrediction>(entity =>
        {
            entity.ToTable("ListingPredictions");
            entity.HasIndex(e => e.ListingId).IsUnique();
            entity.Property(e => e.AverageSoldPrice).HasColumnType("decimal(18,2)");
            entity.Property(e => e.PotentialProfit).HasColumnType("decimal(18,2)");

            entity.HasOne(e => e.Listing)
                .WithMany()
                .HasForeignKey(e => e.ListingId)
                .OnDelete(DeleteBehavior.NoAction);
        });
```

**Step 4: Delete the old model file**

Delete `AIOMarketMaker.Core/Data/Models/ListingPricingComparable.cs`.

Also remove any `using` or reference to `ListingPricingComparable` in the codebase. Search for usages — the `ComparablesRefreshService` references it but we'll replace that service entirely in Task 5.

**Step 5: Build to verify**

Run: `dotnet build AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: May have build errors in `ComparablesRefreshService.cs` and tests referencing `ListingPricingComparable`. That's expected — we'll replace those files in subsequent tasks. If other files break, fix them.

**Step 6: Commit**

```bash
git add AIOMarketMaker.Core/Data/Models/ListingRelationship.cs AIOMarketMaker.Core/Data/Models/ListingPrediction.cs AIOMarketMaker.Core/Data/EtlDbContext.cs
git rm AIOMarketMaker.Core/Data/Models/ListingPricingComparable.cs
git commit -m "feat: add ListingRelationship and ListingPrediction models, remove ListingPricingComparable"
```

---

## Task 3: ListingComparisonService (LLM Classification)

**Files:**
- Create: `AIOMarketMaker.Core/Services/ListingComparisonService.cs`
- Test: `AIOMarketMaker.Tests/Unit/Services/ListingComparisonService_UnitTests.cs`

This service wraps a single gpt-5-nano call to classify whether two listings are the same product.

**Step 1: Write the failing tests**

Create `AIOMarketMaker.Tests/Unit/Services/ListingComparisonService_UnitTests.cs`:

```csharp
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using OpenAI.Chat;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ListingComparisonService_UnitTests
{
    private Mock<ChatClient> _chatClientMock = null!;
    private Mock<ILogger<ListingComparisonService>> _loggerMock = null!;
    private ListingComparisonService _service = null!;

    private static Listing CreateListing(int id, string title, decimal price, string condition, string? description = null) =>
        new()
        {
            Id = id,
            ListingId = id.ToString(),
            Title = title,
            Price = price,
            Condition = condition,
            Description = description ?? $"Description for {title}",
            ScrapeJobId = 1
        };

    [Test]
    public void Should_build_prompt_containing_both_listing_details()
    {
        var listingA = CreateListing(1, "iPhone 15 Pro 256GB", 899.99m, "New");
        var listingB = CreateListing(2, "Apple iPhone 15 Pro 256GB Black", 879.00m, "New");

        var prompt = ListingComparisonService.BuildPrompt(listingA, listingB);

        Assert.Multiple(() =>
        {
            Assert.That(prompt, Does.Contain("iPhone 15 Pro 256GB"));
            Assert.That(prompt, Does.Contain("Apple iPhone 15 Pro 256GB Black"));
            Assert.That(prompt, Does.Contain("899.99"));
            Assert.That(prompt, Does.Contain("879"));
            Assert.That(prompt, Does.Contain("New"));
        });
    }

    [Test]
    public void Should_parse_comparable_true_response()
    {
        var json = """{"isComparable": true, "explanation": "Both are iPhone 15 Pro 256GB in new condition"}""";

        var result = ListingComparisonService.ParseResponse(json);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.True);
            Assert.That(result.Explanation, Is.EqualTo("Both are iPhone 15 Pro 256GB in new condition"));
        });
    }

    [Test]
    public void Should_parse_comparable_false_response()
    {
        var json = """{"isComparable": false, "explanation": "Different storage capacities: 256GB vs 128GB"}""";

        var result = ListingComparisonService.ParseResponse(json);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.False);
            Assert.That(result.Explanation, Is.EqualTo("Different storage capacities: 256GB vs 128GB"));
        });
    }

    [Test]
    public void Should_truncate_explanation_to_500_characters()
    {
        var longExplanation = new string('x', 600);
        var json = $$$"""{"isComparable": true, "explanation": "{{{longExplanation}}}"}""";

        var result = ListingComparisonService.ParseResponse(json);

        Assert.That(result.Explanation.Length, Is.EqualTo(500));
    }

    [Test]
    public void Should_handle_malformed_json_gracefully()
    {
        var badJson = "not valid json at all";

        var result = ListingComparisonService.ParseResponse(badJson);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.False);
            Assert.That(result.Explanation, Does.Contain("Failed to parse"));
        });
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ListingComparisonService_UnitTests"`
Expected: FAIL — `ListingComparisonService` does not exist

**Step 3: Write the implementation**

Create `AIOMarketMaker.Core/Services/ListingComparisonService.cs`:

```csharp
using System.Text.Json;
using AIOMarketMaker.Core.Data.Models;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace AIOMarketMaker.Core.Services;

public record ComparableVerdict(bool IsComparable, string Explanation);

public record ListingComparisonConfig(string ApiKey, string Model = "gpt-5-nano");

public interface IListingComparisonService
{
    Task<ComparableVerdict> Compare(Listing a, Listing b, CancellationToken ct = default);
}

public class ListingComparisonService : IListingComparisonService
{
    private readonly ChatClient _client;
    private readonly ILogger<ListingComparisonService> _logger;

    public ListingComparisonService(ListingComparisonConfig config, ILogger<ListingComparisonService> logger)
    {
        _client = new ChatClient(config.Model, config.ApiKey);
        _logger = logger;
    }

    public async Task<ComparableVerdict> Compare(Listing a, Listing b, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(a, b);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You are a product comparison expert. Respond only with valid JSON."),
            new UserChatMessage(prompt)
        };

        var completion = await _client.CompleteChatAsync(messages, cancellationToken: ct);
        var responseText = completion.Value.Content[0].Text;

        _logger.LogDebug("LLM response for pair ({IdA}, {IdB}): {Response}", a.Id, b.Id, responseText);

        return ParseResponse(responseText);
    }

    public static string BuildPrompt(Listing a, Listing b)
    {
        return $"""
            You are comparing two eBay listings to determine if they are the same product for pricing comparison purposes.
            Two listings are "comparable" if a buyer would consider them interchangeable — same product, same model, same key specs.
            Minor differences (color, seller, bundled accessories) are acceptable.

            Listing A:
            - Title: {a.Title}
            - Price: {a.Price}
            - Condition: {a.Condition}
            - Description: {a.Description}

            Listing B:
            - Title: {b.Title}
            - Price: {b.Price}
            - Condition: {b.Condition}
            - Description: {b.Description}

            Are these the same product (suitable for comparing prices)?
            Respond with JSON only: {{"isComparable": true/false, "explanation": "brief reason"}}
            """;
    }

    public static ComparableVerdict ParseResponse(string responseText)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;

            var isComparable = root.GetProperty("isComparable").GetBoolean();
            var explanation = root.GetProperty("explanation").GetString() ?? "";

            if (explanation.Length > 500)
            {
                explanation = explanation[..500];
            }

            return new ComparableVerdict(isComparable, explanation);
        }
        catch (Exception ex)
        {
            return new ComparableVerdict(false, $"Failed to parse LLM response: {ex.Message}");
        }
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ListingComparisonService_UnitTests"`
Expected: All 5 tests PASS

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/ListingComparisonService.cs AIOMarketMaker.Tests/Unit/Services/ListingComparisonService_UnitTests.cs
git commit -m "feat: add ListingComparisonService with gpt-5-nano LLM classification"
```

---

## Task 4: ComparablesEtlService — Core Pipeline

**Files:**
- Create: `AIOMarketMaker.Core/Services/ComparablesEtlService.cs`
- Test: `AIOMarketMaker.Tests/Unit/Services/ComparablesEtlService_UnitTests.cs`

This is the main orchestrator. It loads listings, queries Pinecone, checks the verdict cache, calls the LLM for uncached pairs, stores verdicts, and computes aggregates.

**Step 1: Write the failing tests**

Create `AIOMarketMaker.Tests/Unit/Services/ComparablesEtlService_UnitTests.cs`:

```csharp
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Pinecone;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ComparablesEtlService_UnitTests
{
    private EtlDbContext _dbContext = null!;
    private Mock<ISemanticSearchService> _searchMock = null!;
    private Mock<IListingComparisonService> _comparisonMock = null!;
    private Mock<ILogger<ComparablesEtlService>> _loggerMock = null!;
    private ComparablesEtlService _service = null!;

    [SetUp]
    public void Setup()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _searchMock = new Mock<ISemanticSearchService>();
        _comparisonMock = new Mock<IListingComparisonService>();
        _loggerMock = new Mock<ILogger<ComparablesEtlService>>();
        _service = new ComparablesEtlService(
            _searchMock.Object,
            _comparisonMock.Object,
            _dbContext,
            _loggerMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    private Listing SeedListing(int id, string title, string status, decimal price = 100m)
    {
        var job = _dbContext.ScrapeJobs.FirstOrDefault() ?? _dbContext.ScrapeJobs.Add(new ScrapeJob
        {
            Id = 1,
            SearchTerm = "test"
        }).Entity;
        _dbContext.SaveChanges();

        var listing = new Listing
        {
            Id = id,
            ListingId = id.ToString(),
            Title = title,
            ListingStatus = status,
            Price = price,
            Condition = "New",
            Description = $"Description for {title}",
            ScrapeJobId = job.Id
        };
        _dbContext.Listings.Add(listing);
        _dbContext.SaveChanges();
        return listing;
    }

    private void MockPineconeResult(string queryListingId, params (string listingId, double score)[] results)
    {
        var scoredVectors = results.Select(r =>
            new ScoredVector
            {
                Id = r.listingId,
                Score = (float)r.score,
                Metadata = new Metadata { ["listingId"] = r.listingId }
            }).ToList();

        _searchMock.Setup(s => s.FindSimilar(
                queryListingId,
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<Metadata?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResult(scoredVectors));
    }

    [Test]
    public async Task Should_call_llm_for_uncached_pair_and_store_verdict()
    {
        var active = SeedListing(1, "iPhone 15 Pro", "Active", 800m);
        var sold = SeedListing(2, "iPhone 15 Pro 256GB", "Sold", 850m);

        MockPineconeResult("1", ("2", 0.92));

        _comparisonMock.Setup(c => c.Compare(
                It.Is<Listing>(l => l.Id == 1),
                It.Is<Listing>(l => l.Id == 2),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComparableVerdict(true, "Same product"));

        _searchMock.Setup(s => s.Exists(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _service.Run(dryRun: false);

        var verdict = _dbContext.ListingRelationships.SingleOrDefault();
        Assert.Multiple(() =>
        {
            Assert.That(verdict, Is.Not.Null);
            Assert.That(verdict!.ListingIdA, Is.EqualTo(1));
            Assert.That(verdict.ListingIdB, Is.EqualTo(2));
            Assert.That(verdict.IsComparable, Is.True);
            Assert.That(verdict.Explanation, Is.EqualTo("Same product"));
            Assert.That(result.LlmCallsMade, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Should_skip_llm_call_when_verdict_already_cached()
    {
        var active = SeedListing(1, "iPhone 15 Pro", "Active", 800m);
        var sold = SeedListing(2, "iPhone 15 Pro 256GB", "Sold", 850m);

        _dbContext.ListingRelationships.Add(new ListingRelationship
        {
            ListingIdA = 1,
            ListingIdB = 2,
            IsComparable = true,
            Explanation = "Already evaluated",
            SimilarityScore = 0.92
        });
        _dbContext.SaveChanges();

        MockPineconeResult("1", ("2", 0.92));

        _searchMock.Setup(s => s.Exists(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _service.Run(dryRun: false);

        _comparisonMock.Verify(
            c => c.Compare(It.IsAny<Listing>(), It.IsAny<Listing>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.That(result.CacheHits, Is.EqualTo(1));
    }

    [Test]
    public async Task Should_use_canonical_ordering_for_verdict_storage()
    {
        // Listing 5 finds listing 3 — should store as (3, 5) not (5, 3)
        var active = SeedListing(5, "Samsung Galaxy S24", "Active", 700m);
        var sold = SeedListing(3, "Galaxy S24 Ultra", "Sold", 750m);

        MockPineconeResult("5", ("3", 0.88));

        _comparisonMock.Setup(c => c.Compare(
                It.IsAny<Listing>(),
                It.IsAny<Listing>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComparableVerdict(true, "Same phone"));

        _searchMock.Setup(s => s.Exists(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _service.Run(dryRun: false);

        var verdict = _dbContext.ListingRelationships.Single();
        Assert.Multiple(() =>
        {
            Assert.That(verdict.ListingIdA, Is.EqualTo(3));
            Assert.That(verdict.ListingIdB, Is.EqualTo(5));
        });
    }

    [Test]
    public async Task Should_compute_predictions_from_comparable_sold_listings()
    {
        var active = SeedListing(1, "iPhone 15 Pro", "Active", 800m);
        var sold1 = SeedListing(2, "iPhone 15 Pro", "Sold", 900m);
        var sold2 = SeedListing(3, "iPhone 15 Pro", "Sold", 850m);

        // Add status history with sold dates for the sold listings
        _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
        {
            ListingId = 2,
            ListingStatus = "Sold",
            SoldDateUtc = DateTime.UtcNow.AddDays(-5),
            RecordedUtc = DateTime.UtcNow,
            Price = 900m
        });
        _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
        {
            ListingId = 3,
            ListingStatus = "Sold",
            SoldDateUtc = DateTime.UtcNow.AddDays(-10),
            RecordedUtc = DateTime.UtcNow,
            Price = 850m
        });
        _dbContext.SaveChanges();

        // Pre-populate verdicts (simulating step 4 already done)
        _dbContext.ListingRelationships.Add(new ListingRelationship
        {
            ListingIdA = 1, ListingIdB = 2,
            IsComparable = true, Explanation = "Same", SimilarityScore = 0.9
        });
        _dbContext.ListingRelationships.Add(new ListingRelationship
        {
            ListingIdA = 1, ListingIdB = 3,
            IsComparable = true, Explanation = "Same", SimilarityScore = 0.88
        });
        _dbContext.SaveChanges();

        // No new Pinecone results needed — just testing aggregation
        _searchMock.Setup(s => s.Exists(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        MockPineconeResult("1");

        await _service.Run(dryRun: false);

        var prediction = _dbContext.ListingPredictions.SingleOrDefault(p => p.ListingId == 1);
        Assert.Multiple(() =>
        {
            Assert.That(prediction, Is.Not.Null);
            Assert.That(prediction!.AverageSoldPrice, Is.EqualTo(875m));
            Assert.That(prediction.SimilarSoldCount, Is.EqualTo(2));
            Assert.That(prediction.PotentialProfit, Is.EqualTo(75m));
        });
    }

    [Test]
    public async Task Should_report_counts_without_making_llm_calls_in_dry_run()
    {
        var active = SeedListing(1, "iPhone 15 Pro", "Active", 800m);
        var sold = SeedListing(2, "iPhone 15 Pro 256GB", "Sold", 850m);

        MockPineconeResult("1", ("2", 0.92));

        _searchMock.Setup(s => s.Exists(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _service.Run(dryRun: true);

        _comparisonMock.Verify(
            c => c.Compare(It.IsAny<Listing>(), It.IsAny<Listing>(), It.IsAny<CancellationToken>()),
            Times.Never);

        Assert.Multiple(() =>
        {
            Assert.That(result.LlmCallsRequired, Is.EqualTo(1));
            Assert.That(result.LlmCallsMade, Is.EqualTo(0));
            Assert.That(result.CacheHits, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Should_skip_listings_not_indexed_in_pinecone()
    {
        var active = SeedListing(1, "iPhone 15 Pro", "Active", 800m);

        _searchMock.Setup(s => s.Exists("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _service.Run(dryRun: false);

        Assert.That(result.ListingsProcessed, Is.EqualTo(0));
        _searchMock.Verify(
            s => s.FindSimilar(It.IsAny<string>(), It.IsAny<IEnumerable<string>?>(),
                It.IsAny<Metadata?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ComparablesEtlService_UnitTests"`
Expected: FAIL — `ComparablesEtlService` does not exist

**Step 3: Write the implementation**

Create `AIOMarketMaker.Core/Services/ComparablesEtlService.cs`:

```csharp
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pinecone;

namespace AIOMarketMaker.Core.Services;

public record ComparablesEtlResult(
    int ListingsProcessed,
    int PineconeQueries,
    int CandidatePairsFound,
    int CacheHits,
    int LlmCallsRequired,
    int LlmCallsMade,
    int ComparablesFound,
    int PredictionsWritten);

public interface IComparablesEtlService
{
    Task<ComparablesEtlResult> Run(bool dryRun, CancellationToken ct = default);
}

public class ComparablesEtlService : IComparablesEtlService
{
    private const int PineconeTopK = 50;
    private const int MaxPineconeConcurrency = 10;
    private const int MaxLlmConcurrency = 20;

    private readonly ISemanticSearchService _search;
    private readonly IListingComparisonService _comparison;
    private readonly EtlDbContext _db;
    private readonly ILogger<ComparablesEtlService> _logger;

    public ComparablesEtlService(
        ISemanticSearchService search,
        IListingComparisonService comparison,
        EtlDbContext db,
        ILogger<ComparablesEtlService> logger)
    {
        _search = search;
        _comparison = comparison;
        _db = db;
        _logger = logger;
    }

    public async Task<ComparablesEtlResult> Run(bool dryRun, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting comparables ETL (dryRun={DryRun})", dryRun);

        // Step 1: Load all listings
        var allListings = await _db.Listings
            .Include(l => l.StatusHistory)
            .ToListAsync(ct);

        // Step 2: Filter to listings indexed in Pinecone
        var indexedListings = await FilterToIndexed(allListings, ct);
        _logger.LogInformation("Found {Count} indexed listings out of {Total}", indexedListings.Count, allListings.Count);

        if (indexedListings.Count == 0)
        {
            return new ComparablesEtlResult(0, 0, 0, 0, 0, 0, 0, 0);
        }

        // Step 3: Query Pinecone for candidates
        var listingMap = allListings.ToDictionary(l => l.ListingId, l => l);
        var candidatePairs = await QueryPineconeCandidates(indexedListings, listingMap, ct);
        _logger.LogInformation("Found {Count} candidate pairs from Pinecone", candidatePairs.Count);

        // Step 4: Check verdict cache
        var existingVerdicts = await LoadExistingVerdicts(ct);
        var uncachedPairs = candidatePairs
            .Where(p => !existingVerdicts.Contains((p.IdA, p.IdB)))
            .ToList();

        var cacheHits = candidatePairs.Count - uncachedPairs.Count;
        _logger.LogInformation("Cache hits: {Hits}, new pairs to evaluate: {New}", cacheHits, uncachedPairs.Count);

        if (dryRun)
        {
            _logger.LogInformation("DRY RUN — would make {Count} LLM calls", uncachedPairs.Count);
            return new ComparablesEtlResult(
                ListingsProcessed: indexedListings.Count,
                PineconeQueries: indexedListings.Count,
                CandidatePairsFound: candidatePairs.Count,
                CacheHits: cacheHits,
                LlmCallsRequired: uncachedPairs.Count,
                LlmCallsMade: 0,
                ComparablesFound: 0,
                PredictionsWritten: 0);
        }

        // Step 5: LLM classification
        var llmCallsMade = 0;
        var comparablesFound = 0;
        var semaphore = new SemaphoreSlim(MaxLlmConcurrency);

        var tasks = uncachedPairs.Select(async pair =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var listingA = allListings.First(l => l.Id == pair.IdA);
                var listingB = allListings.First(l => l.Id == pair.IdB);

                var verdict = await _comparison.Compare(listingA, listingB, ct);
                Interlocked.Increment(ref llmCallsMade);

                if (verdict.IsComparable)
                {
                    Interlocked.Increment(ref comparablesFound);
                }

                _db.ListingRelationships.Add(new ListingRelationship
                {
                    ListingIdA = pair.IdA,
                    ListingIdB = pair.IdB,
                    IsComparable = verdict.IsComparable,
                    Explanation = verdict.Explanation,
                    SimilarityScore = pair.Score,
                    CreatedUtc = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to evaluate pair ({IdA}, {IdB})", pair.IdA, pair.IdB);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("LLM calls made: {Made}, comparables found: {Found}", llmCallsMade, comparablesFound);

        // Step 6: Compute and upsert predictions
        var predictionsWritten = await ComputePredictions(ct);

        return new ComparablesEtlResult(
            ListingsProcessed: indexedListings.Count,
            PineconeQueries: indexedListings.Count,
            CandidatePairsFound: candidatePairs.Count,
            CacheHits: cacheHits,
            LlmCallsRequired: uncachedPairs.Count,
            LlmCallsMade: llmCallsMade,
            ComparablesFound: comparablesFound,
            PredictionsWritten: predictionsWritten);
    }

    private async Task<List<Listing>> FilterToIndexed(IEnumerable<Listing> listings, CancellationToken ct)
    {
        var result = new List<Listing>();
        foreach (var listing in listings)
        {
            if (await _search.Exists(listing.ListingId, ct))
            {
                result.Add(listing);
            }
        }
        return result;
    }

    private async Task<List<CandidatePair>> QueryPineconeCandidates(
        List<Listing> listings, Dictionary<string, Listing> listingMap, CancellationToken ct)
    {
        var pairs = new HashSet<(int, int)>();
        var result = new List<CandidatePair>();
        var semaphore = new SemaphoreSlim(MaxPineconeConcurrency);

        var tasks = listings.Select(async listing =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var searchResult = await _search.FindSimilar(
                    listing.ListingId, topK: PineconeTopK, ct: ct);

                foreach (var match in searchResult.Matches)
                {
                    var matchListingId = match.Metadata?["listingId"]?.ToString();
                    if (matchListingId == null || !listingMap.TryGetValue(matchListingId, out var matchListing))
                    {
                        continue;
                    }

                    var idA = Math.Min(listing.Id, matchListing.Id);
                    var idB = Math.Max(listing.Id, matchListing.Id);

                    lock (pairs)
                    {
                        if (pairs.Add((idA, idB)))
                        {
                            result.Add(new CandidatePair(idA, idB, match.Score ?? 0));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pinecone query failed for listing {Id}", listing.ListingId);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return result;
    }

    private async Task<HashSet<(int, int)>> LoadExistingVerdicts(CancellationToken ct)
    {
        var verdicts = await _db.ListingRelationships
            .Select(v => new { v.ListingIdA, v.ListingIdB })
            .ToListAsync(ct);

        return verdicts.Select(v => (v.ListingIdA, v.ListingIdB)).ToHashSet();
    }

    private async Task<int> ComputePredictions(CancellationToken ct)
    {
        // Get all comparable verdicts with listing data
        var comparables = await _db.ListingRelationships
            .Where(v => v.IsComparable)
            .ToListAsync(ct);

        // Group by each listing that appears in any comparable pair
        var listingIds = comparables
            .SelectMany(v => new[] { v.ListingIdA, v.ListingIdB })
            .Distinct()
            .ToList();

        var listings = await _db.Listings
            .Where(l => listingIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, ct);

        var statusHistories = await _db.ListingStatusHistory
            .Where(sh => listingIds.Contains(sh.ListingId) && sh.ListingStatus == "Sold")
            .ToListAsync(ct);

        var soldDates = statusHistories
            .Where(sh => sh.SoldDateUtc != null)
            .ToDictionary(sh => sh.ListingId, sh => sh.SoldDateUtc!.Value);

        var count = 0;

        foreach (var listingId in listingIds)
        {
            if (!listings.TryGetValue(listingId, out var listing))
            {
                continue;
            }

            // Find all comparable partner IDs for this listing
            var partnerIds = comparables
                .Where(v => v.ListingIdA == listingId || v.ListingIdB == listingId)
                .Select(v => v.ListingIdA == listingId ? v.ListingIdB : v.ListingIdA)
                .Where(id => listings.ContainsKey(id))
                .ToList();

            // Filter to sold partners with valid prices
            var soldPartners = partnerIds
                .Where(id => listings[id].ListingStatus == "Sold" && listings[id].Price.HasValue)
                .Select(id => listings[id])
                .ToList();

            if (soldPartners.Count == 0)
            {
                continue;
            }

            var avgSoldPrice = soldPartners.Average(p => p.Price!.Value);

            var daysToSell = soldPartners
                .Where(p => soldDates.ContainsKey(p.Id))
                .Select(p => (soldDates[p.Id] - p.CreatedUtc).Days)
                .ToList();

            var avgDaysToSell = daysToSell.Count > 0 ? (int?)Math.Round(daysToSell.Average()) : null;
            var profit = listing.Price.HasValue ? avgSoldPrice - listing.Price.Value : (decimal?)null;

            var existing = await _db.ListingPredictions
                .FirstOrDefaultAsync(p => p.ListingId == listingId, ct);

            if (existing != null)
            {
                existing.AverageSoldPrice = avgSoldPrice;
                existing.SimilarSoldCount = soldPartners.Count;
                existing.EstimatedDaysToSell = avgDaysToSell;
                existing.PotentialProfit = profit;
                existing.ComputedUtc = DateTime.UtcNow;
            }
            else
            {
                _db.ListingPredictions.Add(new ListingPrediction
                {
                    ListingId = listingId,
                    AverageSoldPrice = avgSoldPrice,
                    SimilarSoldCount = soldPartners.Count,
                    EstimatedDaysToSell = avgDaysToSell,
                    PotentialProfit = profit,
                    ComputedUtc = DateTime.UtcNow
                });
            }

            count++;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Wrote {Count} predictions", count);
        return count;
    }

    private record CandidatePair(int IdA, int IdB, double Score);
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ComparablesEtlService_UnitTests"`
Expected: All 6 tests PASS

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/ComparablesEtlService.cs AIOMarketMaker.Tests/Unit/Services/ComparablesEtlService_UnitTests.cs
git commit -m "feat: add ComparablesEtlService with Pinecone + LLM pipeline and dry-run support"
```

---

## Task 5: Clean Up Old ComparablesRefreshService

**Files:**
- Delete: `AIOMarketMaker.Core/Services/ComparablesRefreshService.cs`
- Delete: `AIOMarketMaker.Tests/Unit/Services/ComparablesRefreshService_UnitTests.cs`
- Modify: `AIOMarketMaker.Etl/Program.cs` (remove old registration)

**Step 1: Remove old service and tests**

Delete `AIOMarketMaker.Core/Services/ComparablesRefreshService.cs`.
Delete `AIOMarketMaker.Tests/Unit/Services/ComparablesRefreshService_UnitTests.cs`.

**Step 2: Remove references in Program.cs**

In `AIOMarketMaker.Etl/Program.cs`, remove the line (around line 153-154):
```csharp
services.AddScoped<IComparablesRefreshService, ComparablesRefreshService>();
```

Also remove any `using` statement for `ComparablesRefreshService` or `IComparablesRefreshService`.

**Step 3: Search for remaining references**

Search the codebase for `ComparablesRefresh`, `IComparablesRefreshService`, and `ListingPricingComparable` to ensure no dangling references remain. Fix any found.

**Step 4: Build to verify**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`
Expected: Build succeeded (or only unrelated warnings)

**Step 5: Run all tests**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit"`
Expected: All unit tests pass

**Step 6: Commit**

```bash
git rm AIOMarketMaker.Core/Services/ComparablesRefreshService.cs AIOMarketMaker.Tests/Unit/Services/ComparablesRefreshService_UnitTests.cs
git add AIOMarketMaker.Etl/Program.cs
git commit -m "refactor: remove ComparablesRefreshService, replaced by ComparablesEtlService"
```

---

## Task 6: Wire Up DI and CLI Entrypoint

**Files:**
- Modify: `AIOMarketMaker.Etl/Program.cs`
- Modify: `AIOMarketMaker.Etl/local.settings.json`

**Step 1: Add config key to local.settings.json**

In `AIOMarketMaker.Etl/local.settings.json`, add inside `"Values"`:
```json
"OpenAi:ChatModel": "gpt-5-nano"
```

**Step 2: Wire up DI and CLI handling in Program.cs**

In `AIOMarketMaker.Etl/Program.cs`, add the service registrations alongside the existing Pinecone/Embedding setup:

```csharp
// ListingComparisonService (LLM classification)
var chatModel = config["OpenAi:ChatModel"] ?? "gpt-5-nano";
var comparisonConfig = new ListingComparisonConfig(openAiApiKey, chatModel);
services.AddSingleton(comparisonConfig);
services.AddSingleton<IListingComparisonService, ListingComparisonService>();

// ComparablesEtlService
services.AddScoped<IComparablesEtlService, ComparablesEtlService>();
```

Add CLI argument handling. The existing Program.cs uses `args` — add a check for `--comparables` and `--dry-run`:

After the host is built, before/instead of the default ETL run:
```csharp
if (args.Contains("--comparables"))
{
    using var scope = host.Services.CreateScope();
    var etl = scope.ServiceProvider.GetRequiredService<IComparablesEtlService>();
    var dryRun = args.Contains("--dry-run");
    var result = await etl.Run(dryRun);

    Console.WriteLine();
    Console.WriteLine(dryRun ? "Dry Run Summary" : "Run Summary");
    Console.WriteLine("===============");
    Console.WriteLine($"Listings processed:     {result.ListingsProcessed}");
    Console.WriteLine($"Pinecone queries:       {result.PineconeQueries}");
    Console.WriteLine($"Candidate pairs found:  {result.CandidatePairsFound}");
    Console.WriteLine($"Cache hits:             {result.CacheHits}");
    Console.WriteLine($"LLM calls required:     {result.LlmCallsRequired}");
    Console.WriteLine($"LLM calls made:         {result.LlmCallsMade}");
    Console.WriteLine($"Comparables found:      {result.ComparablesFound}");
    Console.WriteLine($"Predictions written:    {result.PredictionsWritten}");
    return;
}
```

**Step 3: Build to verify**

Run: `dotnet build AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add AIOMarketMaker.Etl/Program.cs AIOMarketMaker.Etl/local.settings.json
git commit -m "feat: wire up comparables ETL with --comparables and --dry-run CLI flags"
```

---

## Task 7: Update GetActiveListings API Endpoint

**Files:**
- Modify: `AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs`

**Step 1: Simplify GetActiveListings to use ListingPredictions**

Replace the complex in-memory aggregation logic in the `GetActiveListings` function (lines ~431-505) with a simple join to `ListingPredictions`:

```csharp
[Function("GetActiveListings")]
public async Task<HttpResponseData> GetActiveListings(
    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "listings/active")]
    HttpRequestData req)
{
    var listings = await _dbContext.Listings
        .Where(l => l.ListingStatus == "Active")
        .GroupJoin(
            _dbContext.ListingPredictions,
            l => l.Id,
            p => p.ListingId,
            (listing, predictions) => new { listing, prediction = predictions.FirstOrDefault() })
        .OrderByDescending(x => x.prediction != null ? x.prediction.PotentialProfit : null)
        .Take(200)
        .Select(x => new OpportunityListing(
            x.listing.Id,
            x.listing.ListingId,
            x.listing.Title,
            x.listing.Price,
            x.listing.Currency,
            x.listing.ShippingCost,
            x.listing.Url,
            x.listing.Condition,
            x.listing.ListingStatus,
            x.listing.EndDateUtc,
            x.listing.CreatedUtc,
            x.listing.ScrapeJob.SearchTerm,
            x.listing.Images,
            x.prediction != null ? x.prediction.AverageSoldPrice : null,
            x.prediction != null ? x.prediction.SimilarSoldCount : 0,
            x.prediction != null ? x.prediction.EstimatedDaysToSell : null,
            x.prediction != null ? x.prediction.PotentialProfit : null))
        .ToListAsync();

    var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
    await response.WriteAsJsonAsync(listings);
    return response;
}
```

**Step 2: Build and verify**

Run: `dotnet build AIOMarketMaker.Functions/AIOMarketMaker.Functions.csproj`
Expected: Build succeeded

**Step 3: Run existing API tests if any**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ScrapeJobsApi"`
Expected: Tests pass (or update if assertions changed)

**Step 4: Commit**

```bash
git add AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs
git commit -m "refactor: simplify GetActiveListings to use pre-computed ListingPredictions"
```

---

## Task 8: Full Build and Test Verification

**Step 1: Build entire solution**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`
Expected: Build succeeded with 0 errors

**Step 2: Run all unit tests**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit"`
Expected: All unit tests pass

**Step 3: Verify migration applies cleanly**

Run against local DB:
```bash
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "SELECT name FROM sys.tables WHERE name IN ('ListingRelationships', 'ListingPredictions', 'ListingPricingComparables')" -W
```
Expected: `ListingRelationships` and `ListingPredictions` present. `ListingPricingComparables` absent.

**Step 4: Smoke test dry-run**

Run: `dotnet run --project AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj -- --comparables --dry-run`
Expected: Outputs a summary table with counts (LLM calls made = 0)

**Step 5: Commit if any fixes were needed**

```bash
git add -A
git commit -m "fix: resolve build/test issues from integration"
```
