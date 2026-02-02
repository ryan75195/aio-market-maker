# Debugging Session Notes - 2026-01-26

## Summary

Successfully debugged and fixed the scraper integration to enable listings to be fetched. The system is now working end-to-end.

## Root Cause Analysis

### Problem 1: Scraper Worker Not Running
- **Symptom**: Scrape jobs completing with 0 listings found
- **Root Cause**: AIOWebScraper ScraperWorker was not running on port 5000
- **Fix**: Started the scraper worker with `dotnet run` (queue mode, not dedicated mode)

### Problem 2: Port Configuration Mismatch
- **Symptom**: 404 errors when calling scraper API
- **Root Cause**: AIOMarketMaker Functions was configured to call port 5000 directly, but the architecture requires:
  1. AIOMarketMaker Functions (port 7071) → calls AIOWebScraper Functions API (port 7126)
  2. AIOWebScraper Functions API → enqueues URLs to Azurite queue
  3. ScraperWorker → dequeues and processes URLs with Playwright
- **Fix**: Updated `ScraperApi__BaseUrl` in `local.settings.json` to `http://localhost:7126`

### Problem 3: Storage Configuration Mismatch (CRITICAL)
- **Symptom**: `IncrementProgressAsync failed for jobId` - 404 ResourceNotFound errors
- **Root Cause**: ScraperWorker was connecting to REAL Azure Storage while AIOWebScraper Functions was using Azurite (local emulator)
  - `appsettings.local.json` had real Azure connection strings for table/blob storage
  - Queue storage was correctly using `UseDevelopmentStorage=true`
  - This caused jobs created in Azurite to be invisible to the worker trying to update them in real Azure
- **Fix**: Updated `appsettings.local.json` to use Azurite for ALL storage:
  ```json
  {
    "tableStorageConnectionString": "UseDevelopmentStorage=true",
    "blobStorageKey": "UseDevelopmentStorage=true",
    "queueStorageConnectionString": "UseDevelopmentStorage=true"
  }
  ```

## Architecture (Correct Configuration)

```
AIOMarketMaker.Functions (port 7071)
    │
    │ HTTP calls to ScraperApi__BaseUrl
    ▼
AIOWebScraper.Functions (port 7126)
    │
    │ Creates job in Azurite Table Storage
    │ Enqueues URLs to Azurite Queue (port 10001)
    ▼
ScraperWorker (queue mode - NO --dedicated-mode flag)
    │
    │ Dequeues from Azurite Queue
    │ Processes with Playwright browser
    │ Saves results to Azurite Blob/Table Storage
    ▼
AIOMarketMaker.Functions polls GetStatus/GetResults
```

## Required Running Services for Local Development

1. **Azurite** (storage emulator) - ports 10000, 10001, 10002
   ```bash
   npx azurite --blobPort 10000 --queuePort 10001 --tablePort 10002
   ```

2. **AIOWebScraper Functions API** - port 7126
   ```bash
   cd AIOWebScraper/AIOWebScraper
   func start --port 7126
   ```

3. **ScraperWorker** (queue mode) - no port, reads from queue
   ```bash
   cd AIOWebScraper/ScraperWorker
   dotnet run  # WITHOUT --dedicated-mode flag!
   ```

4. **AIOMarketMaker Functions** - port 7071
   ```bash
   cd AIOMarketMaker/AIOMarketMaker.Functions
   func start
   ```

5. **Electron App** (optional for UI)
   ```bash
   cd AIOMarketMaker/AIOMarketMaker.Desktop/electron
   npm start
   ```

## Configuration Files Modified

### AIOWebScraper/ScraperWorker/appsettings.local.json
Changed from real Azure Storage to Azurite:
```json
{
  "tableStorageConnectionString": "UseDevelopmentStorage=true",
  "blobStorageKey": "UseDevelopmentStorage=true",
  "queueStorageConnectionString": "UseDevelopmentStorage=true",
  "workerCount": 2
}
```

### AIOMarketMaker/AIOMarketMaker.Functions/local.settings.json
Ensure ScraperApi points to AIOWebScraper Functions (port 7126):
```json
{
  "ScraperApi__BaseUrl": "http://localhost:7126"
}
```

## Current Status (RESOLVED)

- **System is fully working end-to-end**
- Run 3007 completed successfully with **2855 listings added** to database
- Scraper worker successfully processing URLs (2MB+ eBay pages)
- Parser correctly extracting listings from page 1 (155 sold, 242 active)

### Problem 4: MaxListingsToFetch Limit Too Low
- **Symptom**: TotalListingsFound showing only 10 listings despite parser finding hundreds
- **Root Cause**: `Scraping__MaxListingsToFetch` was set to 10 in `local.settings.json`
- **Fix**: Increased to 100 for testing (or remove entirely for production)

## Configuration Files Modified

### AIOMarketMaker/AIOMarketMaker.Functions/local.settings.json
Updated `MaxListingsToFetch` from 10 to 100:
```json
{
  "ScraperApi__BaseUrl": "http://localhost:7126",
  "Scraping__MaxListingsToFetch": "100"
}
```

## Known Behaviors

- Queue messages that were dequeued by crashed worker instances have a visibility timeout (5 minutes)
- Pages beyond results will show "0 items" - parser handles this correctly
- Multiple concurrent scrape runs can cause queue congestion

## Verification Commands

Check scrape progress:
```powershell
Invoke-RestMethod -Uri 'http://localhost:7071/api/history' | Select-Object -First 1
```

Check scraper worker logs for processing:
```
[13:43:16 INF] [b830ab3d] Processing: https://www.ebay.co.uk/itm/157621472225 (attempt 1/3)
[13:43:29 INF] [b830ab3d] SUCCESS: https://www.ebay.co.uk/itm/157621472225 (2268KB)
```

Check listings in parser logs:
```
[14:01:XX] Found 155 listings on sold page 1
[14:01:XX] Found 242 listings on active page 1
```

Trigger new scrape:
```powershell
Invoke-RestMethod -Uri 'http://localhost:7071/api/scrape/start' -Method Post
```

## Completed

- Scrape run 3007 completed with 2855 listings added to database
- Parser confirmed working (selectors still valid for 2026 eBay HTML)
- All storage configuration unified to use Azurite for local development
