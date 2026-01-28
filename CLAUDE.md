# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

AIOMarketMaker is an eBay data scraping and processing pipeline built on .NET 8.0 that combines web scraping with AI-powered data processing. The system scrapes eBay listings (both active and sold), parses HTML using AngleSharp, and structures the data into clean product records for market analysis.

## Project Structure

The solution consists of 5 main projects:

### AIOMarketMaker.Api
- Azure Functions application (v4)
- HTTP-triggered endpoints for eBay scraping operations
- Entry point: `AIOMarketMaker\Program.cs`
- Controllers: `AIOMarketMaker\Controllers\EbayController.cs` (stub endpoints)
- DI configuration: `ScraperServiceCollectionExtensions.cs`

### AIOMarketMaker.Core
- Core domain logic and models
- eBay-specific parsers and services
- Key components:
  - `EbayScraper.cs`: Orchestrates search and listing scraping
  - `EbaySearchParser.cs`: Parses search results pages
  - `EbayListingParser.cs`: Parses individual product listing pages
  - `WebscraperClient.cs`: HTTP client for external scraper service
  - `EbayUrlBuilder.cs`: Constructs eBay URLs with filters

### AIOMarketMaker.Etl
- Standalone console application for ETL operations
- Entry point: `AIOMarketMaker.Etl\Program.cs`
- Designed to run as a cron job for batch processing
- Currently has a demo implementation that scrapes PS5 listings and exports to CSV

### AIOMarketMaker.Tests
- NUnit test project with Moq
- Test categories:
  - Unit tests: `Unit\` (parser logic, scraper logic)
  - Contract tests: `ContractTests\` (HTML structure validation)
  - Integration tests: `Integration\` (end-to-end scraping)
- Test data: Saved HTML files in `Data\Listings\` for contract testing

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

## Common Commands

### Build the solution
```bash
dotnet build AIOMarketMaker.sln
```

### Run tests
```bash
# Run all tests
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj

# Run a specific test
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~SearchParserUnit"
```

### Run the ETL console app
```bash
dotnet run --project AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj
```

### Run the Azure Functions API locally
```bash
dotnet run --project AIOMarketMaker/AIOMarketMaker.Api.csproj
```
Or use Azure Functions Core Tools:
```bash
func start --cwd AIOMarketMaker
```

## Important Configuration

### local.settings.json
Both `AIOMarketMaker.Functions` and `AIOMarketMaker.Etl` require `local.settings.json`.

**ETL local.settings.json** (key settings):
```json
{
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "SqlConnectionString": "Server=(localdb)\\MSSQLLocalDB;Database=AIOMarketMaker;...",
    "Scraping:MaxListingsToFetch": "100",
    "Scraping:DefaultLookbackDays": "180"
  }
}
```
- Use `SqlConnectionString` for SQL Server or `SqliteConnectionString` for SQLite (not both)
- `Scraping:MaxListingsToFetch` limits listings per job (can be overridden via UI Settings)

### External Dependencies
- The external web scraper service must be running on `http://localhost:7126`
- Azure Storage Emulator or real Azure Storage account for job persistence

### Running the Scraper Dependency
AIOMarketMaker depends on AIOWebScraper for HTML fetching. Start it before running ETL:

```bash
# From the AIOWebScraper folder
cd ../AIOWebScraper/ScraperWorker
dotnet run -- --dedicated-mode
# Runs on http://localhost:5000, proxied via Azure Functions on 7126
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
AIOMarketMaker.Tests/
├── Unit/           # Isolated unit tests with mocks
├── ContractTests/       # Tests against saved HTML snapshots
├── Integration/         # End-to-end tests with real services
├── Utils/               # Test helpers (PageBuilder, Assertions)
└── Data/                # Test data files
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

### Anti-Patterns to Avoid
- Don't parse prices with culture-specific decimal separators without normalization
- Don't assume all listings have descriptions or item specifics
- Don't skip duplicate detection in paginated searches (use `HashSet<string>` for seen IDs)
- Don't use `#region` / `#endregion` separators in code
- Don't use `List<T>` in function signatures; prefer `IEnumerable<T>` for both inputs and outputs

### Debugging Azure Functions
- **Always run locally first**: When debugging Azure Functions issues, run the project locally with `func start` or `dotnet run` instead of deploying and waiting for GitHub Actions. Local debugging is much faster and provides immediate feedback.
- Use `local.settings.json` to configure connection strings for local development
- The Functions project can connect to the real Azure SQL database locally for testing

### Azure Functions API Access

**Retrieving Function Keys:**
```bash
# Get all function app keys (includes default function key and master key)
az functionapp keys list \
  --name YOUR-FUNCTION-APP \
  --resource-group rg-aiomarketmaker-dev

# Output includes:
# - functionKeys.default: Use for calling HTTP-triggered functions
# - masterKey: Admin access (use sparingly)
# - systemKeys.durabletask_extension: For Durable Functions management
```

**Triggering Manual Scrape:**
```bash
# Trigger manual scrape via HTTP endpoint
curl -X POST "https://YOUR-FUNCTION-APP.azurewebsites.net/api/TriggerManualScrapeHttp?code=<FUNCTION_KEY>"
```

**Checking Orchestration Status:**
```bash
# Get orchestration status using Durable Functions HTTP API
curl "https://YOUR-FUNCTION-APP.azurewebsites.net/runtime/webhooks/durabletask/instances/<INSTANCE_ID>?code=<SYSTEM_KEY>"
```

### Durable Functions Orchestration Debugging

**Key Orchestrators:**
- `JobOrchestrator` - Main orchestrator, one per enabled job with isolated ScrapeRun
- `ScrapeUrlOrchestrator` - Handles single URL scraping with retry
- `ScrapeOrchestrator` - ARCHIVED (was single-run-for-all-jobs, now replaced by per-job architecture)

**Per-Job ScrapeRuns Architecture:**
Each enabled job gets its own `ScrapeRun` record with isolated progress tracking:
- `StartScrapeTrigger` creates separate ScrapeRun per job (with `JobId` foreign key)
- Each run has independent `CurrentPhase`, `TotalListingsFound`, `ListingsProcessed`
- History API returns `JobId` and `JobSearchTerm` for each run
- Prevents race conditions where multiple jobs overwrote each other's progress

**Poison Message Debugging:**
When orchestrations fail repeatedly, messages move to poison queues:
```bash
# Check for poison messages in the storage account
az storage message peek \
  --queue-name funcaiomarketmakerdev-control-02-poison \
  --connection-string "$STORAGE_CONNECTION_STRING" \
  --num-messages 5
```

**Common Failure Points:**
1. `GetScrapedHtmlActivity` - Bot detection (small HTML < 100KB)
2. `ParseSearchPageActivity` - Selector changes (0 products from large HTML)
3. `FilterNewListingsActivity` - All listings filtered as duplicates (normal behavior)

**App Insights Queries for Debugging:**
```kusto
// Find orchestration failures
traces
| where message contains "Error" or severityLevel >= 3
| where cloud_RoleName contains "aiomarketmaker"
| order by timestamp desc

// Check for bot detection warnings
traces
| where message contains "bot detection" or message contains "small HTML"
| order by timestamp desc

// Count listings found per orchestration run
traces
| where message contains "Found" and message contains "listings"
| project timestamp, message
| order by timestamp desc
```

### host.json Logging Configuration

**Critical Settings** (in `AIOMarketMaker.Functions/host.json`):
```json
{
  "logging": {
    "logLevel": {
      "default": "Information",  // NOT "Warning" - you'll miss activity logs
      "DurableTask.AzureStorage": "Warning",  // Reduce noise
      "DurableTask.Core": "Warning"
    },
    "applicationInsights": {
      "samplingSettings": { "isEnabled": true }
      // Do NOT set "excludedTypes": "Request"
    }
  }
}
```

## Database Management

### CRITICAL: Never Delete the Database
The SQLite database (`etl.db`) contains valuable production data. **NEVER** suggest deleting or recreating the database to fix schema issues. Always use migrations instead.

### Migration System
This project uses a custom SQL migration system with **embedded resources**:
- **SQLite migrations**: `AIOMarketMaker.Core/Data/Migrations/*.sql` (embedded in Core assembly)
- **SQL Server migrations**: `AIOMarketMaker.Core/Data/Migrations/SqlServer/*.sql` (for Azure deployment)
- The `MigrationRunner` in `AIOMarketMaker.Core.Data.Migrations` applies pending migrations on startup
- Applied migrations are tracked in the `__MigrationHistory` table
- **IMPORTANT**: After adding a migration file, rebuild the Core project to embed it

### Adding New Tables/Schema Changes
When adding new EF Core entities or modifying the schema:
1. Create the model class in `AIOMarketMaker.Core/Data/Models/`
2. Add the `DbSet<T>` to `EtlDbContext`
3. Configure the entity in `OnModelCreating()`
4. **Create migration SQL file** in `AIOMarketMaker.Core/Data/Migrations/` with next sequence number
5. **Create SQL Server version** in `SqlServer/` subfolder with idempotent syntax (IF NOT EXISTS)
6. Use `CREATE TABLE IF NOT EXISTS` and `CREATE INDEX IF NOT EXISTS` for idempotency
7. **Rebuild Core project** to embed the new migration
8. The migration will be applied automatically on next application restart

### Example Migration Format
```sql
-- Migration: 003_CreateProductStatusHistoryTable
-- Description: Creates the ProductStatusHistory table
-- Date: 2025-11-28

CREATE TABLE IF NOT EXISTS ProductStatusHistory (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    -- columns...
    FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_ProductStatusHistory_ProductId
ON ProductStatusHistory (ProductId);
```
