# Parser-Side Validation with Retry - Design

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Move content validation from scraper to parser, with retry capability for parse failures.

**Architecture:** Scraper saves all HTML unconditionally. Parser validates using existing parsers. Parse failures trigger re-scrape up to 3 times.

**Tech Stack:** AIOWebScraper (worker), AIOMarketMaker (parser/ETL), Azure Storage Queues

---

## Principle

Clean boundary between systems:
- **Scraper** = fetch and save everything (no content validation)
- **Parser** = validate and retry if needed (domain knowledge lives here)

---

## 1. Scraper Changes

### Remove Bot Detection

**File:** `AIOWebScraper/ScraperWorker/Services/SimpleQueueWorker.cs`

Remove:
- `IsBotDetected()` method (lines 320-332)
- Size check and exception throw (lines 160-164)

Before:
```csharp
if (content.Length < 100_000 && IsBotDetected(content))
{
    throw new Exception($"Bot detection page received ({content.Length / 1024.0:F1}KB)");
}
```

After: Delete these lines. Save all content unconditionally.

### Simplify Queue Message

**File:** `AIOWebScraper/AIOWebScraper.Storage.Azure/QueueMessage.cs`

Remove retry fields from contract (scraper handles network retries internally):
- `AttemptNumber`
- `MaxRetries`
- `IsLastAttempt`

Keep only:
```csharp
public record ScrapeQueueMessage
{
    public string JobId { get; init; } = default!;
    public string Url { get; init; } = default!;
    public string? CorrelationId { get; init; }
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? ProxyConfigJson { get; init; }
    public string? GroupId { get; init; }
    public string? FileKey { get; init; }
    public int? ScrapeRunId { get; init; }
}
```

### Internal Network Retries

Scraper keeps internal retry logic for network failures (timeout, connection refused):
- Retry up to 3 times in memory during processing
- On final network failure: dead-letter the message
- This is internal - not exposed on queue message

---

## 2. Parser Validation

### All Fields Required

**File:** `AIOMarketMaker.Etl/Activities/ProcessListingActivity.cs`

After parsing, check all fields are present:

```csharp
var listing = _parser.ParseProductListing(document, url);

var missingFields = new List<string>();
if (listing.id == null) missingFields.Add("id");
if (listing.title == null) missingFields.Add("title");
if (listing.price == null) missingFields.Add("price");
if (listing.currency == null) missingFields.Add("currency");
if (listing.Condition == null) missingFields.Add("condition");
if (listing.images == null || listing.images.Count == 0) missingFields.Add("images");
if (listing.listingStatus == null) missingFields.Add("listingStatus");
if (listing.purchaseFormat == PurchaseFormat.Unknown) missingFields.Add("purchaseFormat");

if (missingFields.Any())
{
    return new ParseResult.Failed(missingFields);
}
```

### No Heuristic Bot Detection

- No keyword checks (captcha, gdpr, etc.)
- No size thresholds
- Just: "did parsing succeed fully?"

---

## 3. Database Changes

### Add Parse Tracking Columns

**File:** `AIOMarketMaker.Core/Data/Migrations/SqlServer/028_AddParseTrackingColumns.sql`

```sql
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunListings') AND name = 'ParseAttempts')
BEGIN
    ALTER TABLE ScrapeRunListings ADD ParseAttempts INT NOT NULL DEFAULT 0;
END

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunListings') AND name = 'FailureReason')
BEGIN
    ALTER TABLE ScrapeRunListings ADD FailureReason NVARCHAR(100) NULL;
END

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunListings') AND name = 'FailureDetails')
BEGIN
    ALTER TABLE ScrapeRunListings ADD FailureDetails NVARCHAR(500) NULL;
END
```

### Update EF Model

**File:** `AIOMarketMaker.Core/Data/Models/ScrapeRunListing.cs`

```csharp
public int ParseAttempts { get; set; } = 0;
public string? FailureReason { get; set; }
public string? FailureDetails { get; set; }
```

### Failure Reasons

| FailureReason | Description |
|---------------|-------------|
| `PARSE_INCOMPLETE` | One or more required fields missing after parse |
| `PARSE_EXHAUSTED` | Max parse attempts reached, still failing |

---

## 4. Retry Flow

### In ProcessListingActivity

```csharp
// 1. Get current attempt count
var listingRow = await GetScrapeRunListing(scrapeRunId, listingId);
var attempts = listingRow.ParseAttempts;

// 2. Try to parse
var result = TryParse(htmlBlob);

if (result.Success)
{
    await SaveListing(result.Listing);
    await MarkComplete(listingRow);
    return;
}

// 3. Parse failed - retry or fail
if (attempts < 3)
{
    listingRow.ParseAttempts = attempts + 1;
    await UpdateListing(listingRow);
    await EnqueueScrapeMessage(listingId, url, scrapeRunId);
    return; // Will retry
}

// 4. Exhausted retries - record failure
listingRow.Status = "Failed";
listingRow.FailureReason = "PARSE_EXHAUSTED";
listingRow.FailureDetails = $"Missing: {string.Join(", ", result.MissingFields)}";
await UpdateListing(listingRow);
await RecordIssue(scrapeRunId, listingId, "PARSE_FAILED", listingRow.FailureDetails);
```

### Enqueue Retry Message

```csharp
private async Task EnqueueScrapeMessage(string listingId, string url, int scrapeRunId)
{
    var message = new ScrapeQueueMessage
    {
        JobId = $"retry-{scrapeRunId}",
        Url = url,
        ScrapeRunId = scrapeRunId,
        GroupId = listingId,
        FileKey = "listing.html"
    };

    await _queueClient.SendMessageAsync(JsonSerializer.Serialize(message));
}
```

---

## 5. Data Flow

```
┌─────────────────────────────────────────────────────────────────┐
│                         SCRAPER                                  │
│  Queue Message → Fetch URL → Save to Blob (always)              │
│  (internal network retries, 3 attempts)                          │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼ Blob Trigger
┌─────────────────────────────────────────────────────────────────┐
│                         PARSER                                   │
│  Read Blob → Parse HTML → All fields present?                   │
│                              │                                   │
│              ┌───────────────┴───────────────┐                  │
│              ▼                               ▼                   │
│           [YES]                           [NO]                   │
│        Save Listing                  ParseAttempts < 3?         │
│        Mark Complete                        │                    │
│                              ┌──────────────┴──────────────┐    │
│                              ▼                              ▼    │
│                           [YES]                          [NO]   │
│                      Increment attempts            Record failure│
│                      Enqueue retry                 Mark Failed   │
│                                                    Log issue     │
└─────────────────────────────────────────────────────────────────┘
```

---

## 6. UI Visibility

### History Issues

Failed listings appear in the existing issues UI with:
- `IssueType`: `PARSE_FAILED`
- `ErrorMessage`: `Missing: title, price, images`

### ScrapeRunListings

Query for debugging:
```sql
SELECT ListingId, ParseAttempts, FailureReason, FailureDetails
FROM ScrapeRunListings
WHERE Status = 'Failed' AND ScrapeRunId = @runId
```

---

## Implementation Tasks

1. **Database Migration** - Add ParseAttempts, FailureReason, FailureDetails columns
2. **EF Model Update** - Add new properties to ScrapeRunListing
3. **Remove Scraper Bot Detection** - Delete IsBotDetected and size check
4. **Simplify Queue Message** - Remove AttemptNumber/MaxRetries from contract
5. **Add Parser Validation** - Check all fields in ProcessListingActivity
6. **Add Retry Logic** - Enqueue retry on parse failure
7. **Add Failure Recording** - Set FailureReason/Details on final failure
