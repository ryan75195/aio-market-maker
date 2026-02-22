# Description Backfill Design

**Goal:** Fetch descriptions for ~9,840 listings with `DescriptionStatus = 'pending'`, parse them, store in DB, and embed+index in the local USearch vector store.

**Command:** `dotnet run -- --backfill-descriptions` in the ETL project.

## Flow

1. Query all listings where `DescriptionStatus = 'pending'`
2. Display pre-run checklist (count, estimated cost, concurrency) and await confirmation
3. Fetch descriptions concurrently (50 parallel):
   - Build URL via `EbayUrlBuilder.BuildDescriptionUrl(listingId)`
   - Fetch via direct HttpClient (no scraper — `itm.ebaydesc.com` doesn't need Playwright)
   - Parse with `EbayListingParser.ParseDescription()`
   - Update `Listing.Description` and `DescriptionStatus` in DB
4. Embed successfully fetched descriptions in batches of 50:
   - Text: `Title + " " + Description` (truncated to 6,000 chars)
   - Call `EmbeddingService.GetEmbeddings()` batch API
   - Upsert into USearch vector index
5. Save vector index to disk

## Key Decisions

- **No proxy needed** — `itm.ebaydesc.com` serves HTML without bot detection
- **Direct HttpClient** — simple GET, no Playwright
- **Concurrency:** 50 parallel HTTP fetches (matches `--clean-descriptions`)
- **Batching:** 50 per embedding batch (matches existing pattern)
- **Truncation:** 6,000 chars before embedding (matches existing pattern)
- **`--limit N`** flag for testing subsets

## Error Handling

- Failed fetch → `DescriptionStatus = 'failed'`, log, continue
- Empty description → `DescriptionStatus = 'missing'`, continue
- Failed embedding → log error, skip listing, continue

## Cost

- **OpenAI:** ~$0.30-0.40 for ~9,840 embeddings
- **Runtime:** ~5-10 minutes
