# Pipeline Components Reference

All reusable components ("lego blocks") in the AIOMarketMaker + AIOWebScraper scraping pipeline.

## Architecture Diagram

```
 STAGE 1: Job Initiation

 SimplifiedScrapeTrigger (Timer: 0 0 2 * * * / HTTP: POST /scrape)
 -> Finds ScrapeJobs due for scraping
 -> Enqueues job message to "scrape-jobs" queue

                           | Azure Queue: "scrape-jobs"
                           v

 STAGE 2: Job Processing (Port 7072)

 ScrapeJobQueueTrigger (QueueTrigger)
 -> Fetches search pages via WebscraperClient (-> port 7126)
 -> Parses listing IDs from search HTML
 -> Filters terminal status listings (ListingsFilteredPreQueue)
 -> [!] FAILURE POINT #1: SendMessageAsync in loop, no transaction
 -> Enqueues individual listing messages to "scrape-work" queue

                           | Azure Queue: "scrape-work"
                           v

 STAGE 3: HTML Fetching (No port - BackgroundService)

 SimpleQueueWorker (runs in Docker container)
 -> Dequeues from "scrape-work"
 -> Fetches listing HTML via Playwright (stealth browser)
 -> Uploads HTML to Azure Blob Storage
 -> [!] FAILURE POINT #2: Callback errors swallowed (LogWarning)
 -> [!] FAILURE POINT #3: Message deleted regardless of callback
 -> Calls HttpProcessingCallback -> POST to port 7072

                           | HTTP POST /api/process-listing
                           v

 STAGE 4: ETL Processing (Port 7072)

 ProcessListingEndpoint (HttpTrigger: POST /api/process-listing)
 -> Downloads HTML from Blob Storage
 -> Parses with AngleSharp + EbayListingParser
 -> Upserts Listing in SQL database
 -> Creates ListingStatusHistory records
 -> Atomic counter increment (ListingsProcessed++)
 -> Immediate completion detection (last listing check)


 FALLBACK: Completion Safety Net

 CompletionCheckTrigger (Timer: every 5 min)
 -> Catches stuck runs (crashed workers, missed messages)
 -> Marks Completed when Processed >= (Total - FilteredPreQueue)


 EXTERNAL: HTML Fetching Service (Port 7126)

 AIOWebScraper (ScraperWorker in dedicated mode)
 -> Playwright browser automation with 15+ stealth evasions
 -> SOCKS5/HTTP proxy support
 -> Called by both Stage 2 (search pages) and Stage 3 (listings)
```

## HTML Fetching (AIOWebScraper)

| Block | What It Does | Key Method |
|-------|-------------|------------|
| **PlaywrightExtraFetcher** | Browser automation with stealth, proxy, CAPTCHA solving | `GetStringAsync(url)` -> HTML string |
| **HttpFetcherService** | Lightweight HTTP-only fetcher with user-agent spoofing | `GetStringAsync(url)` -> HTML string |
| **FetcherService** | Strategy wrapper with throttling + circuit breaker | `FetchPage(url)` -> `FetchResult` |
| **DedicatedModeService** | Single-URL fetch with optional proxy selection | `ProcessUrlAsync(url)` -> HTML string |
| **RouteFilterService** | Blocks images/fonts/CSS in browser requests | `ShouldBlock(url, resourceType)` -> bool |
| **ProxyHelper** | Parses proxy strings, decides when to bypass | `ParseProxy(string)`, `ShouldUseProxy(url)` |
| **BrowserCrashDetector** | Detects browser crashes needing restart | `IsPageCrashError(msg)` -> bool |
| **CircuitBreaker** | Prevents cascading failures | `ExecuteAsync(operation)` |

### PlaywrightExtraFetcher

- **Implements:** `IBrowserFetcher`, `IAsyncDisposable`
- **File:** `ScraperWorker/Services/PlaywrightFetcherService.cs`
- **Dependencies:** `ILogger<PlaywrightExtraFetcher>`, `string? twoCaptchaApiKey`, `string? proxy`, `IRouteFilterService?`, `int contextPoolSize`
- **Methods:**
  - `Task<string> GetStringAsync(string url, CancellationToken token = default)`
  - `Task<string> GetStringAsync(string url, CancellationToken token, ProxyConfig? proxyConfig)`
  - `Task ResetContextPoolAsync()`

### HttpFetcherService

- **Implements:** `IHttpFetcher`
- **File:** `ScraperWorker/Services/HttpFetcherService.cs`
- **Dependencies:** `IHttpClientFactory`, `ILogger<HttpFetcherService>`, `string? twoCaptchaApiKey`, `string? proxy`
- **Methods:**
  - `Task<string> GetStringAsync(string url, CancellationToken token)`
  - `Task<string> GetStringAsync(string url, CancellationToken token, ProxyConfig? proxyConfig)`

### FetcherService

- **Implements:** `IFetcherService`, `IDisposable`
- **File:** `ScraperWorker/Services/FetcherService.cs`
- **Dependencies:** `IHttpFetcher`, `IBrowserFetcher`, `string? twoCaptchaApiKey`, `string? proxy`, `ILogger<FetcherService>?`
- **Methods:**
  - `Task<FetchResult> FetchPage(string url, CancellationToken token)`
  - `Task<FetchResult> FetchPage(string url, CancellationToken token, ProxyConfig? proxyConfig)`
  - `SystemHealth GetHealth()`
  - `Task ResetBrowserContextAsync()`
- **Result:** `FetchResult(string Id, string Url, string? Html, int StatusCode, bool IsSuccess, bool IsBotCheck, TimeSpan ResponseTime, string? ErrorMessage, DateTimeOffset Timestamp)`

### DedicatedModeService

- **Implements:** `IDedicatedModeService`
- **File:** `ScraperWorker/Services/DedicatedModeService.cs`
- **Dependencies:** `IFetcherService`, `ILogger<DedicatedModeService>`
- **Methods:**
  - `Task<string> ProcessUrlAsync(string url, IEnumerable<ProxyConfig>? proxies = null, bool freshContext = false)`

## Parsing (AIOMarketMaker.Core)

| Block | What It Does | Key Method |
|-------|-------------|------------|
| **EbaySearchParser** | Extracts listing IDs/prices from search result pages | `ParseSearchResults(document)` -> `IEnumerable<EbayProductSummary>` |
| **EbayListingParser** | Extracts full listing details from item pages | `ParseProductListing(document, url)` -> `ExtractedEbayListing` |
| **EbayUrlBuilder** | Constructs eBay search/listing/description URLs | `BuildSearchUrl(...)`, `BuildListingUrl(id)` |
| **ListingStatusHelper** | Enforces status transitions (no Sold->Active) | `CanUpdateStatus(old, new)` -> bool |

### EbaySearchParser

- **Implements:** `ISearchParser`
- **File:** `AIOMarketMaker.Core/Parsers/EbaySearchParser.cs`
- **Dependencies:** None (stateless)
- **Methods:**
  - `IEnumerable<IEbayProductSummary> ParseSearchResults(IDocument document)`
  - `bool IsErrorPage(IDocument doc)`
  - `string? GetListingId(IElement li)`
  - `string ExtractTitle(IElement li)`
  - `static decimal ExtractPrice(IElement li)`
  - `static string ExtractCurrency(IElement li)`
  - `static decimal ExtractShippingCost(IElement li)`
  - `static Condition ExtractCondition(IElement listItemElement)`
  - `BuyingFormat ExtractBuyingFormat(IElement li)`
  - `DateTime? ExtractDate(IElement li)`

### EbayListingParser

- **Implements:** `IListingParser`
- **File:** `AIOMarketMaker.Core/Parsers/EbayListingParser.cs`
- **Dependencies:** None (stateless)
- **Methods:**
  - `ExtractedEbayListing ParseProductListing(IDocument document, string url)`
  - `string? ParseDescription(IDocument document)`
  - `bool IsProductCatalogPage(IDocument document)`

### EbayUrlBuilder

- **Implements:** `IEbayUrlBuilder`
- **File:** `AIOMarketMaker.Core/Services/EbayUrlBuilder.cs`
- **Dependencies:** None (stateless)
- **Methods:**
  - `string BuildSearchUrl(string query, bool sold, int page, Condition condition, BuyingFormat buyingFormat)`
  - `string BuildListingUrl(string itemId)`
  - `string BuildDescriptionUrl(string listingId)`

### ListingStatusHelper

- **Static class**
- **File:** `AIOMarketMaker.Core/Services/ListingStatusHelper.cs`
- **Methods:**
  - `static int GetStatusRank(string? status)`
  - `static bool CanUpdateStatus(string? existingStatus, string? newStatus)`

## Storage

| Block | What It Does | Key Method |
|-------|-------------|------------|
| **EtlDbContext** | SQL database (Listings, ScrapeRuns, ScrapeJobs, History) | EF Core DbSets |
| **AzureJobRepository** | Azure Table + Blob storage for scrape jobs/HTML | `SaveContentAsync(...)`, `GetFileContentsAsync(...)` |
| **AzureStorageQueueService** | Azure Queue enqueue/dequeue/dead-letter | `EnqueueAsync(msg)`, `DequeueAsync(timeout)`, `CompleteAsync(ctx)` |
| **BlobPathBuilder** | Builds blob paths like `{runId}/{groupId}/{fileKey}.html` | `Build(jobId, url, groupId, fileKey)` |
| **MigrationRunner** | Applies SQL migrations from embedded resources | `ApplyMigrations()` |

### EtlDbContext

- **Extends:** `DbContext`
- **File:** `AIOMarketMaker.Core/Data/EtlDbContext.cs`
- **Constructors:**
  - `EtlDbContext(string connectionString)`
  - `EtlDbContext(DbContextOptions<EtlDbContext> options)`
- **DbSets:**
  - `DbSet<ScrapeJob> ScrapeJobs`
  - `DbSet<Listing> Listings`
  - `DbSet<ListingStatusHistory> ListingStatusHistory`
  - `DbSet<ScrapeRun> ScrapeRuns`
  - `DbSet<ScrapeRunListing> ScrapeRunListings`
  - `DbSet<ScrapeRunIssue> ScrapeRunIssues`

### AzureJobRepository

- **Implements:** `IJobRepository`
- **File:** `AIOWebScraper.Storage.Azure/AzureJobRepository.cs`
- **Dependencies:** `TableServiceClient`, `BlobServiceClient`, `ILogger<AzureJobRepository>`
- **Methods:**
  - `Task SeedPendingAsync(IEnumerable<string> urls, string jobId, CancellationToken ct)`
  - `Task SaveContentAsync(string jobId, string url, string html, string? groupId, string? fileKey, int? scrapeRunId, CancellationToken ct)`
  - `Task LogProgressAsync(string jobId, JobEntity p, CancellationToken ct)`
  - `Task<JobEntity?> GetJobAsync(string jobId, CancellationToken ct)`
  - `Task<IEnumerable<JobItemEntity>> GetJobResults(string jobId, CancellationToken ct)`
  - `Task<string> GetFileContentsAsync(string jobId, string url, string? groupId, string? fileKey, CancellationToken ct)`
  - `Task CreateJobAsync(string jobId, int totalUrls, CancellationToken ct)`
  - `Task IncrementProgressAsync(string jobId, bool success, CancellationToken ct)`
  - `Task MarkJobCompletedAsync(string jobId, CancellationToken ct)`
  - `Task<bool> IsJobCompletedAsync(string jobId, CancellationToken ct)`
  - `Task SaveItemFailureAsync(string jobId, string url, string errorMessage, CancellationToken ct)`

### AzureStorageQueueService

- **Implements:** `IQueueService`
- **File:** `AIOWebScraper.Storage.Azure/AzureStorageQueueService.cs`
- **Dependencies:** `QueueServiceClient`, `ILogger<AzureStorageQueueService>`
- **Methods:**
  - `Task EnqueueAsync(ScrapeQueueMessage message, CancellationToken ct)`
  - `Task EnqueueBatchAsync(IEnumerable<ScrapeQueueMessage> messages, CancellationToken ct)`
  - `Task<QueueMessageContext<ScrapeQueueMessage>?> DequeueAsync(TimeSpan visibilityTimeout, CancellationToken ct)`
  - `Task CompleteAsync(QueueMessageContext<ScrapeQueueMessage> context, CancellationToken ct)`
  - `Task<int> GetApproximateMessageCountAsync(CancellationToken ct)`
  - `Task MoveToDeadLetterAsync(QueueMessageContext<ScrapeQueueMessage> context, string reason, CancellationToken ct)`

### Domain Models

- **File:** `AIOWebScraper.Storage.Azure/Domain.cs`
  - `JobEntity` - Azure Table entity for job progress (TotalItems, Processed, Success, Failure, ETA)
  - `JobItemEntity` - Azure Table entity for single URL result (status, blob URI, error)
  - `JobStatusType` enum: `Pending`, `Processing`, `Success`, `Failure`
- **File:** `AIOWebScraper.Storage.Azure/QueueMessage.cs`
  - `ScrapeQueueMessage` record: `JobId`, `Url`, `CorrelationId`, `EnqueuedAt`, `ProxyConfigJson`, `GroupId`, `FileKey`, `ScrapeRunId`, `ScrapeRunListingId`, `ScrapeJobId`
  - `QueueMessageContext<T>` - wraps message with `MessageId`, `PopReceipt`, `DequeueCount`

### BlobPathBuilder

- **Static class**
- **File:** `AIOWebScraper.Storage.Azure/BlobPathBuilder.cs`
- **Methods:**
  - `static string Build(string jobId, string url, string? groupId, string? fileKey, bool useSimplePath = false, int? scrapeRunId = null)`

## HTTP Clients

| Block | What It Does | Key Method |
|-------|-------------|------------|
| **WebscraperClient** | Calls AIOWebScraper API (fetch pages, run jobs) | `GetPageHtmlAsync(url)`, `RunJobAsync(urls)` |
| **HttpProcessingCallback** | POSTs to ProcessListingEndpoint after fetch | `NotifyListingProcessedAsync(...)` -> bool |

### WebscraperClient

- **Implements:** `IWebscraperClient`
- **File:** `AIOMarketMaker.Core/Services/WebscraperClient.cs`
- **Dependencies:** `HttpClient`, `ScraperApiConfig`, `IJobRepository`, `ILogger<WebscraperClient>`
- **Config:** `ScraperApiConfig(string BaseUrl, string ApiKey)`
- **Methods:**
  - `Task<StartResponse> NewJobAsync(IEnumerable<string> urls, ...)`
  - `Task<string> GetPageHtmlAsync(string url, ...)`
  - `Task<JobEntity?> GetStatusAsync(string jobId, CancellationToken ct)`
  - `Task<IReadOnlyList<JobItemEntity>> GetResultsAsync(string jobId, CancellationToken ct)`
  - `Task<IEnumerable<JobItemEntity>> RunJobAsync(IEnumerable<string> urls)`

### HttpProcessingCallback

- **Implements:** `IProcessingCallback`
- **File:** `ScraperWorker/Services/HttpProcessingCallback.cs`
- **Dependencies:** `HttpClient`, `ILogger<HttpProcessingCallback>`, `string baseUrl`
- **Methods:**
  - `Task<bool> NotifyListingProcessedAsync(int scrapeRunId, int scrapeRunListingId, string listingId, int scrapeJobId, string blobPath, CancellationToken ct)`

## Orchestration / High-Level

| Block | What It Does | Key Method |
|-------|-------------|------------|
| **EbayScraper** | Searches eBay, fetches listings, parses results | `SearchActiveListings(...)`, `GetItemsFromListings(ids)` |
| **JobRunner** | Full end-to-end scrape job (search->fetch->parse->save) | `RunJob(jobId)` -> `JobRunResult` |
| **StatusRefreshRunner** | Re-scrapes active listings for status changes | `RefreshActiveListingsAsync(jobId)` |
| **JobOrchestrator** | Parallel batch URL processing with retry | `RunAsync(urls, jobId)` |
| **SimpleQueueWorker** | Polls queue, fetches HTML, calls callback | `ExecuteAsync(ct)` |

### EbayScraper

- **Implements:** `IEbayScraper`
- **File:** `AIOMarketMaker.Core/Services/EbayScraper.cs`
- **Dependencies:** `IEbayUrlBuilder`, `IWebscraperClient`, `ISearchParser`, `IListingParser`, `IJobRepository`, `ILogger<EbayScraper>`
- **Methods:**
  - `Task<IEnumerable<EbayProductSummary>> SearchActiveListings(string query, BuyingFormat buyingFormat, Condition condition, int itemLimit = 500)`
  - `Task<IEnumerable<EbayProductSummary>> SearchSoldListings(string query, BuyingFormat buyingFormat, Condition condition, DateTime startDate, DateTime endDate)`
  - `Task<IEnumerable<EbayProduct>> GetItemsFromListings(string[] itemIds)`

### JobRunner

- **Implements:** `IJobRunner`
- **File:** `AIOMarketMaker.Core/Services/JobRunner.cs`
- **Dependencies:** `EtlDbContext`, `IEbayScraper`, `IConfiguration`, `ILogger<JobRunner>`
- **Methods:**
  - `Task<JobRunResult> RunJob(int jobId, CancellationToken ct = default)`
  - `Task<JobRunResult> RunJob(ScrapeJob job, CancellationToken ct = default)`
- **Result:** `JobRunResult(int JobId, bool Success, int ListingsFound, int NewListingsFetched, int StatusUpdates, string? Error)`

### SimpleQueueWorker

- **Extends:** `BackgroundService`
- **File:** `ScraperWorker/Services/SimpleQueueWorker.cs`
- **Dependencies:** `IQueueService`, `IJobRepository`, `ILogger<SimpleQueueWorker>`, `IRouteFilterService`, `string? proxy`, `IProcessingCallback?`
- **Methods:**
  - `protected override Task ExecuteAsync(CancellationToken stoppingToken)`

### JobOrchestrator

- **Implements:** `IJobOrchestrator`, `IDisposable`
- **File:** `ScraperWorker/Services/JobOrchestrator.cs`
- **Dependencies:** `IJobInitializer`, `IJobItemProcessor`, `IJobRepository`, `ILogger<JobOrchestrator>`
- **Methods:**
  - `Task RunAsync(IEnumerable<string> urls, string jobId, CancellationToken ct)`

## AI / Analytics

| Block | What It Does | Key Method |
|-------|-------------|------------|
| **EmbeddingService** | OpenAI text embeddings | `GetEmbeddingAsync(text)` -> `float[]` |
| **SemanticSearchService** | Pinecone vector index + search | `IndexListingsAsync(...)`, `SearchAsync(query)` |
| **ClusteringService** | HDBSCAN density clustering on embeddings | `Cluster(items)` -> `ClusteringResult` |
| **PricingAnalysisService** | Statistical pricing (median, IQR, confidence) | `Analyze(listings, hits)` -> `PricingAnalysisResult` |

### EmbeddingService

- **Implements:** `IEmbeddingService`
- **File:** `AIOMarketMaker.Core/Services/EmbeddingService.cs`
- **Dependencies:** `EmbeddingConfig`, `ILogger<EmbeddingService>`
- **Config:** `EmbeddingConfig(string ApiKey, string Model = "text-embedding-3-small", int Dimensions = 1536)`
- **Methods:**
  - `Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)`
  - `Task<float[][]> GetEmbeddingsAsync(IEnumerable<string> texts, CancellationToken ct = default)`

### SemanticSearchService

- **Implements:** `ISemanticSearchService`
- **File:** `AIOMarketMaker.Core/Services/SemanticSearchService.cs`
- **Dependencies:** `PineconeConfig`, `IPineconeIndexClient`, `IEmbeddingService`, `ILogger<SemanticSearchService>`
- **Methods:**
  - `Task<IndexResult> IndexListingsAsync(IEnumerable<Listing> listings, CancellationToken ct = default)`
  - `Task<SemanticSearchResult> SearchAsync(string queryText, IEnumerable<string>? filterToListingIds, int? topK, CancellationToken ct)`
  - `Task<SemanticSearchResult> FindSimilarAsync(string listingId, IEnumerable<string>? filterToListingIds, int? topK, CancellationToken ct)`
  - `Task DeleteAsync(IEnumerable<string> listingIds, CancellationToken ct)`

### ClusteringService

- **Implements:** `IClusteringService`
- **File:** `AIOMarketMaker.Core/Services/ClusteringService.cs`
- **Dependencies:** `ClusteringConfig`, `ILogger<ClusteringService>`
- **Config:** `ClusteringConfig(int MinClusterSize = 5, int MinPoints = 3)`
- **Methods:**
  - `ClusteringResult Cluster(IReadOnlyList<EmbeddingWithId> items)`

### PricingAnalysisService

- **Implements:** `IPricingAnalysisService`
- **File:** `AIOMarketMaker.Core/Services/PricingAnalysisService.cs`
- **Dependencies:** None (stateless)
- **Methods:**
  - `PricingAnalysisResult Analyze(IEnumerable<Listing> listings, IEnumerable<SemanticSearchHit> hits, PricingAnalysisOptions? options = null)`

## DI Registration

### AIOMarketMaker.Etl Program.cs (Azure Functions host)

| Interface | Implementation | Lifetime |
|-----------|---------------|----------|
| `IEbayUrlBuilder` | `EbayUrlBuilder` | Singleton |
| `ISearchParser` | `EbaySearchParser` | Singleton |
| `IListingParser` | `EbayListingParser` | Singleton |
| `IWebscraperClient` | `WebscraperClient` | Singleton (via `AddHttpClient`) |
| `IJobRepository` | `AzureJobRepository` | Singleton |
| `IQueueService` | `AzureStorageQueueService` | Singleton |
| `IEmbeddingService` | `EmbeddingService` | Singleton (optional) |
| `IClusteringService` | `ClusteringService` | Singleton |
| `IPineconeIndexClient` | `PineconeIndexClientWrapper` | Singleton (optional) |
| `ISemanticSearchService` | `SemanticSearchService` | Singleton (optional) |
| `IPricingAnalysisService` | `PricingAnalysisService` | Singleton |

## Key Design Insight

The atomic unit should be **fetch + parse + save in a single transaction**. Currently fetch (SimpleQueueWorker in AIOWebScraper) and parse (ProcessListingEndpoint in AIOMarketMaker.Etl) are split across services with a lossy HTTP callback in between. Combining those blocks into one step eliminates all three failure points in the pipeline.
