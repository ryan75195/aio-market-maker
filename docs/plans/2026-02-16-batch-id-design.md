# Batch ID for Scrape Run Grouping

**Date:** 2026-02-16
**Status:** Approved

## Problem

When a scrape is triggered (manual or nightly), each enabled job gets its own `ScrapeRun` record. There is no way to know which runs were triggered together. This makes it hard to:
- Show a batch as one collapsible row in the history UI
- Answer "did tonight's nightly batch fully succeed?"
- Aggregate stats across all jobs from a single trigger

## Design

### Approach: `BatchId` column on `ScrapeRun`

Add a nullable `Guid? BatchId` to the existing `ScrapeRun` table. Both trigger points (`StartScrape`, `RunNightly`) generate a GUID before the job loop and stamp every run with it.

**Why not a separate `ScrapeBatch` table?** No batch-level operations (cancel, retry) are needed yet. A column is sufficient for grouping and aggregation. If batch-level metadata is needed later, migrating from a column to a table is straightforward: create `ScrapeBatch`, backfill from distinct `BatchId` values.

### Schema Change

Migration `038_AddBatchIdToScrapeRuns.sql`:

```sql
ALTER TABLE ScrapeRuns ADD BatchId UNIQUEIDENTIFIER NULL;
CREATE INDEX IX_ScrapeRuns_BatchId ON ScrapeRuns (BatchId) WHERE BatchId IS NOT NULL;
```

Nullable so legacy runs need no backfill.

### Model Change

`ScrapeRun.cs`:

```csharp
public Guid? BatchId { get; set; }
```

EF config: filtered index on `BatchId`.

### Run Creation

`IScrapeJobProcessor.CreateRun` gains an optional `batchId` parameter:

```csharp
Task<ScrapeRun> CreateRun(ScrapeJobConfig job, string triggerType, Guid? batchId = null);
```

Both `ScrapeEndpoints.StartScrape` and `NightlyScrapeService.RunNightly` generate a `Guid.NewGuid()` before the loop and pass it through.

### StartScrape Response

Include `BatchId` so the UI can link to it:

```csharp
public record StartScrapeResponse(Guid BatchId, IEnumerable<ScrapeRunInfo> Runs);
```

### History API: Batch Endpoint

New endpoint: `GET /api/history/batches?page=1&pageSize=20`

Response shape:

```json
{
  "items": [
    {
      "batchId": "abc-123",
      "triggerType": "Nightly",
      "startedUtc": "2026-02-16T02:00:00Z",
      "completedUtc": "2026-02-16T02:45:00Z",
      "status": "Completed",
      "runCount": 3,
      "totalListingsFound": 1500,
      "totalListingsProcessed": 1420,
      "runs": [...]
    }
  ],
  "totalCount": 45,
  "totalPages": 3,
  "page": 1,
  "pageSize": 20
}
```

**Batch status derivation** (computed from child runs, not stored):
- Any run Running/Searching/Indexing/Processing -> `Running`
- Any run Queued (none running) -> `Queued`
- All Completed -> `Completed`
- Mix of Completed and Failed -> `PartialFailure`
- All Failed -> `Failed`

Existing `GET /api/history` unchanged for backward compatibility.

### Out of Scope

- No "cancel batch" or "retry batch" operations
- No backfill of legacy runs (they show as unbatched)
- No changes to existing per-run history endpoint
