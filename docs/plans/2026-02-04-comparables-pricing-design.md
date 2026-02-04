# Comparables Pricing ETL — Design

## Goal

Build a standalone ETL process that cross-matches all listings in the database using Pinecone semantic search and LLM-based pairwise verification to compute pricing predictions (average sold price, estimated time to sell, potential profit).

## Data Model

### `ListingRelationships` (new table)

Caches every LLM verdict for a pair of listings. Canonical ordering ensures each pair is stored once (ListingIdA < ListingIdB).

| Column | Type | Purpose |
|--------|------|---------|
| `Id` | int, PK | Auto-increment |
| `ListingIdA` | int, FK → Listings | Lower ID in the pair |
| `ListingIdB` | int, FK → Listings | Higher ID in the pair |
| `IsComparable` | bit | LLM verdict: are these the same product? |
| `Explanation` | nvarchar(500) | LLM reasoning for the verdict |
| `SimilarityScore` | float | Pinecone cosine similarity |
| `CreatedUtc` | datetime2 | When evaluated |

- Unique constraint on `(ListingIdA, ListingIdB)`
- Indexes on both FK columns

### `ListingPredictions` (new table)

Pre-computed pricing aggregates, one row per listing. Written at the end of each ETL run.

| Column | Type | Purpose |
|--------|------|---------|
| `Id` | int, PK | Auto-increment |
| `ListingId` | int, FK → Listings, unique | One prediction per listing |
| `AverageSoldPrice` | decimal(18,2) | Avg price of verified comparable sold listings |
| `SimilarSoldCount` | int | Count of comparables with valid prices |
| `EstimatedDaysToSell` | int, nullable | Avg days from listed to sold |
| `PotentialProfit` | decimal(18,2), nullable | AverageSoldPrice minus listing's current price |
| `ComputedUtc` | datetime2 | When this prediction was last calculated |

## ETL Pipeline Flow

Triggered as a standalone command: `dotnet run -- --comparables`

```
1. Load all listings from DB that have embeddings in Pinecone
   - No prioritization — process all unchecked relevant listings

2. For each listing, query Pinecone (parallel, throttled)
   - FindSimilar(listingId, topK=50)
   - Use metadata filters where applicable (e.g., condition)

3. Check verdict cache (ListingRelationships)
   - For each candidate pair, canonicalize: min(A,B), max(A,B)
   - Skip pairs that already have a verdict

4. LLM classification (parallel, throttled)
   - For uncached pairs, call gpt-5-nano with:
     title, price, condition, description for both listings
   - LLM returns { isComparable: bool, explanation: string }
   - Store verdict in ListingRelationships

5. Compute aggregates
   - Query ListingRelationships WHERE IsComparable = 1
   - Join to Listings for prices, StatusHistory for sold dates
   - Group by listing, compute avg price / count / days to sell / profit
   - Upsert into ListingPredictions

6. Log summary
   - Total listings processed
   - Pinecone queries made
   - LLM calls made vs skipped (cache hits)
   - Comparables found
```

### Bidirectional Cache Optimization

The canonical ordering `(min(A,B), max(A,B))` means:
- When processing listing A and finding candidate B, the verdict is stored as `(A, B)` if A < B
- Later, when processing listing B and finding candidate A in Pinecone results, the cache lookup finds the existing verdict
- This eliminates redundant LLM calls as more listings are processed

## LLM Classification

### Model
`gpt-5-nano` — cheap and fast, sufficient for binary product matching.

### Prompt
```
You are comparing two eBay listings to determine if they are
the same product for pricing comparison purposes.

Listing A:
- Title: {titleA}
- Price: {priceA}
- Condition: {conditionA}
- Description: {descriptionA}

Listing B:
- Title: {titleB}
- Price: {priceB}
- Condition: {conditionB}
- Description: {descriptionB}

Are these the same product (suitable for comparing prices)?
Respond with JSON: { "isComparable": true/false, "explanation": "..." }
```

### Concurrency
- `SemaphoreSlim` with configurable limit (default 20 concurrent calls)
- On failure (timeout, rate limit): log warning, skip pair, continue

### Service Interface
```csharp
record ComparableVerdict(bool IsComparable, string Explanation);

interface IListingComparisonService
{
    Task<ComparableVerdict> Compare(Listing a, Listing b, CancellationToken ct);
}
```

## New Components

### Services
- **`IListingComparisonService`** — Wraps gpt-5-nano call for a single pair
- **`IComparablesEtlService`** — Orchestrates the full pipeline

### ETL Entrypoint
- New CLI flag `--comparables` in `Program.cs`
- Wires up DI and runs `IComparablesEtlService`

### Modified
- **`ISemanticSearchService`** — Keep metadata filter support from current branch
- **`GetActiveListings` API endpoint** — Simplified to join Listings with ListingPredictions

## What We Discard From Current Branch

| Current Branch | Replacement | Reason |
|----------------|-------------|--------|
| `ComparablesRefreshService` | `ComparablesEtlService` + `ListingComparisonService` | Adding LLM verification + cache |
| `ListingPricingComparable` model | `ListingRelationship` + `ListingPrediction` | Redundant junction table eliminated |
| `ListingPricingComparables` table | `ListingRelationships` + `ListingPredictions` | Single verdict table with directional cache |
| Complex in-memory aggregation in API | Simple join to `ListingPredictions` | Pre-computed in ETL |
| Coupled to scrape pipeline | Standalone `--comparables` command | Decoupled, run independently |

## Dry Run Mode

`dotnet run -- --comparables --dry-run`

Runs the full pipeline up to the point of making LLM calls, then stops and reports:

```
Dry Run Summary
===============
Total listings to process:        12,450
Pinecone queries to make:         12,450
Candidate pairs found:            623,000
Already cached verdicts:          184,200
New LLM calls required:           438,800
Estimated cost (gpt-5-nano):      ~$X.XX (based on avg token count)
```

Implementation: the `IComparablesEtlService` accepts a `dryRun` flag. In dry-run mode it executes steps 1-3 (load listings, query Pinecone, check cache) but skips steps 4-5 (LLM calls, aggregation). This gives an accurate count because it does the real Pinecone queries and real cache lookups.

## Migrations

Migration 032 (`CreateListingPricingComparablesTable`) is already applied. We add a new migration that drops the unused table and creates the replacements:

- **033** — `DropComparablesCreateRelationshipsAndPredictions.sql`:
  - Drops `ListingPricingComparables` (superseded by `ListingRelationships`)
  - Creates `ListingRelationships` with unique constraint and indexes
  - Creates `ListingPredictions` with unique constraint and indexes

## What We Keep From Current Branch

- `SemanticSearchService` metadata filter changes
- UI columns for avg price / profit / days to sell (data source changes, display stays)
