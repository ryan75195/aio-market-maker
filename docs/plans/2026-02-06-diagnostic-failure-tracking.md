# Diagnostic Failure Tracking Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Extend ScrapeRunIssues to capture diagnostic context (phase, stack trace, HTTP status) so every failure can be traced to its root cause.

**Architecture:** Add `Phase`, `StackTrace`, and `HttpStatusCode` columns to `ScrapeRunIssues`. Break the catch-all exception handler in `ProcessFetchedDescription` into distinct parse vs embed vs index failures. Each failure path records the exact phase and full exception details.

**Tech Stack:** SQL Server migration, EF Core model, ScrapeJobProcessor changes, NUnit tests

---

## Current State

`ScrapeRunIssues` has: `Id, ScrapeRunId, ListingId, IssueType, ErrorMessage(500), CreatedUtc`

Three failure paths in `ProcessFetchedDescription`:

| Line | Failure | IssueType | What's missing |
|------|---------|-----------|----------------|
| 419 | Listing not in DB | ListingNotFound | No phase info |
| 427 | Scraper HTTP error | DescriptionFetchFailed | No HTTP status, no stack trace |
| 483 | Catch-all exception | ProcessingFailed | No phase (parse? embed? index?), no stack trace |

The catch-all at line 483 is the worst — it covers 3 completely different subsystems (HTML parsing, OpenAI embedding, Pinecone upsert) but gives no indication which one failed.

## Changes

### New columns on ScrapeRunIssues

| Column | Type | Purpose |
|--------|------|---------|
| `Phase` | `NVARCHAR(50) NULL` | Pipeline step: DescriptionFetch, Parse, Embedding, Indexing |
| `StackTrace` | `NVARCHAR(MAX) NULL` | Full exception stack trace |
| `HttpStatusCode` | `INT NULL` | HTTP status for fetch failures (500, 403, etc.) |

### Break up the catch-all

Replace the single `catch (Exception ex)` with specific try/catch blocks around each step:

```
Parse HTML        → IssueType: ParseFailed,     Phase: Parse
Embed via OpenAI  → IssueType: EmbeddingFailed,  Phase: Embedding
Upsert to Pinecone → IssueType: IndexingFailed,  Phase: Indexing
```

### Extract HTTP status from fetch errors

`WebscraperClient.GetPageHtmlAsync` calls `resp.EnsureSuccessStatusCode()` which throws `HttpRequestException`. In .NET 8, `HttpRequestException.StatusCode` is available — capture it.

---

### Task 1: Add migration for new columns

**Files:**
- Create: `AIOMarketMaker.Core/Data/Migrations/SqlServer/035_AddDiagnosticColumnsToScrapeRunIssues.sql`

**Step 1: Write the migration**

```sql
-- Migration: 035_AddDiagnosticColumnsToScrapeRunIssues
-- Description: Adds Phase, StackTrace, and HttpStatusCode columns for failure diagnostics
-- Date: 2026-02-06

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunIssues') AND name = 'Phase')
BEGIN
    ALTER TABLE ScrapeRunIssues ADD Phase NVARCHAR(50) NULL;
END
GO

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunIssues') AND name = 'StackTrace')
BEGIN
    ALTER TABLE ScrapeRunIssues ADD StackTrace NVARCHAR(MAX) NULL;
END
GO

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunIssues') AND name = 'HttpStatusCode')
BEGIN
    ALTER TABLE ScrapeRunIssues ADD HttpStatusCode INT NULL;
END
GO
```

**Step 2: Rebuild Core project to embed the migration**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Data/Migrations/SqlServer/035_AddDiagnosticColumnsToScrapeRunIssues.sql
git commit -m "migration: add diagnostic columns to ScrapeRunIssues"
```

---

### Task 2: Update ScrapeRunIssue model and EF config

**Files:**
- Modify: `AIOMarketMaker.Core/Data/Models/ScrapeRunIssue.cs`
- Modify: `AIOMarketMaker.Core/Data/EtlDbContext.cs:103-118` (ScrapeRunIssue entity config)

**Step 1: Add properties to ScrapeRunIssue model**

Add after `ErrorMessage` property:

```csharp
public string? Phase { get; set; }
public string? StackTrace { get; set; }
public int? HttpStatusCode { get; set; }
```

Remove the XML doc comments from the model — they just restate the property names.

**Step 2: Add EF config for new columns**

In `EtlDbContext.OnModelCreating`, inside the `ScrapeRunIssue` entity block, add:

```csharp
entity.Property(e => e.Phase).HasMaxLength(50);
entity.Property(e => e.ErrorMessage).HasMaxLength(2000); // widen from 500
```

No config needed for `StackTrace` (NVARCHAR(MAX) is the default) or `HttpStatusCode` (INT NULL maps automatically).

Also widen `ErrorMessage` from 500 to 2000 — many exception messages get truncated at 500 chars. The migration should handle this too (add to migration in Task 1):

```sql
IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunIssues') AND name = 'ErrorMessage')
BEGIN
    ALTER TABLE ScrapeRunIssues ALTER COLUMN ErrorMessage NVARCHAR(2000) NULL;
END
GO
```

**Step 3: Build to verify**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Data/Models/ScrapeRunIssue.cs AIOMarketMaker/AIOMarketMaker.Core/Data/EtlDbContext.cs
git commit -m "feat: add diagnostic properties to ScrapeRunIssue model"
```

---

### Task 3: Write failing tests for diagnostic failure tracking

**Files:**
- Modify: `AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_InlineTests.cs`

**Step 1: Write test for fetch failure diagnostics**

```csharp
[Test]
public async Task Should_record_http_status_and_phase_on_fetch_failure()
{
    // Arrange: scraper returns 500
    var httpEx = new HttpRequestException("Internal Server Error", null, System.Net.HttpStatusCode.InternalServerError);
    _webscraperClientMock
        .Setup(w => w.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(),
            It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
        .ThrowsAsync(httpEx);

    SetupSearchResults(new[] { CreateSummary("itm001", sold: false) });
    var processor = CreateProcessor();
    var run = await processor.CreateRun(new ScrapeJobConfig(1, "Test"), "Manual");

    // Act
    await processor.Execute(run, new ScrapeJobConfig(1, "Test"));

    // Assert
    var issue = _dbContext.ScrapeRunIssues.FirstOrDefault(i => i.ScrapeRunId == run.Id);
    Assert.That(issue, Is.Not.Null, "Should record a ScrapeRunIssue");
    Assert.Multiple(() =>
    {
        Assert.That(issue!.IssueType, Is.EqualTo("DescriptionFetchFailed"));
        Assert.That(issue.Phase, Is.EqualTo("DescriptionFetch"));
        Assert.That(issue.HttpStatusCode, Is.EqualTo(500));
        Assert.That(issue.StackTrace, Is.Not.Null.And.Contains("HttpRequestException"));
    });
}
```

**Step 2: Write test for embedding failure diagnostics**

```csharp
[Test]
public async Task Should_record_phase_and_stack_trace_on_embedding_failure()
{
    // Arrange: description parses fine but embedding throws
    _webscraperClientMock
        .Setup(w => w.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(),
            It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync("<html><body>A real description</body></html>");

    _listingParserMock
        .Setup(p => p.ParseDescription(It.IsAny<IDocument>()))
        .Returns("A real description");

    _indexingServiceMock
        .Setup(i => i.Index(It.IsAny<Listing>(), true, It.IsAny<CancellationToken>()))
        .ThrowsAsync(new HttpRequestException("OpenAI rate limit exceeded"));

    SetupSearchResults(new[] { CreateSummary("itm001", sold: false) });
    var processor = CreateProcessor();
    var run = await processor.CreateRun(new ScrapeJobConfig(1, "Test"), "Manual");

    // Act
    await processor.Execute(run, new ScrapeJobConfig(1, "Test"));

    // Assert
    var issue = _dbContext.ScrapeRunIssues.FirstOrDefault(i => i.ScrapeRunId == run.Id);
    Assert.That(issue, Is.Not.Null, "Should record a ScrapeRunIssue");
    Assert.Multiple(() =>
    {
        Assert.That(issue!.IssueType, Is.EqualTo("IndexingFailed"));
        Assert.That(issue.Phase, Is.EqualTo("Indexing"));
        Assert.That(issue.StackTrace, Is.Not.Null.And.Contains("HttpRequestException"));
        Assert.That(issue.ErrorMessage, Does.Contain("OpenAI rate limit exceeded"));
    });
}
```

**Step 3: Write test for parse failure diagnostics**

```csharp
[Test]
public async Task Should_record_parse_failure_with_phase_and_stack_trace()
{
    // Arrange: HTML parsing throws
    _webscraperClientMock
        .Setup(w => w.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(),
            It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync("<html><body>content</body></html>");

    _listingParserMock
        .Setup(p => p.ParseDescription(It.IsAny<IDocument>()))
        .Throws(new InvalidOperationException("Unexpected DOM structure"));

    SetupSearchResults(new[] { CreateSummary("itm001", sold: false) });
    var processor = CreateProcessor();
    var run = await processor.CreateRun(new ScrapeJobConfig(1, "Test"), "Manual");

    // Act
    await processor.Execute(run, new ScrapeJobConfig(1, "Test"));

    // Assert
    var issue = _dbContext.ScrapeRunIssues.FirstOrDefault(i => i.ScrapeRunId == run.Id);
    Assert.That(issue, Is.Not.Null, "Should record a ScrapeRunIssue");
    Assert.Multiple(() =>
    {
        Assert.That(issue!.IssueType, Is.EqualTo("ParseFailed"));
        Assert.That(issue.Phase, Is.EqualTo("Parse"));
        Assert.That(issue.StackTrace, Is.Not.Null.And.Contains("InvalidOperationException"));
    });
}
```

**Step 4: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ScrapeJobProcessor_InlineTests" -v n`
Expected: 3 new tests FAIL (Phase/StackTrace/HttpStatusCode are null since model doesn't have them yet)

**Step 5: Commit failing tests**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_InlineTests.cs
git commit -m "test: add failing tests for diagnostic failure tracking"
```

---

### Task 4: Break up the catch-all and populate diagnostics

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs:408-485` (ProcessFetchedDescription)

**Step 1: Update fetch failure path to capture HTTP status and stack trace**

Replace the `result.Error != null` block (currently at ~line 423-438):

```csharp
if (result.Error != null)
{
    _logger.LogWarning(result.Error, "Failed to fetch description for {ListingId}", summary.ListingId);
    listing.DescriptionStatus = "missing";
    scrapeRun.ListingsFailed++;

    int? httpStatus = (result.Error as HttpRequestException)?.StatusCode is { } code
        ? (int)code
        : null;

    _dbContext.ScrapeRunIssues.Add(new ScrapeRunIssue
    {
        ScrapeRunId = scrapeRun.Id,
        ListingId = summary.ListingId!,
        IssueType = "DescriptionFetchFailed",
        Phase = "DescriptionFetch",
        ErrorMessage = result.Error.Message,
        StackTrace = result.Error.ToString(),
        HttpStatusCode = httpStatus,
        CreatedUtc = DateTime.UtcNow
    });
    await _dbContext.SaveChangesAsync();
    return;
}
```

**Step 2: Replace the try/catch block with granular error handling**

Replace the entire try/catch (currently at ~line 441-485) with three separate try/catch blocks:

```csharp
// Phase 1: Parse description
string? description = null;
try
{
    if (result.Html != null)
    {
        var document = await ParseHtml(result.Html);
        description = _listingParser.ParseDescription(document);
    }
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "Failed to parse description for {ListingId}", summary.ListingId);
    listing.DescriptionStatus = "failed";
    scrapeRun.ListingsFailed++;

    _dbContext.ScrapeRunIssues.Add(new ScrapeRunIssue
    {
        ScrapeRunId = scrapeRun.Id,
        ListingId = summary.ListingId!,
        IssueType = "ParseFailed",
        Phase = "Parse",
        ErrorMessage = ex.Message,
        StackTrace = ex.ToString(),
        CreatedUtc = DateTime.UtcNow
    });
    await _dbContext.SaveChangesAsync();
    return;
}

if (string.IsNullOrEmpty(description))
{
    listing.DescriptionStatus = "missing";
}
else
{
    listing.Description = description;
    listing.DescriptionStatus = "complete";
}

await _dbContext.SaveChangesAsync();

// Phase 2: Embed and index
if (listing.DescriptionStatus == "complete")
{
    try
    {
        await _indexingService.Index(listing, embedContent: true);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to index listing {ListingId}", summary.ListingId);
        scrapeRun.ListingsFailed++;

        _dbContext.ScrapeRunIssues.Add(new ScrapeRunIssue
        {
            ScrapeRunId = scrapeRun.Id,
            ListingId = summary.ListingId!,
            IssueType = "IndexingFailed",
            Phase = "Indexing",
            ErrorMessage = ex.Message,
            StackTrace = ex.ToString(),
            CreatedUtc = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();
        return;
    }
}

if (summary.IsSold)
{
    scrapeRun.ListingsAddedSold++;
}
else
{
    scrapeRun.ListingsAddedActive++;
}
```

Note: Indexing failures no longer set `listing.DescriptionStatus = "failed"` — the description parsed fine, only embedding/indexing failed. The listing data is still good; it just isn't in the vector index yet.

**Step 3: Update the ListingNotFound path to include Phase**

```csharp
if (listing == null)
{
    _logger.LogWarning("Listing {ListingId} not found for description processing", summary.ListingId);
    scrapeRun.ListingsFailed++;

    _dbContext.ScrapeRunIssues.Add(new ScrapeRunIssue
    {
        ScrapeRunId = scrapeRun.Id,
        ListingId = summary.ListingId!,
        IssueType = "ListingNotFound",
        Phase = "DescriptionFetch",
        ErrorMessage = "Listing not found in database during description processing",
        CreatedUtc = DateTime.UtcNow
    });
    await _dbContext.SaveChangesAsync();
    return;
}
```

**Step 4: Add `using System.Net.Http;` if not present** (for HttpRequestException.StatusCode)

**Step 5: Run tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ScrapeJobProcessor_InlineTests" -v n`
Expected: All tests PASS

**Step 6: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs
git commit -m "feat: granular failure tracking with phase, stack trace, and HTTP status"
```

---

### Task 5: Build and verify migration applies

**Step 1: Build the full solution**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`
Expected: Build succeeded

**Step 2: Run all unit tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter Category=Unit -v n`
Expected: All pass

**Step 3: Commit if any remaining changes**

---

## Summary of IssueTypes after implementation

| IssueType | Phase | Diagnostics captured |
|-----------|-------|---------------------|
| ListingNotFound | DescriptionFetch | Static message |
| DescriptionFetchFailed | DescriptionFetch | ErrorMessage, StackTrace, HttpStatusCode |
| ParseFailed | Parse | ErrorMessage, StackTrace |
| IndexingFailed | Indexing | ErrorMessage, StackTrace |

## Querying diagnostics

```sql
-- Find all failures grouped by type and phase
SELECT IssueType, Phase, COUNT(*) as Count
FROM ScrapeRunIssues
GROUP BY IssueType, Phase
ORDER BY Count DESC

-- Find embedding failures (OpenAI issues)
SELECT ListingId, ErrorMessage, HttpStatusCode, CreatedUtc
FROM ScrapeRunIssues
WHERE Phase = 'Indexing'
ORDER BY CreatedUtc DESC

-- Find bot detection (403/500 from scraper)
SELECT ListingId, HttpStatusCode, ErrorMessage
FROM ScrapeRunIssues
WHERE Phase = 'DescriptionFetch' AND HttpStatusCode IS NOT NULL
ORDER BY CreatedUtc DESC

-- Full stack trace for a specific failure
SELECT ListingId, IssueType, Phase, ErrorMessage, StackTrace
FROM ScrapeRunIssues
WHERE Id = <issueId>
```
