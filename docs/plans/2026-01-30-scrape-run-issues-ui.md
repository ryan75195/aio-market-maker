# Scrape Run Issues UI - Design

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Surface partial failures (like missing descriptions) in the history UI so users can see when scrape runs had issues.

**Architecture:** New `ScrapeRunIssues` table stores fetch failures. History API returns issue counts. UI shows expandable issue details per run.

**Tech Stack:** SQL Server, Azure Functions API, Electron/HTML UI

---

## Data Model

### New Table: `ScrapeRunIssues`

```sql
CREATE TABLE ScrapeRunIssues (
    Id INT IDENTITY PRIMARY KEY,
    ScrapeRunId INT NOT NULL,
    ListingId NVARCHAR(50) NOT NULL,
    IssueType NVARCHAR(50) NOT NULL,
    ErrorMessage NVARCHAR(500),
    CreatedUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT FK_ScrapeRunIssues_ScrapeRuns FOREIGN KEY (ScrapeRunId) REFERENCES ScrapeRuns(Id)
);
CREATE INDEX IX_ScrapeRunIssues_ScrapeRunId ON ScrapeRunIssues(ScrapeRunId);
```

### Issue Types

| IssueType | Description |
|-----------|-------------|
| `LISTING_FETCH_FAILED` | Main listing HTML couldn't be fetched |
| `DESCRIPTION_FETCH_FAILED` | Description iframe HTML couldn't be fetched |

---

## API Changes

### Extend `GET /api/history`

Add `issueCount` field to each run:

```json
{
  "Id": 18050,
  "JobSearchTerm": "PlayStation 5 Console",
  "Status": "Completed",
  "ListingsAdded": 200,
  "issueCount": 4,
  ...
}
```

### New Endpoint: `GET /api/history/{runId}/issues`

Returns issues for a specific run:

```json
[
  {
    "listingId": "406635225517",
    "issueType": "DESCRIPTION_FETCH_FAILED",
    "errorMessage": "Bot detection page received (3.7KB)",
    "createdUtc": "2026-01-30T15:17:24Z"
  }
]
```

---

## UI Changes

### History Row (with issues)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ ⚠️ PlayStation 5 Console    200 added    Completed    Jan 30, 15:22    ▼ 4  │
└──────────────────────────────────────────────────────────────────────────────┘
```

- Warning icon (⚠️) when `issueCount > 0`
- Expand indicator (▼ N) at end of row
- Clicking expands to show issue details

### Expanded View

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ ⚠️ PlayStation 5 Console    200 added    Completed    Jan 30, 15:22    ▲ 4  │
│  ┌─────────────────────────────────────────────────────────────────────────┐ │
│  │ 406635225517    Description fetch failed    Bot detection (3.7KB)       │ │
│  │ 406650718297    Description fetch failed    Bot detection (4.3KB)       │ │
│  │ 406650725123    Description fetch failed    Bot detection (7.2KB)       │ │
│  │ 236607898320    Description fetch failed    Bot detection (9.6KB)       │ │
│  └─────────────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────────┘
```

- Listing IDs link to eBay
- Human-readable issue type
- Runs with no issues show no indicator (unchanged from today)

---

## Issue Recording

### Integration Point

`ListingEtlOrchestrator` records issues when blob fetch fails after timeout:

```csharp
public record RecordIssueRequest(int ScrapeRunId, string ListingId, string IssueType, string ErrorMessage);

// Called when description blob not found after timeout
await context.CallActivityAsync("RecordIssueActivity", new RecordIssueRequest(
    scrapeRunId,
    listingId,
    "DESCRIPTION_FETCH_FAILED",
    "Blob not found after timeout"
));
```

The orchestrator continues processing (partial success) - issues are recorded for visibility only.

---

## Implementation Tasks

1. **Database Migration** - Create `ScrapeRunIssues` table
2. **EF Core Model** - Add `ScrapeRunIssue` entity and DbSet
3. **RecordIssueActivity** - New activity to write issues to database
4. **ListingEtlOrchestrator** - Call RecordIssueActivity on fetch failures
5. **History API** - Add issueCount to response, add issues endpoint
6. **Desktop UI** - Add expandable issues to history table
