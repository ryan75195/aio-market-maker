# Listing Prediction Service Design

**Date:** 2026-02-21
**Status:** Approved

## Problem

Three independent code paths compute listing prediction data (comps count, avg sold price, profit, days to sell):

1. **Opportunities table** (`GetActiveListings`) — raw SQL CTE with priceBand/matchCondition/feePercent filters
2. **Listing detail** (`GetListingDetail`) — EF Core LINQ + `vw_ListingPredictions` view (no priceBand filter)
3. **Overview dashboard** (`GetOverview`) — `vw_ListingPredictions` view (no priceBand filter)

This causes the comps count in the opportunities table to differ from the listing detail view (e.g., 9 vs 19 for the same listing) because the table applies priceBand filtering but the detail view doesn't.

## Solution

Extract a `ListingPredictionService` that encapsulates the CTE-based prediction logic. All three consumers call the same service with the same `PredictionFilters`, guaranteeing identical results.

## Design

### Filter Record

```csharp
public record PredictionFilters(
    decimal PriceBand = 0,
    decimal FeePercent = 0,
    bool MatchCondition = true,
    int MinComps = 0);
```

### Interface

```csharp
public interface IListingPredictionService
{
    Task<ListingPredictionResult?> GetPrediction(int listingId, PredictionFilters filters);
    Task<IEnumerable<ComparableSoldListing>> GetComparables(int listingId, PredictionFilters filters);
    Task<PagedPredictions> GetPredictions(
        PredictionFilters filters, IEnumerable<int>? jobIds,
        string sortBy, string sortDir, int page, int pageSize);
    Task<PredictionAggregates> GetAggregates(PredictionFilters filters);
}
```

### Location

- Interface + records: `AIOMarketMaker.Core/Services/ListingPredictionService.cs`
- Implementation: same file (per codebase convention: interface above class)

### Internal Implementation

- One private method `BuildCte(PredictionFilters)` generates the CTE SQL (moved from `BuildFilteredPredictionsCte`)
- `GetPrediction`: CTE scoped to single `ActiveListingId` — fast (~6ms)
- `GetComparables`: queries `ListingRelationships` with same condition/priceBand filters applied in SQL
- `GetPredictions`: CTE with sort/page/jobId — same as current `GetActiveListings` (~2.5s)
- `GetAggregates`: CTE with aggregation — replaces view-based overview queries (~1.2s)

### Endpoint Changes

- `GetActiveListings`: parse params -> call `GetPredictions` -> map to response
- `GetListingDetail`: call `GetPrediction` + `GetComparables` -> map to response
- `DismissComparable`: delete relationship, call `GetPrediction` + `GetComparables`
- `GetOverview` (OverviewEndpoints.cs): call `GetAggregates` instead of querying view

### What Gets Dropped

- `vw_ListingPredictions` database view (migration to drop it)
- `ListingPrediction` EF Core model + `DbSet<ListingPrediction>`
- `BuildFilteredPredictionsCte` private method in `ListingEndpoints.cs`
- In-memory filtering code added to `GetListingDetail` (revert)

### Performance Impact

| Endpoint | Before | After | Notes |
|----------|--------|-------|-------|
| Opportunities table | ~2.5s | ~2.5s | Same CTE, moved to service |
| Listing detail | ~6ms | ~6ms | CTE scoped to 1 ID, touches ~20 rows |
| Overview | ~1.2s | ~1.2-2.5s | Slight risk, benchmark during implementation |

Scale: 262K listings, 563K comparable relationships.

### Testing Strategy (TDD)

Tests go in `AIOMarketMaker.Tests.Unit/Services/ListingPredictionService_UnitTests.cs`.

Key test scenarios:
- **Price band filtering**: comps outside band excluded from count + avg
- **Condition matching**: toggle on/off changes comp set
- **Fee deduction**: profit computed with/without fees
- **Single listing**: `GetPrediction` returns correct values for one listing
- **No comps**: returns null/zero when no comparable relationships exist
- **Dismissed comps**: after relationship deletion, count decrements
- **Consistency**: `GetPrediction` and `GetPredictions` return same values for same listing
