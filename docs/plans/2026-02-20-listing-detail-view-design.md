# Listing Detail View — Design

## Goal

Add a full-page detail view to the Desktop app so users can click an opportunity, inspect its data, and validate that each comparable is actually the same product. Bad comps can be dismissed, which recalculates the opportunity metrics.

## Why

The opportunities table shows aggregate metrics (avg sold price, comp count, profit) but there's no way to verify the comps are correct. A listing for "PS5 Digital" might have comps that are bundles, accessories, or wrong variants. Without manual inspection, users can't trust the pricing data.

## Approach

Single new API endpoint returns the anchor listing with all its comparables in one call. Desktop renders a full-page view with the anchor at top and a grid of comp cards below. Each comp card has a dismiss button that removes the relationship and recalculates metrics.

## API

### `GET /api/listings/{id:int}`

Returns the anchor listing with computed predictions and all comparable sold listings.

**Response:**

```json
{
  "listing": {
    "id": 42,
    "listingId": "123456789",
    "title": "Sony PS5 Digital Edition",
    "description": "Brand new sealed...",
    "price": 349.99,
    "currency": "GBP",
    "shippingCost": 5.00,
    "condition": "New",
    "url": "https://ebay.co.uk/itm/123456789",
    "images": ["https://...jpg"],
    "listingStatus": "Active",
    "searchTerm": "PS5 Console",
    "createdUtc": "2026-02-18T...",
    "averageSoldPrice": 385.00,
    "similarSoldCount": 8,
    "estimatedDaysToSell": 4,
    "potentialProfit": 25.01
  },
  "comparables": [
    {
      "relationshipId": 101,
      "listingId": "987654321",
      "title": "PlayStation 5 Digital Console",
      "description": "Used once, excellent...",
      "price": 380.00,
      "condition": "Used",
      "url": "https://ebay.co.uk/itm/987654321",
      "images": ["https://...jpg"],
      "soldDateUtc": "2026-02-10T...",
      "similarityScore": 0.94,
      "explanation": "Same console variant..."
    }
  ]
}
```

**Implementation:** EF Core query joins `Listing` → `ListingPrediction` → `ListingRelationship` → comparable `Listing`. Single DB round-trip.

### `DELETE /api/listings/{id:int}/comparables/{relationshipId:int}`

Deletes the `ListingRelationship` row and recalculates `ListingPrediction` for the anchor listing (re-averages remaining comps' sold prices, days to sell).

Returns the updated listing response (same shape as GET) so the UI can refresh in place without a second call.

## Desktop UI

### Navigation

Click any row in the opportunities table → navigates to `listing-detail` view. Back button returns to opportunities table, preserving filters, sort, and page position.

### Layout

```
┌──────────────────────────────────────────────────────┐
│ ← Back    Sony PS5 Digital Edition    [View on eBay] │
├──────────────────────────────────────────────────────┤
│ ┌─────────┐                                          │
│ │  IMAGE  │  Title: Sony PS5 Digital Edition          │
│ │         │  Price: £349.99 + £5.00 shipping          │
│ │         │  Condition: New                           │
│ │         │  Status: Active                           │
│ │         │  ─────────────────────────────            │
│ └─────────┘  Avg Sold: £385.00 | Comps: 8            │
│              Profit: £25.01 | Days to Sell: 4         │
│              ─────────────────────────────            │
│              Description:                             │
│              Brand new sealed PlayStation 5...        │
├──────────────────────────────────────────────────────┤
│ Comparables (8)                                      │
│                                                      │
│ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐   │
│ │ [IMG]        │ │ [IMG]        │ │ [IMG]        │   │
│ │ PS5 Digital  │ │ PlayStation  │ │ PS5 Console  │   │
│ │ Console      │ │ 5 Digital Ed │ │ Digital      │   │
│ │              │ │              │ │              │   │
│ │ Brand new... │ │ Excellent... │ │ Like new...  │   │
│ │              │ │              │ │              │   │
│ │ £380 | Used  │ │ £395 | New   │ │ £370 | Used  │   │
│ │ Score: 0.94  │ │ Score: 0.91  │ │ Score: 0.89  │   │
│ │ [Dismiss]    │ │ [Dismiss]    │ │ [Dismiss]    │   │
│ └──────────────┘ └──────────────┘ └──────────────┘   │
└──────────────────────────────────────────────────────┘
```

### Comp Cards

Each card shows:
- Thumbnail image (first from images array, or a placeholder)
- Title
- Description (truncated to ~3 lines)
- Sold price + condition
- Similarity score badge
- Red "Dismiss" button

### Dismiss Flow

1. User clicks "Dismiss" on a comp card
2. Card fades out
3. `DELETE /api/listings/{id}/comparables/{relationshipId}` fires
4. Response contains updated listing + remaining comps
5. Anchor metrics update (avg price, profit, comp count)
6. Toast: "Comparable dismissed. 7 remaining."

### Styling

Matches existing dark theme. Cards use same border/shadow patterns as dashboard KPI cards.

## Data Flow

```
User clicks row in opportunities table
  → app.currentView = 'listing-detail'
  → app.selectedListingId = row.id
  → fetch GET /api/listings/{id}
  → render full page

User clicks "Dismiss" on a comp
  → fetch DELETE /api/listings/{id}/comparables/{relationshipId}
  → response contains updated listing + remaining comps
  → re-render with updated data
  → toast notification

User clicks "Back"
  → app.currentView = 'opportunities'
  → table restores previous state (filters, sort, page)
```

## What This Design Does NOT Cover

- Editing listing data
- Manually adding comps
- Price history chart
- Side-by-side diff view
- Bulk dismiss across multiple opportunities
