# Job Categories Design

**Goal:** Add a category/tag grouping system for scrape jobs so users can organize jobs into categories (e.g., "Electronics", "Luxury Watches") and enable/disable entire categories as a group.

**Architecture:** Many-to-many relationship between jobs and categories via join table. Categories are first-class entities with their own enabled state. Job effective-enabled logic combines per-job IsEnabled with category membership.

## Data Model

### New Tables

```sql
Categories (
    Id          INT IDENTITY PRIMARY KEY,
    Name        NVARCHAR(100) NOT NULL UNIQUE,
    IsEnabled   BIT NOT NULL DEFAULT 1,
    CreatedUtc  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
)

JobCategories (
    JobId       INT NOT NULL FK -> ScrapeJobs(Id) ON DELETE CASCADE,
    CategoryId  INT NOT NULL FK -> Categories(Id) ON DELETE CASCADE,
    PRIMARY KEY (JobId, CategoryId)
)
```

### Effective Enabled Logic

A job is "effectively enabled" when:
1. `job.IsEnabled = true`
2. AND (job has no categories OR at least one of its categories has `IsEnabled = true`)

This means:
- **Uncategorized jobs**: controlled purely by their own `IsEnabled`
- **Categorized jobs**: need both their own `IsEnabled` AND at least one enabled category
- Disabling all categories on a job effectively disables it, even if `job.IsEnabled` is true
- A job in both "Electronics" (enabled) and "Watches" (disabled) stays enabled (union semantics)

## API Endpoints

### New Category Endpoints (`/api/categories`)

| Method | Endpoint | Purpose |
|--------|----------|---------|
| GET | `/api/categories` | List all categories with job counts |
| POST | `/api/categories` | Create category |
| PUT | `/api/categories/{id}` | Rename category |
| DELETE | `/api/categories/{id}` | Delete category (removes assignments, doesn't delete jobs) |
| POST | `/api/categories/{id}/enable` | Enable category |
| POST | `/api/categories/{id}/disable` | Disable category |

### Modified Job Endpoints

| Endpoint | Change |
|----------|--------|
| GET `/api/jobs` | Response includes `categories: [{id, name}]` per job |
| POST `/api/jobs` | Request accepts optional `categoryIds: [int]` |
| PUT `/api/jobs/{id}` | Request accepts optional `categoryIds: [int]` |
| POST `/api/jobs/{id}/categories` | Set categories on a job (replaces all assignments) |

### Modified Scrape Filtering

Replace `WHERE j.IsEnabled` in `ScrapeEndpoints.StartScrape` and `NightlyScrapeService` with:

```sql
WHERE j.IsEnabled = 1
  AND (NOT EXISTS (SELECT 1 FROM JobCategories jc WHERE jc.JobId = j.Id)
       OR EXISTS (SELECT 1 FROM JobCategories jc
                  JOIN Categories c ON c.Id = jc.CategoryId
                  WHERE jc.JobId = j.Id AND c.IsEnabled = 1))
```

## UI Design

### Job Overview Panel — View Toggle

The toolbar gains a two-button toggle:

```
[Jobs] [Categories]    [Search...]    [+ New Job]
```

### Job View (enhanced existing)

Same table as today with a new **Categories** column showing colored tag pills per job. Edit job modal gains a multi-select for assigning categories.

### Category View (new)

Expandable accordion rows, one per category:

- Each row: **Category Name | Job Count | Enabled toggle | Rename | Delete**
- Expand to see jobs in that category (compact sub-table: ID, Search Term, Enabled)
- Within expanded category: **+ Add Job** dropdown to assign jobs, **Remove** button per job
- **+ New Category** button in header (inline form, just a name field)
- Bottom section: **Uncategorized Jobs** showing jobs with no categories

Search bar filters categories by name in Category View.
