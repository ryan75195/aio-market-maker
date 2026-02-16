# History UI Batch Drill-Down Design

**Date:** 2026-02-16
**Status:** Approved

## Problem

The History tab shows a flat list of individual ScrapeRun rows. Now that runs are grouped by BatchId, the UI should show batches as top-level rows that drill down into individual job runs.

## Design

### Navigation Flow

```
History Tab
├── Batch List (default view)
│   Row: "Nightly • Feb 16, 2:00 AM • 3 jobs • Completed • 1,500 found"
│   ...
│
└── [Click a batch] → Run Detail View
    ← Back to Batches
    Header: "Manual • Feb 15 3:30 PM • 2 jobs"
    Row: "Nike Dunk Low • Completed • 400 found • 12 active • 8 sold"
    Row: "PS5 Console • Failed • 400 found • 0 active • 0 sold"
        └── [Expandable issues row — same as today]
```

### Batch List View (replaces current flat table)

**Data source:** `GET /api/history/batches?page=1&pageSize=20`

**Columns:**
- Trigger (Manual/Nightly badge)
- Started (relative time)
- Status (derived badge — Completed/Running/PartialFailure/Failed/Queued)
- Run Count ("3 jobs")
- Listings Found (total across runs)
- Listings Processed (total across runs)
- Progress bar (when running)

Aggregate stats banner stays, computed from active batches.
Auto-refresh continues — polls when any batch is Running/Queued.

### Run Detail View (on batch click)

**Header:** Back button + batch summary (trigger, timestamp, status)

**Table:** Same columns as today's history table (job search term, status, phase, progress, active/sold/updated/skipped/failed counts)

**Data source:** Already loaded — `/api/history/batches` response includes `runs[]` per batch.

**Expandable issues:** Same pattern — click a failed run to see issues via `GET /api/history/{runId}/issues`.

### State Management

```javascript
historyMode: 'batches',        // 'batches' | 'runs'
batches: [],                   // from /api/history/batches
selectedBatch: null,           // clicked batch object (contains runs[])
batchPage: 1,
batchTotalPages: 0,
```

### Legacy Runs

Legacy runs (null BatchId) are hidden. Only batched runs shown.

### Not in Scope

- No API changes
- No new endpoints
