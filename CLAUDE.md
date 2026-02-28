# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

AIOMarketMaker is an eBay data scraping and processing pipeline built on .NET 8.0 that combines web scraping with AI-powered data processing. The system scrapes eBay listings (both active and sold), parses HTML using AngleSharp, and structures the data into clean product records for market analysis.

## Project Structure

The solution consists of these main projects:

### AIOMarketMaker.Api
- ASP.NET Core web API (port 5000)
- HTTP endpoints for scraping, history, jobs, listings
- Inline description fetching, nightly scheduling, background processing
- Entry point: `AIOMarketMaker.Api\Program.cs`

### AIOMarketMaker.Core
- Core domain logic, models, and services
- eBay-specific parsers (AngleSharp), EF Core data layer
- Services: ScrapeJobProcessor, StatusRefreshRunner, DbWriteGate, ComparablesEtlService
- Key components:
  - `EbayScraper.cs`: Orchestrates search and listing scraping
  - `EbaySearchParser.cs`: Parses search results pages
  - `EbayListingParser.cs`: Parses individual product listing pages
  - `WebscraperClient.cs`: HTTP client for external scraper service
  - `EbayUrlBuilder.cs`: Constructs eBay URLs with filters

### AIOMarketMaker.ML
- ONNX variant classifier, training scripts, CUDA utilities
- Embedding service (OpenAI), clustering (HDBSCAN), vector search (USearch)

### AIOMarketMaker.Console
- CLI tools using ITask pattern with auto-discovery via DI
- Tasks: search, pricing, backfill-confidence, comparables, reindex-missing, validation, k-analysis, batch-label, migrate
- Run: `dotnet run --project AIOMarketMaker.Console -- taskname`
- Entry point: `AIOMarketMaker.Console\Program.cs`

### AIOMarketMaker.Tests.*
- Split test projects: Unit, Integration, E2E, Contract, Common
- NUnit with Moq
- Test data: Saved HTML files in `Tests.Common\Data\Listings\`

### AIOWebScraper.Storage.Azure
- External dependency (located in `..\..\AIOWebScraper\`)
- Azure Table Storage and Blob Storage abstractions
- Provides `IJobRepository` for managing scrape jobs

## Key Architecture Patterns

### eBay Scraping Workflow
1. **Search Phase**: `EbayScraper.SearchActiveListings()` or `SearchSoldListings()` queries eBay search pages
2. **Parse Summaries**: `EbaySearchParser` extracts product summaries (ID, title, price, condition) from search results
3. **Fetch Listings**: `EbayScraper.GetItemsFromListings()` retrieves full listing pages via WebscraperClient
4. **Parse Details**: `EbayListingParser` extracts detailed product data (specs, images, description)
5. **Fetch Descriptions**: Separate iframe description sources are fetched and parsed

### External Web Scraper Service
The system depends on an external HTTP scraper service (not in this repo) running at `http://localhost:7126`:
- `POST api/NewJob` - Start a scrape job with URLs
- `GET api/GetStatus?jobId=...` - Poll job status
- `GET api/GetResults?jobId=...` - Retrieve scraped HTML
- `POST api/GetPageHtml` - Synchronous single-page fetch

The `WebscraperClient` wraps this API and handles polling until jobs complete.

### Data Models
- `EbayProductSummary`: Lightweight search result (from search pages)
- `EbayProduct`: Full product with description and specs
- `ExtractedEbayListing`: Internal intermediate representation
- Enums: `Condition`, `BuyingFormat`, `PurchaseFormat`, `EbayListingStatus`

### Dependency Injection
- Registration happens in `ScraperServiceCollectionExtensions.AddEbayScraperPipeline()`
- Requires `StorageConnectionString` configuration for Azure Storage
- Parsers are registered as singletons (stateless)

## Git & Committing

This directory (`AIOMarketMaker/`) is its own git repository, separate from the parent `parent-repo/` repo. Always commit from inside this directory.

```bash
# Correct — commit from the AIOMarketMaker directory
cd AIOMarketMaker
git add <files>
git commit -m "feat: description"

# Wrong — committing from the parent repo won't see AIOMarketMaker files
cd parent-repo
git add AIOMarketMaker/...  # These files are invisible to this repo
```

- **Remote:** `origin` → `https://github.com/ryan75195/AIOMarketMaker.git`
- **Main branch:** `main`
- **Working branch:** typically `master` (local development)

The parent `parent-repo/` repo treats `AIOMarketMaker/` as a nested repo (it has its own `.git/`). Plan docs live in the parent repo at `docs/plans/`.

## Common Commands

### Build the solution
```bash
dotnet build AIOMarketMaker.sln
```

### Run tests
```bash
# Run all tests across all projects
dotnet test AIOMarketMaker.sln

# Run unit tests only (fast, no external deps)
dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj

# Run by project
dotnet test AIOMarketMaker.Tests.Integration/AIOMarketMaker.Tests.Integration.csproj
dotnet test AIOMarketMaker.Tests.E2E/AIOMarketMaker.Tests.E2E.csproj
dotnet test AIOMarketMaker.Tests.Contract/AIOMarketMaker.Tests.Contract.csproj

# Run a specific test
dotnet test --filter "FullyQualifiedName~SearchParserUnit"
```

### Run Console CLI tasks
```bash
dotnet run --project AIOMarketMaker.Console -- help
dotnet run --project AIOMarketMaker.Console -- search "PlayStation 5"
dotnet run --project AIOMarketMaker.Console -- backfill-confidence
```

### Run the API
```bash
dotnet run --project AIOMarketMaker.Api/AIOMarketMaker.Api.csproj
# Runs on http://localhost:5000
```

## Desktop App (Electron + Playwright)

### Project Location
`AIOMarketMaker.Desktop/electron/` — Electron app with Vue 3 (CDN-loaded), Chart.js, vanilla CSS.

### Running the Desktop App
```bash
cd AIOMarketMaker.Desktop/electron
npm install    # First time only
npm run dev    # Launch Electron app
```

The app connects to the API at `http://localhost:5000` — start the API first.

### Desktop Test Suite

Tests use **Vitest** as the runner with **Playwright** for Electron automation. No separate Playwright config file — Playwright is configured inline in test files.

```bash
cd AIOMarketMaker.Desktop/electron

# Run all desktop tests (unit + integration)
npm test

# Watch mode for development
npm run test:watch
```

**Test files** in `tests/`:

| File | Type | What it tests |
|------|------|---------------|
| `progress.test.js` | Unit (Vitest only) | Progress calculation functions from `src/progress.js` — percentages, ETA, rate, formatting |
| `ui.test.js` | Integration (Vitest + Playwright) | Electron app launch, navigation, batch list, batch detail, progress bars, stats banner |
| `e2e-monitor.js` | E2E script | Long-running batch monitoring with stall detection. Run with `node tests/e2e-monitor.js` (optionally `--start` to trigger a new batch) |
| `audit-ui.js` | Visual audit script | Takes screenshots of every UI view for regression review. Run with `node tests/audit-ui.js` |

### Writing Playwright Tests for Electron

Tests use Playwright's `_electron` module to launch and control the app:

```javascript
import { _electron as electron } from 'playwright';

// Launch the Electron app
const electronApp = await electron.launch({
  args: [path.join(__dirname, '..', 'main.js')],
  env: { ...process.env, NODE_ENV: 'test' },
});

// Get the first window
const page = await electronApp.firstWindow();

// Wait for Vue to mount (app loads data on mount)
await page.waitForTimeout(2000);

// Interact with the UI
await page.click('.sidebar button:nth-child(2)');
const title = await page.locator('.view-title').textContent();

// Take a screenshot
await page.screenshot({ path: 'tests/screenshots/test.png' });

// Clean up
await electronApp.close();
```

**Key locator patterns used in existing tests:**
- `.sidebar button` — navigation buttons
- `.batch-row`, `.batch-card` — batch list items
- `.progress-bar`, `.progress-fill`, `.progress-text` — progress indicators
- `.stats-banner`, `.stat` — stats display
- `.status-badge` — run status badges

**Wait strategies:**
- `page.waitForTimeout(2000)` — wait for Vue mount + API data load
- Playwright locators auto-wait for elements by default (30s timeout)

### Screenshots

Test screenshots are saved to `tests/screenshots/`:
- `tests/screenshots/` — general test captures
- `tests/screenshots/audit/` — systematic UI audit (all views)
- `tests/screenshots/monitor/` — E2E monitoring captures with stall detection

## Important Configuration

### local.settings.json
`AIOMarketMaker.Api` and `AIOMarketMaker.Console` require `local.settings.json`.

**local.settings.json** (key settings):
```json
{
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "SqlConnectionString": "Server=(localdb)\\MSSQLLocalDB;Database=AIOMarketMaker;Trusted_Connection=True;TrustServerCertificate=True;",
    "Scraping:MaxListingsToFetch": "100",
    "Scraping:DefaultLookbackDays": "180"
  }
}
```
- `SqlConnectionString` points to SQL Server LocalDB for local development
- `Scraping:MaxListingsToFetch` limits listings per job (can be overridden via UI Settings)

### External Dependencies
- The external web scraper service must be running on `http://localhost:7126`
- Azure Storage Emulator or real Azure Storage account for job persistence

### Running the Scraper Dependency
AIOMarketMaker depends on AIOWebScraper for HTML fetching. Start it before running the API:

```bash
# From the AIOWebScraper folder
cd ../AIOWebScraper/ScraperWorker
dotnet run -- --dedicated-mode
# Runs on http://localhost:7126
```

### Clearing Azurite Queues
To clear the scrape work queue during development:
```bash
az storage message clear --queue-name "scrape-work" \
  --connection-string "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;"
```

### Debugging Failed Scrapes
When scrapes return small/invalid HTML:
1. **Check response size**: Real eBay pages are ~1.6MB, consent/bot pages are 50-70KB
2. **Look for bot detection keywords**: `captcha`, `blocked`, `cookie consent`, `gdpr`
3. **Verify proxy is configured**: Check scraper startup logs for "Proxy configuration: CONFIGURED"
4. **Human delays matter**: Delays under 1 second trigger bot detection

## Migration & Refactor Planning - LESSONS LEARNED

**Context:** The 2026-01-31 Durable Functions migration introduced regressions because the plan didn't explicitly document behavioral requirements.

### Required Sections in Any Migration Plan

#### 1. Behavioral Parity Checklist
Document EVERY behavior of the existing system before writing code:

```markdown
## Scrape Pipeline Behaviors to Preserve
- [ ] Multi-page search (old: searched until exhausted)
- [ ] Sold listing search (old: separate phase before active)
- [ ] Filter only terminal statuses (Sold/Ended/OutOfStock)
- [ ] Re-scrape active listings for price updates
- [ ] Create ListingStatusHistory on status changes
- [ ] Status progression validation (CanUpdateStatus)
- [ ] Retry logic with exponential backoff
```

#### 2. Explicit "What We're Dropping" Section
If simplifying, list EVERY feature being removed:

| Feature | Why Dropping | User Impact |
|---------|--------------|-------------|
| Multi-page search | Not needed for <100 results | May miss listings |
| Sold search | Separate initiative | Can't track sales |

#### 3. Tests Must Encode Business Requirements

**The 2026-01-31 regression happened because:**
```csharp
// The test created a listing with NULL status
var existing = new Listing { ListingId = "itm002" };
// But never tested with terminal statuses!
// var sold = new Listing { ListingId = "itm002", ListingStatus = "Sold" };
```

**Correct approach:**
```csharp
[Test]
public void Should_skip_terminal_listings_but_rescrape_active()
{
    var activeListing = new Listing { ListingStatus = "Active" };
    var soldListing = new Listing { ListingStatus = "Sold" };

    // Assert: active included (re-scrape for price updates)
    // Assert: sold excluded (terminal - nothing to update)
}
```

#### 4. Red Flags to Watch For
- "Filter existing" without specifying exact criteria
- "Simplified" without listing dropped features
- Tests verify "messages sent" not "correct filtering"
- No behavioral parity checklist in plan
- Integration tests marked `[Explicit]` (don't run in CI)

### Current Pipeline Behaviors (Reference)

The simplified pipeline SHOULD:
- **Search:** Only page 1 currently (regression - should be multi-page)
- **Filter:** Only terminal statuses (Sold, Ended, OutOfStock) - fixed in commit b4b69e1
- **Re-scrape:** Active listings for price/status updates
- **Track:** ListingStatusHistory on status changes (NOT IMPLEMENTED - regression)
- **Sold Search:** NOT IMPLEMENTED (intentional simplification? undocumented)

## Testing Strategy

### Test Conventions
- Use **NUnit** as the test framework with **Moq** for mocking
- Name tests using `Should_do_something_when_condition` format (e.g., `Should_return_zero_counts_when_indexing_empty_list`)
- **Separate test levels into different files**:
  - `{Service}Tests.cs` - Unit tests (no external dependencies)
  - `{Service}IntegrationTests.cs` - Integration tests requiring real services
- Mark integration tests with `[Category("Integration")]` and `[Explicit]` attributes
- Use `Assert.Multiple()` to group related assertions

### Test Organization
```
AIOMarketMaker.Tests.Common/    # Shared utilities + Data/
AIOMarketMaker.Tests.Unit/      # Fast unit tests, no external deps
AIOMarketMaker.Tests.Integration/ # Tests with real services
AIOMarketMaker.Tests.E2E/       # Full system tests + Infrastructure/
AIOMarketMaker.Tests.Contract/  # HTML structure validation
```

### Contract Tests
HTML structure can change. Contract tests (`ContractTests/`) validate that parsers still work with saved HTML snapshots of different listing types:
- `ActiveBuyItNowListing.html`
- `ActiveAuctionWithOfferAvailable.html`
- `SoldBuyNowListing.html`
- `SoldBidListing.html`
- `BiddingEndedNoSale.html`

When eBay changes their HTML, these tests will fail. Update the saved HTML and adjust parser selectors accordingly.

### Unit Tests
Focus on parser logic (`EbaySearchParser`, `EbayListingParser`) with AngleSharp documents constructed via `PageBuilder` utility.

## Known Patterns

### Parsing Philosophy
- Parsers use CSS selectors with AngleSharp's `QuerySelector/QuerySelectorAll`
- Price extraction handles multiple currency formats and decimal separators
- Date parsing uses `CultureInfo.GetCultureInfo("en-GB")` with `DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal`
- Condition mapping uses a `Dictionary<string, Condition>` for flexible text matching

### Error Handling
- Parsers return `null` or default values for missing data rather than throwing
- `try/catch` blocks are used sparingly (e.g., shipping price parsing)
- The `WebscraperClient.RunJobAsync()` polls every 5 seconds until job completes
- **Fail Fast for External Services**: Do NOT swallow errors from external services like Pinecone, OpenAI, etc. Let exceptions propagate so issues are caught early rather than silently degrading functionality. If a service is optional, configure it to not be used at all rather than catching and ignoring errors at runtime.

### Coding Standards

**Naming:**
- **Don't suffix async methods with `Async`.** The return type (`Task<T>`) already communicates this.

**Type Safety:**
- **No tuples as return types or parameters.** If a method returns multiple values, define a named record.
- **No anonymous types.** Always use named records for data objects, including HTTP response bodies, error responses, and projections.
- **Prefer `IEnumerable<T>` over `IReadOnlyList<T>` or `List<T>`** in method signatures (parameters and return types). Use `List<T>` only as a local implementation detail.

**File Organization:**
- **Interfaces at the top of the file**, above records and class implementations.
- **Records above the class.** DTOs/records declared in the same file go between the namespace and the class definition.

**Style:**
- **Always use braces for `if`, `else`, `for`, `foreach`, `while`, and `using` blocks**, even for single-line bodies.

**Architecture:**
- **Thin controllers/triggers.** HTTP/timer triggers handle only request parsing, response building, and status codes. Business logic belongs in a service.
- **Don't leak entities across boundaries.** Services return their own result types (e.g., `EnqueuedScrapeRun`), not EF Core entities (e.g., `ScrapeRun`).
- **Clean, small high-level methods.** Public trigger/controller methods should be short and read like a summary. Extract details into well-named private or service methods.
- **Self-contained helper methods.** When extracting helpers, bundle all coupled concerns into the helper — state transitions, logging, and the work itself. If a phase sets status, does work, and logs the result, all three belong in the helper. The calling method should read as a clean pipeline without interleaved infrastructure.

### Anti-Patterns to Avoid
- Don't parse prices with culture-specific decimal separators without normalization
- Don't assume all listings have descriptions or item specifics
- Don't skip duplicate detection in paginated searches (use `HashSet<string>` for seen IDs)
- Don't use `#region` / `#endregion` separators in code
- Don't use `List<T>` in function signatures; prefer `IEnumerable<T>` for both inputs and outputs

### Expensive or Irreversible Operations — Confirm First
Before running scripts that cost money (API calls), take significant time, or produce datasets that downstream work depends on, **explicitly surface all design decisions that affect data quality** and get user approval. This includes:
- **Data truncations**: column limits, token caps, string slicing (e.g., `desc[:300]`, `reasoning[:200]`)
- **Data filtering**: what gets excluded and why (e.g., categories with <10 listings)
- **Sampling strategies**: how data is distributed across categories, similarity bands, etc.
- **Hardcoded defaults**: batch sizes, worker counts, sleep timers, model parameters

Present these as a pre-run checklist — not buried in code — so the user can make informed decisions before spending money and time.

### Debugging the API
- **Always run locally first**: Run the project locally with `dotnet run` instead of deploying. Local debugging is much faster and provides immediate feedback.
- Use `local.settings.json` to configure connection strings for local development
- The Api project can connect to the real Azure SQL database locally for testing

## Database Management

### Database Architecture
- **Local development**: SQL Server LocalDB (`(localdb)\MSSQLLocalDB`, database `AIOMarketMaker`)
- **Azure deployment**: Azure SQL Database
- **Connection string**: `SqlConnectionString` in `local.settings.json`
- **ORM**: Entity Framework Core with `EtlDbContext`

### Querying the Database Locally
Use `sqlcmd` to query the LocalDB instance:
```bash
# View scrape runs
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "SELECT Id, Status, CurrentPhase, TotalListingsFound, ListingsProcessed FROM ScrapeRuns ORDER BY Id DESC" -W

# View scrape run listings status
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "SELECT ScrapeRunId, Status, COUNT(*) as Count FROM ScrapeRunListings GROUP BY ScrapeRunId, Status ORDER BY ScrapeRunId" -W

# View listings count by job
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "SELECT ScrapeJobId, COUNT(*) as Count FROM Listings GROUP BY ScrapeJobId" -W

# View scrape jobs
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "SELECT Id, SearchTerm, IsEnabled FROM ScrapeJobs" -W
```

### CRITICAL: Never Delete the Database
The database contains valuable production data. **NEVER** suggest deleting or recreating the database to fix schema issues. Always use migrations instead.

### Migration System
This project uses a custom SQL migration system with **embedded resources**:
- **SQL Server migrations**: `AIOMarketMaker.Core/Data/Migrations/SqlServer/*.sql` (used for both local and Azure)
- The `MigrationRunner` in `AIOMarketMaker.Core.Data.Migrations` applies pending migrations on startup
- Applied migrations are tracked in the `__MigrationHistory` table
- **IMPORTANT**: After adding a migration file, rebuild the Core project to embed it

### Adding New Tables/Schema Changes
When adding new EF Core entities or modifying the schema:
1. Create the model class in `AIOMarketMaker.Core/Data/Models/`
2. Add the `DbSet<T>` to `EtlDbContext`
3. Configure the entity in `OnModelCreating()`
4. **Create migration SQL file** in `AIOMarketMaker.Core/Data/Migrations/SqlServer/` with next sequence number
5. Use `IF NOT EXISTS` patterns for idempotency (SQL Server syntax)
6. **Rebuild Core project** to embed the new migration
7. The migration will be applied automatically on next application restart

### Example Migration Format (SQL Server)
```sql
-- Migration: 003_CreateProductStatusHistoryTable
-- Description: Creates the ProductStatusHistory table
-- Date: 2025-11-28

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ProductStatusHistory')
BEGIN
    CREATE TABLE ProductStatusHistory (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        -- columns...
        CONSTRAINT FK_ProductStatusHistory_Products FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE CASCADE
    );
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ProductStatusHistory_ProductId')
BEGIN
    CREATE INDEX IX_ProductStatusHistory_ProductId ON ProductStatusHistory (ProductId);
END
```
