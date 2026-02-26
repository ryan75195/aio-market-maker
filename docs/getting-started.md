# Getting Started

Local development setup for AIOMarketMaker.

## Prerequisites

- .NET 8.0 SDK
- SQL Server LocalDB (included with Visual Studio)
- Docker Desktop (for scraper workers)
- Azurite storage emulator (`npm install -g azurite` or via Docker)

## Quick Start

### 1. Start Infrastructure

```bash
# Start Azurite (storage emulator)
npx azurite --blobPort 10000 --queuePort 10001 --tablePort 10002

# Or via Docker:
docker run -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite
```

### 2. Start the Scraper Workers

```bash
cd ../AIOWebScraper/ScraperWorker
dotnet run -- --dedicated-mode
# Runs on http://localhost:7126
```

Or use the Docker workers for parallel processing:
```bash
docker-compose up -d  # From AIOWebScraper directory
```

### 3. Start the API

```bash
cd AIOMarketMaker/AIOMarketMaker.Api
dotnet run
# Runs on http://localhost:5000
```

### 4. Start the Desktop UI

```bash
cd AIOMarketMaker/AIOMarketMaker.Desktop/electron
npm start
```

## Configuration

Create `local.settings.json` in `AIOMarketMaker.Api` and `AIOMarketMaker.Console`:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "SqlConnectionString": "Server=(localdb)\\MSSQLLocalDB;Database=AIOMarketMaker;Trusted_Connection=True;TrustServerCertificate=True;",
    "tableStorageConnectionString": "UseDevelopmentStorage=true",
    "blobStorageConnectionString": "UseDevelopmentStorage=true"
  }
}
```

## Database Setup

The database is created automatically on first run via migrations. To verify:

```bash
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "SELECT COUNT(*) FROM Listings"
```

## Verify Everything Works

1. Open the Desktop UI
2. Add a scrape job (e.g., "PlayStation 5")
3. Click "Start Scrape"
4. Watch the progress update in real-time

## Port Reference

| Service | Port | Purpose |
|---------|------|---------|
| AIOMarketMaker API | 5000 | HTTP endpoints, scraping, scheduling |
| ScraperWorker | 7126 | HTML fetching |
| Azurite Blob | 10000 | Blob storage |
| Azurite Queue | 10001 | Queue storage |
| Azurite Table | 10002 | Table storage |
