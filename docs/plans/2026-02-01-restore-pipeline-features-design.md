# Design: Restore Pipeline Features from Durable Functions Migration

**Date:** 2026-02-01
**Status:** Approved
**Authors:** Claude + Ryan

## Problem Statement

The 2026-01-31 Durable Functions migration introduced regressions by dropping features without documenting them. This plan restores full behavioral parity with the old pipeline.

## Behavioral Parity Checklist

| Behavior | Old Implementation | Status | Action |
|----------|-------------------|--------|--------|
| Multi-page search | `JobOrchestrator` lines 49-96 | MISSING | Restore |
| Sold listing search | `SearchPageAsync(isSold: true)` | MISSING | Restore |
| Active→Sold detection | `DetectAndUpdateSoldListingsAsync` | MISSING | Restore |
| ListingStatusHistory | `SaveListingsActivity` | MISSING | Restore |
| Status progression validation | `ListingStatusHelper.CanUpdateStatus()` | MISSING | Restore |
| Terminal status filtering | `FilterNewListingsActivity` | FIXED | b4b69e1 |

## Success Criteria

1. **Multi-page:** Scrape returns same listing count as old pipeline for same search term
2. **Sold search:** Sold listings appear in database with correct status
3. **Active→Sold:** Listings that sell between runs get status updated
4. **History:** `ListingStatusHistory` table populated on status changes
5. **Validation:** Cannot update a Sold listing back to Active
6. **Tests:** All behaviors have explicit unit tests that encode requirements

## Architecture: Search Phase

### Current Flow (Broken)
```
SimplifiedScrapeTrigger.RunScrapeForJobAsync()
    → Build search URL (page: 1, sold: false)  ← ONLY PAGE 1!
    → Fetch single page
    → Parse listings
    → Filter terminal statuses
    → Enqueue for processing
```

### New Flow (Restored)
```
SimplifiedScrapeTrigger.RunScrapeForJobAsync()
    │
    ├─► Phase 1: Search Sold Listings
    │   → Loop: page 1, 2, 3... until no results or lookbackDays exceeded
    │   → Deduplicate with HashSet<string>
    │   → Store sold listing IDs for Active→Sold detection
    │   → Update: ScrapeRun.CurrentPhase = "Searching Sold"
    │
    ├─► Phase 2: Search Active Listings
    │   → Loop: page 1, 2, 3... until no results (max 10,000 cap)
    │   → Deduplicate with HashSet<string>
    │   → Update: ScrapeRun.CurrentPhase = "Searching Active"
    │
    ├─► Phase 3: Detect Active→Sold Transitions
    │   → Query DB: listings WHERE Status = 'Active' AND ListingId IN (soldListingIds)
    │   → These are listings that sold since last scrape
    │   → Include them for re-scraping to get sold price/date
    │   → Update: ScrapeRun.CurrentPhase = "Filtering"
    │
    └─► Phase 4: Filter & Enqueue
        → Filter terminal statuses (existing behavior)
        → Combine: new active + new sold + active→sold transitions
        → Create ScrapeRunListings
        → Enqueue messages
        → Update: ScrapeRun.CurrentPhase = "Indexing"
```

## Architecture: Processing Phase

### Current Flow (Broken)
```
ProcessListingEndpoint.Run()
    → Check idempotency
    → Read blob, parse HTML
    → Simple upsert to Listings table (NO VALIDATION!)
    → Update ScrapeRunListing status
    → Increment counters
```

### New Flow (Restored)
```
ProcessListingEndpoint.Run()
    │
    ├─► Check idempotency (existing)
    │
    ├─► Read blob, parse HTML (existing)
    │
    ├─► Status Progression Validation (NEW)
    │   → Get existing listing if any
    │   → Call ListingStatusHelper.CanUpdateStatus(oldStatus, newStatus)
    │   → If invalid transition (e.g., Sold→Active): skip update, log warning
    │
    ├─► Upsert to Listings table
    │   → If status changed: create ListingStatusHistory record
    │   → Record: ListingId, OldStatus, NewStatus, OldPrice, NewPrice, RecordedUtc
    │
    └─► Update counters (existing)
```

### ListingStatusHelper

```csharp
public static class ListingStatusHelper
{
    private static readonly Dictionary<string, HashSet<string>> ValidTransitions = new()
    {
        ["Active"] = new() { "Sold", "Ended", "OutOfStock" },
        ["Sold"] = new() { },  // Terminal
        ["Ended"] = new() { },  // Terminal
        ["OutOfStock"] = new() { "Active" }  // Can come back in stock
    };

    public static bool CanUpdateStatus(string? oldStatus, string? newStatus)
    {
        if (oldStatus == null) return true;
        if (oldStatus == newStatus) return true;

        return ValidTransitions.TryGetValue(oldStatus, out var valid)
            && valid.Contains(newStatus);
    }
}
```

### ListingStatusHistory Population

Source values:
- `"InitialScrape"` - First time seeing this listing
- `"StatusUpdate"` - Status changed on re-scrape
- `"PriceUpdate"` - Price changed (same status)

## Test Strategy

### Principle: Audit Before Adding

For each feature, follow this order:
1. **Audit** - Find existing tests related to the behavior
2. **Evaluate** - Do they encode the business requirement correctly?
3. **Decide** - Modify, remove, or keep + add new

### Required Tests (Business Requirements)

#### SimplifiedScrapeTrigger Tests

| Test | Requirement |
|------|-------------|
| `Should_search_multiple_pages_until_no_results` | Multi-page continues until exhausted |
| `Should_search_sold_listings_before_active` | Sold phase runs first |
| `Should_detect_listings_that_transitioned_from_active_to_sold` | Active→Sold detection |
| `Should_skip_terminal_statuses_but_include_active_for_rescrape` | Correct filtering logic |

#### ProcessListingEndpoint Tests

| Test | Requirement |
|------|-------------|
| `Should_reject_invalid_status_transition_sold_to_active` | Status validation |
| `Should_create_status_history_when_status_changes` | History tracking |
| `Should_create_history_for_price_change_same_status` | Price change tracking |

#### Integration Tests

- Remove `[Explicit]` attribute so they run in CI
- Add full pipeline test covering sold + active + pagination

## Implementation Phases

### Phase 1: Test Audit & Updates
For each feature:
1. Grep for existing tests
2. Evaluate if they encode correct requirements
3. Modify/remove/add as needed

### Phase 2: Search Phase Enhancements
- Add `SearchSoldListingsAsync` with pagination
- Add `SearchActiveListingsAsync` with pagination
- Add `DetectActiveToSoldTransitionsAsync`
- Update `RunScrapeForJobAsync` to orchestrate phases
- Update phase tracking

### Phase 3: Processing Phase Enhancements
- Restore `ListingStatusHelper`
- Add status validation to `ProcessListingEndpoint`
- Add `ListingStatusHistory` creation
- Update response for validation skips

### Phase 4: Integration & Verification
- Run full test suite
- Manual E2E verification
- Update documentation

## Files to Modify

| File | Changes |
|------|---------|
| `SimplifiedScrapeTrigger.cs` | Add sold search, pagination, Active→Sold detection |
| `ProcessListingEndpoint.cs` | Add status validation, history creation |
| `SimplifiedScrapeTrigger_UnitTests.cs` | Audit and update tests |
| `ProcessListingEndpoint_UnitTests.cs` | Audit and update tests |

## Files to Create

| File | Purpose |
|------|---------|
| `ListingStatusHelper.cs` | Status transition validation |

## Estimated Scope

- **New/modified tests:** ~10
- **Lines of code:** ~400-500
- **Risk:** Medium (changes core pipeline logic)

## Rollback Plan

All changes are additive. If issues found:
1. Revert to terminal-only filtering (already working)
2. Disable sold search via config flag
3. Previous behavior preserved
