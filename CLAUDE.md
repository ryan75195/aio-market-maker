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
  - Unit tests: `UnitTests\` (parser logic, scraper logic)
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
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~SearchParserUnitTests"
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
Both `AIOMarketMaker.Api` and `AIOMarketMaker.Etl` require `local.settings.json` with:
```json
{
  "Values": {
    "StorageConnectionString": "UseDevelopmentStorage=true",
    ...
  }
}
```

### External Dependencies
- The external web scraper service must be running on `http://localhost:7126`
- Azure Storage Emulator or real Azure Storage account for job persistence

## Testing Strategy

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

## Database Management

### CRITICAL: Never Delete the Database
The SQLite database (`etl.db`) contains valuable production data. **NEVER** suggest deleting or recreating the database to fix schema issues. Always use migrations instead.

### Migration System
This project uses a custom SQL migration system located in `AIOMarketMaker.Etl/Data/Migrations/`:
- Migrations are plain `.sql` files with sequential numbering: `001_InitialCreate.sql`, `002_CreateProductsTable.sql`, etc.
- The `MigrationRunner` class automatically applies pending migrations on startup
- Applied migrations are tracked in the `__MigrationHistory` table

### Adding New Tables/Schema Changes
When adding new EF Core entities or modifying the schema:
1. Create the model class in `AIOMarketMaker.Etl/Data/Models/`
2. Add the `DbSet<T>` to `EtlDbContext`
3. Configure the entity in `OnModelCreating()`
4. **Create a new migration SQL file** in `Data/Migrations/` with the next sequence number
5. Use `CREATE TABLE IF NOT EXISTS` and `CREATE INDEX IF NOT EXISTS` for idempotency
6. The migration will be applied automatically on next application restart

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
