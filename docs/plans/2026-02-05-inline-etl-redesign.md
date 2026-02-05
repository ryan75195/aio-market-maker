# Inline ETL Redesign

## Problem

The current queue-based ETL architecture is unstable. Jobs stall with unprocessed listings because:

- HTTP callbacks from workers to ProcessListingEndpoint fail silently
- Queue messages are marked complete even when the ETL never learns about them
- No stall detection or recovery mechanism exists
- Two competing tracking systems (Table Storage + SQL) create confusion
- Azure Functions timeout constraints forced a distributed architecture that's hard to debug

The root cause is architectural: the system distributes work across queues, workers, and callbacks when a simpler inline approach would eliminate every failure mode.

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Hosting model | ASP.NET API (drop Azure Functions) | No timeout limits, safe fire-and-forget, simpler DI |
| Description fetching | Inline via `GetPageHtml` with `SemaphoreSlim(15)` | Same parallelism, zero orchestration |
| Scraper infrastructure | Docker Compose cluster with nginx LB | Scale by adding replicas, ETL sees single URL |
| Job scheduling | `BackgroundService` for nightly runs | Replaces timer trigger, no external dependency |
| Error handling | Skip failed descriptions, mark as "missing" | Matches current behavior, run always completes |
| Progress tracking | Direct DB updates during processing loop | UI polls history API, same as today |
| `ScrapeRunListings` table | Drop | No per-listing tracking needed when processing is inline |
| HTTP trigger pattern | Return 202 Accepted, process in background | UI stays responsive, `BackgroundService` keeps task alive |

## Architecture

### Before (current)

```
ScrapeTrigger → scrape-jobs queue → ScrapeJobQueueTrigger → ScrapeJobProcessor
  → scrape-work queue → 15 Docker workers → blob storage → HTTP callback
  → ProcessListingEndpoint → CounterService → CompletionCheckTrigger
```

9 components, 4 async boundaries, 2 tracking systems.

### After

```
Electron UI
    │
    ├── POST /scrape/start ──→ ASP.NET API (ETL)
    ├── GET /history        ──→     │
    │                               ├── ScrapeJobProcessor
    │                               │     ├── search (GetPageHtml)
    │                               │     ├── classify
    │                               │     ├── fetch descriptions (x15 concurrent)
    │                               │     ├── parse + save + index
    │                               │     └── mark complete
    │                               │
    │                               └── NightlyScrapeService (BackgroundService)
    │
    └── ScraperWorker cluster (Docker Compose, dedicated mode)
```

3 processes, 0 async boundaries, 0 tracking systems.

## Component Design

### 1. ASP.NET API Host

Replaces both Azure Functions projects (port 7071 history API + port 7072 ETL triggers) with a single ASP.NET API.

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register services (same as current Program.cs minus Functions SDK)
builder.Services.AddDbContext<EtlDbContext>(options => ...);
builder.Services.AddScoped<IScrapeJobProcessor, ScrapeJobProcessor>();
builder.Services.AddHostedService<NightlyScrapeService>();
// ... parsers, indexing, scraper client

var app = builder.Build();

// Scrape triggers
app.MapPost("/api/scrape/start", ScrapeEndpoints.StartScrape);

// History API (moved from Functions project)
app.MapGet("/api/history", HistoryEndpoints.GetHistory);
app.MapGet("/api/history/{id}", HistoryEndpoints.GetRun);

// Health check
app.MapGet("/health", () => Results.Ok());

app.Run();
```

Single port, single process. The Electron UI points here for everything.

### 2. ScrapeJobProcessor (Rewritten Core Loop)

The processor owns the full lifecycle of a scrape run. Two public methods:

- `CreateRun(job, triggerType)` - creates ScrapeRun in DB, returns immediately
- `Execute(run, job)` - does all work, updates progress, marks complete/failed

```csharp
public async Task Execute(ScrapeRun run, ScrapeJobConfig job)
{
    try
    {
        // Phase 1: Search (unchanged)
        var soldSummaries = await SearchSoldListings(run, job.SearchTerm);
        var activeSummaries = await SearchActiveListings(run, job.SearchTerm);

        // Phase 2: Classify (unchanged)
        var classified = await ClassifyListings(run, activeSummaries, soldSummaries, job.Id);

        // Phase 3: Update existing from summary (unchanged)
        if (classified.ToUpdateFromSummary.Count > 0)
            await UpdateListingsFromSummary(run, classified.ToUpdateFromSummary, ...);

        // Phase 4: Fetch descriptions inline
        if (classified.ToScrape.Count > 0)
            await FetchAndProcessDescriptions(run, classified.ToScrape, job.Id);

        // Phase 5: Complete
        await MarkCompleted(run);
    }
    catch (Exception ex)
    {
        run.Status = "Failed";
        run.ErrorMessage = ex.Message;
        run.CompletedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
    }
}
```

### 3. Inline Description Fetching

The key architectural change. Replaces queue + workers + callbacks with a concurrent loop.

```csharp
private async Task FetchAndProcessDescriptions(
    ScrapeRun run, List<IEbayProductSummary> listings, int jobId)
{
    await SetPhase(run, "Indexing", status: "Indexing");

    var concurrency = new SemaphoreSlim(15);
    var processed = 0;

    var tasks = listings.Select(async summary =>
    {
        await concurrency.WaitAsync();
        try
        {
            await ProcessSingleDescription(run, summary, jobId);
        }
        finally
        {
            concurrency.Release();
            var count = Interlocked.Increment(ref processed);

            // Update progress every 10 listings
            if (count % 10 == 0)
            {
                run.ListingsProcessed = count;
                await _dbContext.SaveChangesAsync();
            }
        }
    });

    await Task.WhenAll(tasks);

    // Final progress update
    run.ListingsProcessed = processed;
    await _dbContext.SaveChangesAsync();
}

private async Task ProcessSingleDescription(
    ScrapeRun run, IEbayProductSummary summary, int jobId)
{
    var descriptionUrl = _urlBuilder.BuildDescriptionUrl(summary.ListingId!);

    try
    {
        var html = await _webscraperClient.GetPageHtml(descriptionUrl);
        var document = await ParseHtml(html);
        var description = _listingParser.ParseDescription(document);

        await SaveListing(summary, jobId, description);
        await _indexingService.Index(listing, embedContent: true);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to fetch description for {ListingId}", summary.ListingId);
        await SaveListing(summary, jobId, description: null, descriptionStatus: "missing");
    }
}
```

**Why this works:**
- `GetPageHtml` is a synchronous HTTP call to the scraper cluster - request in, response out
- Failure = HTTP error or timeout, handled immediately with try/catch
- No lost messages, no orphaned state, no completion detection
- Progress updates are batched (every 10 listings) to reduce DB writes
- `Interlocked.Increment` for thread-safe counting

### 4. Fire-and-Forget HTTP Trigger

```csharp
public static class ScrapeEndpoints
{
    public static async Task<IResult> StartScrape(
        IScrapeJobProcessor processor,
        EtlDbContext db,
        HttpRequest req)
    {
        var jobs = await db.ScrapeJobs.Where(j => j.IsEnabled)
            .Select(j => new ScrapeJobConfig(j.Id, j.SearchTerm))
            .ToListAsync();

        if (!jobs.Any())
            return Results.Ok(new { message = "No enabled jobs" });

        // Create runs immediately so UI can see them
        var runs = new List<ScrapeRun>();
        foreach (var job in jobs)
        {
            var run = await processor.CreateRun(job, "Manual");
            runs.Add(run);
        }

        // Process in background - safe because ASP.NET host stays alive
        _ = Task.Run(async () =>
        {
            foreach (var (job, run) in jobs.Zip(runs))
            {
                try
                {
                    await processor.Execute(run, job);
                }
                catch (Exception ex)
                {
                    // Execute already marks run as Failed internally
                }
            }
        });

        return Results.Accepted(value: new
        {
            runs = runs.Select(r => new { r.Id, r.JobId, r.Status })
        });
    }
}
```

### 5. Nightly Schedule

```csharp
public class NightlyScrapeService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NightlyScrapeService> _logger;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var next2AM = now.Date.AddHours(2);
            if (next2AM <= now) next2AM = next2AM.AddDays(1);

            var delay = next2AM - now;
            _logger.LogInformation("Next nightly scrape at {Time} ({Delay})", next2AM, delay);
            await Task.Delay(delay, ct);

            using var scope = _scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<IScrapeJobProcessor>();
            var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();

            var jobs = await db.ScrapeJobs.Where(j => j.IsEnabled)
                .Select(j => new ScrapeJobConfig(j.Id, j.SearchTerm))
                .ToListAsync(ct);

            foreach (var job in jobs)
            {
                var run = await processor.CreateRun(job, "Nightly");
                await processor.Execute(run, job);
            }
        }
    }
}
```

### 6. Docker Compose Scraper Cluster

```yaml
# docker-compose.scraper.yml
services:
  scraper:
    build:
      context: ./AIOWebScraper/ScraperWorker
    command: ["--dedicated-mode"]
    deploy:
      replicas: 5
    expose:
      - "7126"

  scraper-lb:
    image: nginx:alpine
    ports:
      - "7126:7126"
    volumes:
      - ./nginx.conf:/etc/nginx/nginx.conf:ro
    depends_on:
      - scraper
```

```nginx
# nginx.conf
upstream scrapers {
    server scraper:7126;
}
server {
    listen 7126;
    location / {
        proxy_pass http://scrapers;
        proxy_read_timeout 120s;
    }
}
```

Scale: `docker compose up --scale scraper=10`

ETL config: `Scraper:BaseUrl=http://localhost:7126` (same as today, zero change).

## Files to Delete

```
# Queue/callback infrastructure
AIOMarketMaker.Etl/Triggers/ScrapeJobQueueTrigger.cs
AIOMarketMaker.Etl/Triggers/CompletionCheckTrigger.cs
AIOMarketMaker.Etl/Endpoints/ProcessListingEndpoint.cs
AIOMarketMaker.Etl/Services/ListingProcessorService.cs
AIOMarketMaker.Etl/Services/ScrapeRunCounterService.cs

# Tests for removed components
AIOMarketMaker.Tests/Unit/Triggers/ScrapeJobQueueTrigger_UnitTests.cs
AIOMarketMaker.Tests/Unit/Triggers/CompletionCheckTrigger_UnitTests.cs
AIOMarketMaker.Tests/Unit/Triggers/ScrapeTrigger_SequentialTests.cs
AIOMarketMaker.Tests/Unit/Endpoints/ProcessListingEndpoint_UnitTests.cs
AIOMarketMaker.Tests/Unit/Services/ScrapeRunCounterService_UnitTests.cs
AIOMarketMaker.Tests/Unit/Services/ListingProcessorService_UnitTests.cs

# Worker callback in AIOWebScraper
AIOWebScraper/ScraperWorker/Services/HttpProcessingCallback.cs
```

## Files to Rewrite

```
# New ASP.NET host (replaces Azure Functions Program.cs)
AIOMarketMaker.Etl/Program.cs

# Simplified processor (inline description fetching)
AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs

# Simplified trigger (fire-and-forget, no queue writes)
AIOMarketMaker.Etl/Triggers/ScrapeTrigger.cs → AIOMarketMaker.Etl/Endpoints/ScrapeEndpoints.cs

# Simplified run service (no queue, no IsRunComplete)
AIOMarketMaker.Etl/Services/ScrapeRunService.cs
```

## Files Unchanged

```
# All parsers
AIOMarketMaker.Core/Parsers/EbaySearchParser.cs
AIOMarketMaker.Core/Parsers/EbayListingParser.cs

# Scraper client (GetPageHtml stays, remove EnqueueScrapeWork)
AIOMarketMaker.Core/Services/WebscraperClient.cs

# Indexing
AIOMarketMaker.Etl/Services/ListingIndexingService.cs

# All parser tests, contract tests, E2E tests
# Classification logic within ScrapeJobProcessor
```

## Database Migration

```sql
-- 034_drop_scrape_run_listings.sql
IF EXISTS (SELECT * FROM sys.tables WHERE name = 'ScrapeRunListings')
    DROP TABLE ScrapeRunListings;
```

## Performance Budget (10-min Azure timeout no longer applies, but for reference)

| Phase | Duration | Notes |
|-------|----------|-------|
| Search sold (10 pages) | ~45s | Sequential, full eBay pages via Playwright |
| Search active (10 pages) | ~45s | Sequential, full eBay pages via Playwright |
| Classify | <1s | DB query + in-memory logic |
| Update from summary | <5s | Batch DB writes |
| Fetch 900 descriptions | ~3 min | 15 concurrent x ~3s each via scraper cluster |
| Parse + save + index | Overlapped | Inline with fetch |
| **Total** | **~5 min** | Well within any reasonable limit |

## Testing Strategy

### Unit Tests (rewrite)

- `ScrapeJobProcessor_UnitTests.cs` - Mock `GetPageHtml`, verify:
  - Descriptions fetched for all `ToScrape` listings
  - Failed descriptions marked as "missing", run still completes
  - Progress updated during processing
  - Run marked as Completed when loop finishes
  - Run marked as Failed on unrecoverable error
- `ScrapeEndpoints_UnitTests.cs` - Verify 202 response, run creation
- `NightlyScrapeService_UnitTests.cs` - Verify scheduling logic

### Integration Tests

- `InlinePipeline_IntegrationTests.cs` - Full flow with mock scraper:
  - Start scrape → verify descriptions fetched → verify listings saved → verify run completed
  - Test with some descriptions failing → verify partial success

### E2E Tests (keep existing)

- `ScrapePipeline_E2ETests.cs` - Already tests search + parse with mock eBay server
- `EbayContract_E2ETests.cs` - Already tests against real eBay HTML

## Behavioral Parity Checklist

### Behaviors Preserved

- [x] Multi-page search (sold + active, continues until no results)
- [x] Sold listing search before active search
- [x] Terminal status filtering (Sold/Ended/OutOfStock skip re-scrape)
- [x] Active listing re-scrape for price/status updates (via UpdateListingsFromSummary)
- [x] Status history records on status changes
- [x] Duplicate detection with HashSet in paginated search
- [x] Pinecone indexing on description processing
- [x] Description parsing with AngleSharp
- [x] Progress visible in history API during processing
- [x] Manual and nightly trigger support
- [x] Multiple jobs processed sequentially
- [x] Failed descriptions don't fail the run

### Behaviors Intentionally Changed

| Old Behavior | New Behavior | Rationale |
|---|---|---|
| Per-listing queue messages | Inline HTTP calls | Eliminates silent failures |
| Docker queue workers | Scraper cluster (dedicated mode) | Same Playwright, simpler orchestration |
| Blob storage for description HTML | In-memory parse | No intermediate storage needed |
| ScrapeRunListings tracking | Direct counter on ScrapeRun | Less state, fewer bugs |
| Azure Functions hosting | ASP.NET API | No timeout limits, simpler DI |
| Completion via counter threshold | Completion via loop ending | Deterministic, no race conditions |
| Two Azure Functions projects (7071 + 7072) | Single ASP.NET API | One process, one port |
