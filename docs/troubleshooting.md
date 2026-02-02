# Troubleshooting

Common issues and solutions for AIOMarketMaker.

## Scraping Issues

### Bot Detection (Small HTML)

**Symptom:** Worker logs show HTML sizes < 100KB instead of expected 1.5-2MB.

**Cause:** eBay detected the scraper as a bot or showed a consent page.

**Solutions:**
1. Check proxy is configured: Look for `Proxy configuration: CONFIGURED` in worker startup logs
2. Verify human delays are 1-3 seconds (not 0.5-1.5 seconds)
3. Check wait strategy is `NetworkIdle` for JS-heavy pages
4. Compare working vs broken configurations

```bash
# Check for bot detection in worker logs
powershell -Command "docker logs scraper-queue-worker 2>&1 | Select-String 'bot detection|small HTML|FAILED' | Select-Object -Last 20"
```

### Workers Not Processing

**Symptom:** Queue has messages but no worker activity.

```bash
# Check if workers are running
powershell -Command "docker ps --filter 'name=scraper-queue-worker'"

# Check for worker errors
powershell -Command "docker logs scraper-queue-worker --tail 50 2>&1 | Select-String 'error|exception|fail'"
```

### Stalled Pipeline

**Symptom:** No progress for 60+ seconds, queue is empty.

```bash
# Compare junction table to totals
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "
  SELECT r.Id, r.TotalListingsFound, r.ListingsProcessed,
         (SELECT COUNT(*) FROM ScrapeRunListings WHERE ScrapeRunId = r.Id AND Status = 'Pending') as Pending
  FROM ScrapeRuns r WHERE r.Status IN ('Running', 'Indexing')" -W
```

If Pending > 0 but queue empty, the orchestration may have lost state. Check if Azurite restarted.

## Database Issues

### Connection Failed

**Symptom:** `SqlException: Cannot open database`

**Solutions:**
1. Verify LocalDB is running: `sqllocaldb info MSSQLLocalDB`
2. Start it if stopped: `sqllocaldb start MSSQLLocalDB`
3. Check connection string in `local.settings.json`

### Migration Errors

Migrations run automatically on startup. If they fail:

```bash
# Check migration history
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "SELECT * FROM __MigrationHistory ORDER BY Id DESC" -W
```

**Never delete the database** - it contains production data. Fix migration issues by creating new migrations.

## Azure Functions Issues

### Functions Not Starting

```bash
# Check if port is in use
netstat -ano | findstr :7071

# Kill orphan process if needed
taskkill /PID <pid> /F
```

### Blob Triggers Not Firing

Known issue with Azurite in Docker. See `investigations/2026-01-28-blob-trigger-not-firing.md`.

Workaround: Use the HTTP endpoint approach instead of blob triggers.

## Queue Issues

### Clear Stuck Messages

```bash
az storage message clear --queue-name "scrape-work" \
  --connection-string "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;"
```

### Check Queue Depth

```bash
az storage message peek --queue-name "scrape-work" --num-messages 32 \
  --connection-string "..." --query "length(@)"
```

## Azurite Issues

### Azurite in Docker vs Local

If `az storage` commands return empty but you expect data, Azurite may be in Docker with isolated storage.

```bash
# Check if Azurite is in Docker
docker ps --filter 'ancestor=mcr.microsoft.com/azure-storage/azurite'

# Read Azurite's internal database directly
docker exec azurite cat /data/__azurite_db_blob__.json | head -100
```

### Ports Not Listening

```bash
# Check what's listening
netstat -ano | findstr "10000 10001 10002"

# Verify Docker containers
docker ps --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
```

## Logging

### Enable Verbose Logging

In `host.json`:
```json
{
  "logging": {
    "logLevel": {
      "default": "Information",
      "Function": "Debug"
    }
  }
}
```

### View App Insights (Azure)

```kusto
traces
| where cloud_RoleName contains "aiomarketmaker"
| where severityLevel >= 2
| order by timestamp desc
| take 100
```
