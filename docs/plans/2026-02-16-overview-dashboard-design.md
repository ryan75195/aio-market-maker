# Overview Dashboard Design

**Date:** 2026-02-16
**Goal:** Add an Overview tab to the Electron desktop UI that serves as a combined health check + portfolio analysis dashboard.

## Tech Stack

- **Charting:** Chart.js via CDN (same pattern as Vue CDN in index.html)
- **API:** Single new `GET /api/overview` endpoint returning all dashboard data
- **Frontend:** New Vue view in existing `app.js` + `index.html`

## Layout

### KPI Cards (top row)

| Card | Data Source | Notes |
|---|---|---|
| Total Listings | `COUNT(*)` from Listings | All statuses |
| Active Listings | `WHERE ListingStatus = 'Active'` | Filterable subset |
| Opportunities | Active + `PotentialProfit > 0` + `SimilarSoldCount >= minComps` | Uses vw_ListingPredictions |
| Aggregate Profit | `SUM(PotentialProfit)` where profit > 0 | Sum across all qualifying opportunities |
| Last Scrape | Most recent ScrapeRun | Status, time ago, active/sold counts |

### Charts (middle section)

**Cumulative Listings Over Time** - Chart.js line chart (full width)
- X-axis: dates, Y-axis: cumulative listing count
- Query: `GROUP BY CAST(CreatedUtc AS DATE)` with windowed SUM
- Single line showing total growth over time

**Listings by Job** - Chart.js horizontal bar (left half)
- One bar per ScrapeJob showing listing count
- Query: `GROUP BY ScrapeJobId` joined to ScrapeJobs for search term labels

**Profit Distribution** - Chart.js vertical bar (right half)
- Buckets: $0-25, $25-50, $50-100, $100+
- Count of opportunities in each profit range

### Tables (bottom section)

**Top 10 Opportunities** - table sorted by PotentialProfit DESC
- Columns: Title, Price, Avg Sold, Profit, Comps, Condition, View link
- Filtered to >= 3 comps (uses settings.opportunities.minComps)

**Recent Scrape Runs** - last 5 runs, compact table
- Columns: Time, Job, Status, +Active, +Sold, Failed

## API Design

### `GET /api/overview`

Single endpoint returning all dashboard data in one call to avoid waterfall requests.

**Query parameters:**
- `minComps` (int, default 3) - minimum comparable sales for opportunity counting
- `feePercent` (decimal, default 13.25) - fee deduction for profit calculations
- `matchCondition` (bool, default true) - condition matching for predictions

**Response shape:**

```json
{
  "totalListings": 4231,
  "activeListings": 1847,
  "soldListings": 2104,
  "endedListings": 280,
  "opportunities": 312,
  "aggregateProfit": 14230.50,
  "lastScrape": {
    "startedUtc": "2026-02-16T10:00:00Z",
    "status": "Completed",
    "jobSearchTerm": "PS5 Console",
    "listingsAddedActive": 47,
    "listingsAddedSold": 23
  },
  "cumulativeGrowth": [
    { "date": "2026-01-15", "cumulative": 120 },
    { "date": "2026-01-16", "cumulative": 245 }
  ],
  "listingsByJob": [
    { "jobId": 1, "searchTerm": "PS5 Console", "count": 892 }
  ],
  "profitDistribution": {
    "range0to25": 142,
    "range25to50": 89,
    "range50to100": 52,
    "range100plus": 29
  },
  "topOpportunities": [
    {
      "listingId": "123456",
      "title": "PS5 Slim Digital Edition",
      "price": 320.00,
      "currency": "USD",
      "averageSoldPrice": 445.00,
      "potentialProfit": 66.25,
      "similarSoldCount": 8,
      "condition": "Used",
      "url": "https://ebay.com/itm/123456"
    }
  ],
  "recentRuns": [
    {
      "id": 42,
      "startedUtc": "2026-02-16T10:00:00Z",
      "jobSearchTerm": "PS5 Console",
      "status": "Completed",
      "listingsAddedActive": 47,
      "listingsAddedSold": 23,
      "listingsFailed": 0
    }
  ]
}
```

## Frontend Changes

1. Add Chart.js CDN script tag to `index.html`
2. Add "Overview" nav button as first sidebar item
3. Set `currentView` default to `'overview'` (landing page)
4. New `loadOverview()` method fetches `/api/overview`
5. Chart.js instances created/updated in `Vue.nextTick()` after data loads
6. Reuse existing CSS patterns (`.card`, `.data-table`, `.stats-banner`)

## Implementation Notes

- All queries are read-only aggregations, no performance concern for dashboard load
- The cumulative growth query uses SQL window functions (`SUM OVER ORDER BY`)
- Chart.js instances must be destroyed before re-creating on refresh (prevent memory leaks)
- The overview endpoint reuses the same opportunity filtering logic as `/api/listings/active`
