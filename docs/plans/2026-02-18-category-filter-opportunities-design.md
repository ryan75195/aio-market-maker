# Category Filter for Opportunities

**Date:** 2026-02-18
**Status:** Approved

## Problem

The opportunities view shows all listings across all jobs. Users need to filter by category to focus on specific product types. Jobs without a category should appear under "Uncategorised".

## Design

### Approach: Client-Side Category Resolution

No backend changes. The frontend resolves selected categories to job IDs and passes them to the existing `/api/listings/active?jobIds=` parameter.

### UI Layout

Two dropdowns in the opportunities toolbar:

1. **Category filter** (primary) — multi-select dropdown matching existing job filter style
   - "All" (default) — everything including uncategorised
   - List of categories from `/api/categories`
   - "Uncategorised" — virtual entry at the bottom for jobs with no categories

2. **Job filter** (secondary) — existing dropdown, scoped to only show jobs belonging to selected categories. When no categories selected, shows all jobs.

### State

```javascript
opportunityCategoryFilter: []    // selected category IDs (empty = All)
// -1 = Uncategorised virtual category
// existing: opportunityJobFilter: [] // scoped within selected categories
```

### Data Flow

1. User toggles categories in dropdown
2. Frontend computes matching job IDs:
   - Real categories: `jobs.filter(j => j.categories.some(c => selectedCategoryIds.includes(c.id)))`
   - "Uncategorised" (id=-1): `jobs.filter(j => j.categories.length === 0)`
3. Job filter dropdown updates to show only those jobs
4. Combined job IDs sent as `jobIds` param to API
5. `loadOpportunities()` called with filtered job IDs

### Interactions

- Selecting a category resets the job filter and page to 1
- Clearing categories shows all jobs and clears the job filter
- Category and job filters combine: category narrows available jobs, job filter narrows further within those

### Files Changed

- `AIOMarketMaker.Desktop/electron/src/app.js` — state, computed properties, methods
- `AIOMarketMaker.Desktop/electron/src/index.html` — category dropdown markup

### No Backend Changes

The existing `/api/listings/active?jobIds=` handles all filtering. Category-to-job resolution uses already-loaded `jobs[]` and `categories[]` data.
