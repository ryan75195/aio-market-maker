# Retry Status UI Design

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Show retrying and failed listings in the History view so users can see parse retry progress in real-time.

**Architecture:** Enhance existing issues endpoint to include retrying listings, update UI to show status badges with attempt counts.

**Tech Stack:** Azure Functions API, Vue.js frontend, EF Core

---

## Summary

Currently, listings that fail parsing retry up to 3 times silently. Users only see failures after all retries are exhausted. This design adds visibility into:
- Listings currently retrying (with attempt count)
- Failed listings (with final attempt count)

## API Changes

### Enhanced `/history/{runId}/issues` Endpoint

**File:** `AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs`

The endpoint will return a union of:
1. **Retrying listings** - from `ScrapeRunListings` where `ParseAttempts > 0` AND `Status` not Complete/Failed
2. **Failed listings** - from `ScrapeRunIssues` joined with `ScrapeRunListings`

**Response structure:**
```json
[
  {
    "listingId": "123456789",
    "status": "Retrying",
    "parseAttempts": 2,
    "issueType": null,
    "errorMessage": "Missing: title, price",
    "createdUtc": "2026-01-30T12:00:00Z"
  },
  {
    "listingId": "987654321",
    "status": "Failed",
    "parseAttempts": 3,
    "issueType": "PARSE_FAILED",
    "errorMessage": "Missing: images",
    "createdUtc": "2026-01-30T12:05:00Z"
  }
]
```

### Updated `GetHistory` Endpoint

Include retrying listings count in `issueCount` so the expand indicator shows correct total.

## UI Changes

### Issues Table Columns

**File:** `AIOMarketMaker.Desktop/electron/src/index.html`

**Current:**
| Listing ID | Issue Type | Error Message | Time |

**New:**
| Listing ID | Status | Attempts | Error Message | Time |

- **Status** - Badge showing `Retrying` (amber) or `Failed` (red)
- **Attempts** - Format: `2/3`
- **Issue Type** column removed (redundant)

### Status Badge Styling

**File:** `AIOMarketMaker.Desktop/electron/src/styles.css`

```css
.status-badge.retrying { background: #f59e0b; }  /* amber */
.status-badge.failed { background: #ef4444; }    /* red */
```

### Live Polling

**File:** `AIOMarketMaker.Desktop/electron/src/app.js`

Update `startAutoRefresh()` to also refresh issues for expanded running runs:

```javascript
startAutoRefresh() {
  this.refreshInterval = setInterval(() => {
    if (this.currentView === 'history') {
      const hasRunning = this.history.some(r => r.status === 'Running');
      if (hasRunning) {
        this.loadHistory();
        // Also refresh issues for any expanded running runs
        this.history
          .filter(r => r.status === 'Running' && this.expandedRuns[r.id])
          .forEach(r => this.loadRunIssues(r.id));
      }
    }
  }, 2000);
}
```

## Implementation Tasks

1. **Update GetHistoryIssues endpoint** - Query ScrapeRunListings for retrying listings, union with failed issues
2. **Update GetHistory endpoint** - Include retrying count in issueCount
3. **Update issues table HTML** - Replace columns with Status, Attempts
4. **Add status badge CSS** - Retrying (amber), Failed (red)
5. **Update app.js** - Handle new fields, format attempts, add live polling for issues
