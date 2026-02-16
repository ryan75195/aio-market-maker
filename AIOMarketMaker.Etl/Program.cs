using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Migrations;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Etl.Services;
using ScraperWorker.Services;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using System.Text.Json;
using Serilog;
using Serilog.Formatting.Compact;

// Configure Serilog with optional file sink
var logSessionPath = Environment.GetEnvironmentVariable("LOG_SESSION_PATH");

var loggerConfig = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Component", "AIOMarketMaker.Etl")
    .WriteTo.Console();

if (!string.IsNullOrEmpty(logSessionPath))
{
    Directory.CreateDirectory(logSessionPath);
    var logFile = Path.Combine(logSessionPath, "etl.json");
    loggerConfig.WriteTo.File(
        new CompactJsonFormatter(),
        logFile,
        rollingInterval: RollingInterval.Hour,
        retainedFileCountLimit: null);
}

Log.Logger = loggerConfig.CreateLogger();

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((context, config) =>
    {
        // Try multiple locations for local.settings.json (after ConfigureFunctionsWorkerDefaults)
        var currentDir = Directory.GetCurrentDirectory();
        var baseDir = AppContext.BaseDirectory;

        config.AddJsonFile(Path.Combine(currentDir, "local.settings.json"), optional: true, reloadOnChange: false)
              .AddJsonFile(Path.Combine(baseDir, "local.settings.json"), optional: true, reloadOnChange: false)
              .AddEnvironmentVariables();

        // Azure Functions stores values under "Values" section - flatten them to root
        // Use AsEnumerable to recursively flatten nested keys (e.g. VariantClassifier:ModelPath)
        var tempConfig = config.Build();
        var valuesSection = tempConfig.GetSection("Values");
        if (valuesSection.Exists())
        {
            var values = new Dictionary<string, string?>();
            foreach (var kvp in valuesSection.AsEnumerable(makePathsRelative: true))
            {
                if (kvp.Value != null)
                {
                    values[kvp.Key] = kvp.Value;
                }
            }
            config.AddInMemoryCollection(values);
        }
    })
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;

        // Application Insights
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Configuration - use Azure Functions style connection strings
        var blobConnectionString = configuration.GetValue<string>("blobStorageConnectionString")
            ?? configuration.GetValue<string>("AzureWebJobsStorage")
            ?? "UseDevelopmentStorage=true";
        var tableConnectionString = configuration.GetValue<string>("tableStorageConnectionString")
            ?? configuration.GetValue<string>("AzureWebJobsStorage")
            ?? "UseDevelopmentStorage=true";

        // Azure Storage clients
        services.AddSingleton(_ => new BlobServiceClient(blobConnectionString));
        services.AddSingleton(_ => new TableServiceClient(tableConnectionString));

        services.AddScoped<IScrapeJobProcessor, ScrapeJobProcessor>();

        services.AddSingleton<IJobRepository>(sp =>
            new AzureJobRepository(
                sp.GetRequiredService<TableServiceClient>(),
                sp.GetRequiredService<BlobServiceClient>(),
                sp.GetRequiredService<ILogger<AzureJobRepository>>()));

        // SQLite database (for local development)
        var sqliteConnectionString = configuration.GetValue<string>("SqliteConnectionString");
        if (!string.IsNullOrEmpty(sqliteConnectionString))
        {
            // Run migrations on startup
            var migrationRunner = new MigrationRunner(sqliteConnectionString, null);
            migrationRunner.ApplyMigrations();

            services.AddDbContext<EtlDbContext>(options =>
                options.UseSqlite(sqliteConnectionString));
        }

        // SQL Server database (for Azure deployment)
        var sqlConnectionString = configuration.GetValue<string>("SqlConnectionString")
            ?? configuration.GetValue<string>("Values:SqlConnectionString");
        if (!string.IsNullOrEmpty(sqlConnectionString))
        {
            // Run migrations on startup
            var migrationRunner = new MigrationRunner(sqlConnectionString, null, useSqlServer: true);
            migrationRunner.ApplyMigrations();

            services.AddDbContext<EtlDbContext>(options =>
                options.UseSqlServer(sqlConnectionString));
        }

        // Core parsing services
        services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
        services.AddSingleton<ISearchParser, EbaySearchParser>();
        services.AddSingleton<IListingParser, EbayListingParser>();

        // Web scraper client (required for orchestrators)
        var scraperBaseUrl = configuration.GetValue<string>("Scraper:BaseUrl") ?? "http://localhost:7126";
        var scraperApiKey = configuration.GetValue<string>("Scraper:ApiKey") ?? "";
        services.AddSingleton(new ScraperApiConfig(scraperBaseUrl, scraperApiKey));
        services.AddHttpClient<IWebscraperClient, WebscraperClient>(client =>
        {
            client.BaseAddress = new Uri(scraperBaseUrl);
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        // Embedding service (required)
        var openAiKey = configuration.GetValue<string>("OpenAi:ApiKey")
            ?? configuration.GetValue<string>("Values:OpenAi:ApiKey")
            ?? throw new InvalidOperationException("OpenAi:ApiKey is required. Add it to local.settings.json.");
        var embeddingModel = configuration.GetValue<string>("Embedding:Model") ?? "text-embedding-3-large";
        var embeddingDimensions = configuration.GetValue<int>("Embedding:Dimensions", 3072);
        services.AddSingleton(new EmbeddingConfig(openAiKey, embeddingModel, embeddingDimensions));
        services.AddSingleton<IEmbeddingService, EmbeddingService>();

        // Clustering service
        var clusteringConfig = new ClusteringConfig(
            configuration.GetValue<int>("Clustering:MinClusterSize", 8),
            configuration.GetValue<int>("Clustering:MinPoints", 4));
        services.AddSingleton(clusteringConfig);
        services.AddSingleton<IClusteringService, ClusteringService>();

        // Vector index (local USearch)
        var vectorIndexConfig = new VectorIndexConfig(
            IndexPath: configuration.GetValue<string>("VectorIndex:IndexPath")
                ?? configuration.GetValue<string>("Values:VectorIndex:IndexPath")
                ?? "./data/vectors.usearch",
            IdMapPath: configuration.GetValue<string>("VectorIndex:IdMapPath")
                ?? configuration.GetValue<string>("Values:VectorIndex:IdMapPath")
                ?? "./data/vectors-idmap.json",
            TopK: configuration.GetValue<int?>("VectorIndex:TopK")
                ?? configuration.GetValue<int?>("Values:VectorIndex:TopK")
                ?? 30,
            SimilarityThreshold: configuration.GetValue<float?>("VectorIndex:SimilarityThreshold")
                ?? configuration.GetValue<float?>("Values:VectorIndex:SimilarityThreshold")
                ?? 0.80f,
            Dimensions: configuration.GetValue<int?>("VectorIndex:Dimensions")
                ?? configuration.GetValue<int?>("Values:VectorIndex:Dimensions")
                ?? 3072,
            Connectivity: configuration.GetValue<int?>("VectorIndex:Connectivity")
                ?? configuration.GetValue<int?>("Values:VectorIndex:Connectivity")
                ?? 16,
            ExpansionAdd: configuration.GetValue<int?>("VectorIndex:ExpansionAdd")
                ?? configuration.GetValue<int?>("Values:VectorIndex:ExpansionAdd")
                ?? 128,
            ExpansionSearch: configuration.GetValue<int?>("VectorIndex:ExpansionSearch")
                ?? configuration.GetValue<int?>("Values:VectorIndex:ExpansionSearch")
                ?? 64);
        services.AddSingleton(vectorIndexConfig);
        services.AddSingleton<IVectorIndex>(sp =>
        {
            var config = sp.GetRequiredService<VectorIndexConfig>();
            var index = new USearchVectorIndex(config);
            if (File.Exists(config.IndexPath) && File.Exists(config.IdMapPath))
            {
                index.Load();
                sp.GetRequiredService<ILogger<USearchVectorIndex>>()
                    .LogInformation("Loaded vector index with {Count} vectors from {Path}",
                        index.Count, config.IndexPath);
            }
            return index;
        });
        services.AddSingleton<ISemanticSearchService, SemanticSearchService>();

        // Listing indexing service
        services.AddSingleton<IListingIndexingService, ListingIndexingService>();

        // Pricing analysis service
        services.AddSingleton<IPricingAnalysisService, PricingAnalysisService>();

        // Variant classifier (local ONNX model)
        var classifierConfig = new OnnxClassifierConfig(
            ModelPath: configuration.GetValue<string>("VariantClassifier:ModelPath") ?? "models/variant-classifier/model.onnx",
            VocabPath: configuration.GetValue<string>("VariantClassifier:VocabPath") ?? "models/variant-classifier/vocab.json",
            MergesPath: configuration.GetValue<string>("VariantClassifier:MergesPath") ?? "models/variant-classifier/merges.txt");
        services.AddSingleton(classifierConfig);
        services.AddSingleton<IVariantClassifierClient, OnnxVariantClassifier>();

        // GPT comparison (fallback for low-confidence pairs)
        var chatModel = configuration.GetValue<string>("OpenAi:ChatModel") ?? "gpt-5-nano";
        var comparisonConfig = new ListingComparisonConfig(openAiKey, chatModel);
        services.AddSingleton(comparisonConfig);
        services.AddSingleton<ListingComparisonService>();

        // Model-first with GPT fallback
        var modelFirstConfig = new ModelFirstComparisonConfig(
            ConfidenceThreshold: configuration.GetValue<float>("VariantClassifier:ConfidenceThreshold", 0.80f),
            EnableGptFallback: configuration.GetValue<bool>("VariantClassifier:EnableGptFallback", false));
        services.AddSingleton<IListingComparisonService>(sp =>
            new ModelFirstComparisonService(
                sp.GetRequiredService<IVariantClassifierClient>(),
                sp.GetRequiredService<ListingComparisonService>(),
                modelFirstConfig,
                sp.GetRequiredService<ILogger<ModelFirstComparisonService>>()));

        // ComparablesEtlService
        services.AddScoped<IComparablesEtlService, ComparablesEtlService>();
    })
    .ConfigureLogging(logging =>
    {
        logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
    })
    .UseSerilog()
    .Build();

if (args.Contains("--export-vectors"))
{
    await ExportVectorsFromPinecone(host, args);
    return;
}

if (args.Contains("--comparables"))
{
    using var scope = host.Services.CreateScope();
    var etl = scope.ServiceProvider.GetRequiredService<IComparablesEtlService>();
    var dryRun = args.Contains("--dry-run");
    var result = await etl.Run(dryRun);

    Console.WriteLine();
    Console.WriteLine(dryRun ? "Dry Run Summary" : "Run Summary");
    Console.WriteLine("===============");
    Console.WriteLine($"Listings processed:     {result.ListingsProcessed}");
    Console.WriteLine($"Vector queries:         {result.VectorQueries}");
    Console.WriteLine($"Candidate pairs found:  {result.CandidatePairsFound}");
    Console.WriteLine($"Cache hits:             {result.CacheHits}");
    Console.WriteLine($"ONNX pairs classified:  {result.LlmCallsMade}");
    Console.WriteLine($"Comparables found:      {result.ComparablesFound}");
    Console.WriteLine();
    Console.WriteLine("Predictions are computed live via vw_ListingPredictions view.");
    return;
}

if (args.Contains("--k-analysis"))
{
    await RunKAnalysis(host, args);
    return;
}

if (args.Contains("--validate"))
{
    await RunValidation(host, args);
    return;
}

static async Task RunValidation(IHost host, string[] args)
{
    const int neighborsPerListing = 20;

    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
    var vectorIndex = scope.ServiceProvider.GetRequiredService<IVectorIndex>();
    var classifier = scope.ServiceProvider.GetRequiredService<IVariantClassifierClient>();

    // Load all listings
    Console.Write("Loading listings...");
    var allListings = await db.Listings
        .AsNoTracking()
        .ToDictionaryAsync(l => l.ListingId);
    var listingsById = allListings.Values.ToDictionary(l => l.Id);
    Console.WriteLine($" {allListings.Count:N0} loaded.");

    // Load scrape jobs for category names
    var jobs = await db.ScrapeJobs.AsNoTracking().ToDictionaryAsync(j => j.Id);

    // Pick interesting categories to validate - mix of easy and hard
    var targetJobTerms = new[] {
        "PlayStation 5 Console", "RTX 4090 Graphics Card", "Dyson Airwrap",
        "Nike Air Jordan 1", "Cartier Love Bracelet", "Rolex Submariner",
        "LEGO Star Wars Set", "Moissanite Engagement Ring", "Canada Goose",
        "Mac Mini", "Sonos One Speaker", "TaylorMade Stealth 2 Driver"
    };

    var targetJobIds = jobs.Values
        .Where(j => targetJobTerms.Any(t => j.SearchTerm.Contains(t, StringComparison.OrdinalIgnoreCase)))
        .Select(j => j.Id)
        .ToHashSet();

    // Sample 2 active listings per category
    var activeListings = allListings.Values
        .Where(l => l.ListingStatus == "Active" && targetJobIds.Contains(l.ScrapeJobId))
        .GroupBy(l => l.ScrapeJobId)
        .SelectMany(g => g.OrderByDescending(l => l.Id).Take(2))
        .ToList();

    Console.WriteLine($"Validating {activeListings.Count} listings across {activeListings.Select(l => l.ScrapeJobId).Distinct().Count()} categories");
    Console.WriteLine(new string('=', 120));

    foreach (var listing in activeListings)
    {
        var jobName = jobs.TryGetValue(listing.ScrapeJobId, out var job) ? job.SearchTerm : "Unknown";

        Console.WriteLine();
        Console.WriteLine($"[{jobName}] ACTIVE: {listing.Title}");
        Console.WriteLine($"  Price: {listing.Price:C} | Condition: {listing.Condition} | Id: {listing.Id}");
        Console.WriteLine($"  Desc: {(listing.Description ?? "")[..Math.Min((listing.Description ?? "").Length, 150)]}");
        Console.WriteLine();

        // Query vector index
        var neighbors = vectorIndex.SearchById(listing.ListingId, neighborsPerListing + 1)
            .Where(h => h.Id != listing.ListingId)
            .Take(neighborsPerListing)
            .ToList();

        if (neighbors.Count == 0)
        {
            Console.WriteLine("  (no vector neighbors found)");
            continue;
        }

        // Build classify requests
        var pairsToClassify = new List<(float Score, AIOMarketMaker.Core.Data.Models.Listing Neighbor, ClassifyPairRequest Request)>();
        foreach (var neighbor in neighbors)
        {
            if (!allListings.TryGetValue(neighbor.Id, out var neighborListing))
            {
                continue;
            }
            pairsToClassify.Add((
                Score: neighbor.Score,
                Neighbor: neighborListing,
                Request: new ClassifyPairRequest(
                    listing.Title ?? "", listing.Description ?? "",
                    neighborListing.Title ?? "", neighborListing.Description ?? "")));
        }

        // Classify batch
        var requests = pairsToClassify.Select(p => p.Request).ToList();
        var results = await classifier.Classify(requests, CancellationToken.None);

        // Print results
        for (var i = 0; i < pairsToClassify.Count; i++)
        {
            var pair = pairsToClassify[i];
            var result = results[i];
            var verdict = result.IsComparable ? "MATCH" : "REJECT";
            var marker = result.IsComparable ? "+" : "-";
            var status = pair.Neighbor.ListingStatus ?? "?";
            var price = pair.Neighbor.Price;

            Console.WriteLine($"  {marker} [{verdict}] conf={result.Confidence:F3} sim={pair.Score:F3} | {status} {price:C} | {pair.Neighbor.Title?[..Math.Min(pair.Neighbor.Title?.Length ?? 0, 80)]}");
        }

        Console.WriteLine($"  --- {results.Count(r => r.IsComparable)}/{pairsToClassify.Count} accepted ---");
    }
}

static async Task ExportVectorsFromPinecone(IHost host, string[] args)
{
    using var scope = host.Services.CreateScope();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
    var vectorIndexConfig = scope.ServiceProvider.GetRequiredService<VectorIndexConfig>();

    // Pinecone credentials (required for export)
    var pineconeApiKey = configuration.GetValue<string>("Pinecone:ApiKey")
        ?? configuration.GetValue<string>("Values:Pinecone:ApiKey")
        ?? throw new InvalidOperationException("Pinecone:ApiKey is required for export. Add it to local.settings.json.");
    var pineconeHost = configuration.GetValue<string>("Pinecone:Host")
        ?? configuration.GetValue<string>("Values:Pinecone:Host")
        ?? "arbitrage-d207f30.svc.aped-4627-b74a.pinecone.io";

    Console.WriteLine($"Export vectors from Pinecone host '{pineconeHost}' to local USearch index");
    Console.WriteLine($"  Index path: {vectorIndexConfig.IndexPath}");
    Console.WriteLine($"  ID map path: {vectorIndexConfig.IdMapPath}");
    Console.WriteLine();

    // Create HttpClient for Pinecone REST API (no SDK needed)
    using var httpClient = new HttpClient();
    httpClient.DefaultRequestHeaders.Add("Api-Key", pineconeApiKey);
    httpClient.BaseAddress = new Uri($"https://{pineconeHost}");
    var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    // Load all listing IDs from database
    Console.Write("Loading listing IDs from database...");
    var listingIds = await db.Listings
        .AsNoTracking()
        .Select(l => l.ListingId)
        .ToListAsync();
    Console.WriteLine($" {listingIds.Count:N0} listings.");

    // Create the local USearch index
    using var localIndex = new USearchVectorIndex(vectorIndexConfig);

    var batchSize = 1000; // Pinecone Fetch API limit
    var exported = 0;
    var missing = 0;
    var batches = listingIds.Chunk(batchSize);
    var batchCount = (int)Math.Ceiling(listingIds.Count / (double)batchSize);
    var batchNum = 0;

    Console.WriteLine($"Fetching {listingIds.Count:N0} vectors in {batchCount} batches of {batchSize}...");
    Console.WriteLine();

    foreach (var batch in batches)
    {
        batchNum++;
        var queryString = string.Join("&", batch.Select(id => $"ids={Uri.EscapeDataString(id)}"));
        var httpResponse = await httpClient.GetAsync($"/vectors/fetch?{queryString}");
        httpResponse.EnsureSuccessStatusCode();
        var json = await httpResponse.Content.ReadAsStringAsync();
        var response = JsonSerializer.Deserialize<PineconeFetchResponse>(json, jsonOptions);

        if (response?.Vectors != null)
        {
            foreach (var (id, vector) in response.Vectors)
            {
                if (vector.Values != null)
                {
                    localIndex.Upsert(id, vector.Values);
                    exported++;
                }
                else
                {
                    missing++;
                }
            }
        }

        var batchMissing = batch.Length - (response?.Vectors?.Count ?? 0);
        missing += batchMissing;

        if (batchNum % 10 == 0 || batchNum == batchCount)
        {
            Console.WriteLine($"  Batch {batchNum}/{batchCount}: {exported:N0} exported, {missing:N0} missing");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Export complete: {exported:N0} vectors exported, {missing:N0} not found in Pinecone");

    // Save to disk
    Console.Write($"Saving index to {vectorIndexConfig.IndexPath}...");
    localIndex.Save();
    Console.WriteLine(" done.");

    // Report file sizes
    var indexSize = new FileInfo(vectorIndexConfig.IndexPath).Length;
    var idMapSize = new FileInfo(vectorIndexConfig.IdMapPath).Length;
    Console.WriteLine($"  Index file: {indexSize / 1024.0 / 1024.0:F1} MB");
    Console.WriteLine($"  ID map file: {idMapSize / 1024.0 / 1024.0:F1} MB");
    Console.WriteLine($"  Total vectors in index: {localIndex.Count:N0}");
}

static async Task RunKAnalysis(IHost host, string[] args)
{
    const int maxK = 500;
    const int sampleSize = 20;

    // Parse optional --sample N
    var sampleArg = Array.IndexOf(args, "--sample");
    var actualSample = sampleArg >= 0 && sampleArg + 1 < args.Length && int.TryParse(args[sampleArg + 1], out var s) ? s : sampleSize;

    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
    var vectorIndex = scope.ServiceProvider.GetRequiredService<IVectorIndex>();
    var classifier = scope.ServiceProvider.GetRequiredService<IVariantClassifierClient>();

    Console.WriteLine($"K-Analysis: sampling {actualSample} active listings, querying vector index with K={maxK}");
    Console.WriteLine();

    // Load all listings into lookup dictionary
    Console.Write("Loading listings...");
    var allListings = await db.Listings
        .AsNoTracking()
        .ToDictionaryAsync(l => l.ListingId);
    var listingsById = allListings.Values.ToDictionary(l => l.Id);
    Console.WriteLine($" {allListings.Count:N0} listings loaded.");

    // Sample active listings spread across different scrape jobs (from in-memory data)
    var activeListings = allListings.Values
        .Where(l => l.ListingStatus == "Active")
        .GroupBy(l => l.ScrapeJobId)
        .SelectMany(g => g.OrderBy(l => l.Id).Take(3))
        .Take(actualSample)
        .ToList();

    Console.WriteLine($"Sampled {activeListings.Count} active listings from {activeListings.Select(l => l.ScrapeJobId).Distinct().Count()} jobs:");
    foreach (var l in activeListings)
    {
        Console.WriteLine($"  [{l.Id}] {l.Title?[..Math.Min(l.Title.Length, 70)]}");
    }
    Console.WriteLine();

    // Track results: rank -> (accepted, rejected, similarities)
    var rankResults = new Dictionary<int, (int Accepted, int Rejected, List<float> Scores, List<float> Confidences)>();
    for (var r = 1; r <= maxK; r++)
    {
        rankResults[r] = (0, 0, new List<float>(), new List<float>());
    }

    var totalQueries = 0;
    var totalPairs = 0;

    foreach (var listing in activeListings)
    {
        totalQueries++;
        Console.Write($"[{totalQueries}/{activeListings.Count}] Querying {listing.ListingId}...");

        // Query vector index directly — bypass SemanticSearchService threshold filter
        var neighbors = vectorIndex.SearchById(listing.ListingId, maxK + 1)
            .Where(h => h.Id != listing.ListingId)
            .Take(maxK)
            .ToList();

        Console.Write($" {neighbors.Count} neighbors.");

        if (neighbors.Count == 0)
        {
            Console.WriteLine(" (no results)");
            continue;
        }

        // Look up neighbor listings and build classify requests per rank
        var pairsToClassify = new List<(int Rank, float Score, ClassifyPairRequest Request)>();

        for (var i = 0; i < neighbors.Count; i++)
        {
            var neighbor = neighbors[i];
            if (!allListings.TryGetValue(neighbor.Id, out var neighborListing))
            {
                continue; // Listing not in database (deleted?)
            }

            pairsToClassify.Add((
                Rank: i + 1,
                Score: neighbor.Score,
                Request: new ClassifyPairRequest(
                    listing.Title ?? "", listing.Description ?? "",
                    neighborListing.Title ?? "", neighborListing.Description ?? "")
            ));
        }

        // Classify in batches of 128
        var allRequests = pairsToClassify.Select(p => p.Request).ToList();
        var results = await classifier.Classify(allRequests);

        var accepted = 0;
        for (var i = 0; i < pairsToClassify.Count; i++)
        {
            var (rank, score, _) = pairsToClassify[i];
            var result = results[i];

            var entry = rankResults[rank];
            if (result.IsComparable)
            {
                entry.Accepted++;
                accepted++;
            }
            else
            {
                entry.Rejected++;
            }
            entry.Scores.Add(score);
            entry.Confidences.Add(result.Confidence);
            rankResults[rank] = entry;
        }

        totalPairs += pairsToClassify.Count;
        Console.WriteLine($" Classified {pairsToClassify.Count} pairs, {accepted} accepted.");
    }

    // Print results table
    Console.WriteLine();
    Console.WriteLine($"{'=',-60}");
    Console.WriteLine($"K-ANALYSIS RESULTS ({actualSample} listings, K={maxK})");
    Console.WriteLine($"{'=',-60}");
    Console.WriteLine();
    Console.WriteLine($"{"Rank",-8} {"Accepted",10} {"Rejected",10} {"Accept%",10} {"AvgSim",10} {"AvgConf",10} {"Cumul.Acc",10}");
    Console.WriteLine($"{new string('-', 8)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)}");

    var cumulativeAccepted = 0;
    var lastRankWithAcceptance = 0;

    for (var rank = 1; rank <= maxK; rank++)
    {
        var entry = rankResults[rank];
        var total = entry.Accepted + entry.Rejected;
        if (total == 0)
        {
            break;
        }

        cumulativeAccepted += entry.Accepted;
        var acceptPct = 100.0 * entry.Accepted / total;
        var avgSim = entry.Scores.Count > 0 ? entry.Scores.Average() : 0f;
        var avgConf = entry.Confidences.Count > 0 ? entry.Confidences.Average() : 0f;

        if (entry.Accepted > 0)
        {
            lastRankWithAcceptance = rank;
        }

        // Print every rank up to 30, then every 10th, then every 50th
        var shouldPrint = rank <= 30 || rank % 10 == 0 || rank % 50 == 0 || rank == lastRankWithAcceptance;
        if (shouldPrint || entry.Accepted > 0)
        {
            Console.WriteLine($"{rank,-8} {entry.Accepted,10} {entry.Rejected,10} {acceptPct,9:F1}% {avgSim,10:F4} {avgConf,10:F4} {cumulativeAccepted,10}");
        }
    }

    // Summary
    Console.WriteLine();
    Console.WriteLine($"Total pairs classified: {totalPairs:N0}");
    Console.WriteLine($"Total accepted:         {cumulativeAccepted:N0}");
    Console.WriteLine($"Last rank with accept:  {lastRankWithAcceptance}");
    Console.WriteLine();

    // Bucket summary
    Console.WriteLine("Bucket Summary:");
    Console.WriteLine($"{"Bucket",-15} {"Accepted",10} {"Rejected",10} {"Accept%",10}");
    Console.WriteLine($"{new string('-', 15)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)}");

    var buckets = new[] { (1, 10), (11, 20), (21, 30), (31, 50), (51, 100), (101, 200), (201, 300), (301, 500) };
    foreach (var (lo, hi) in buckets)
    {
        var bucketAcc = 0;
        var bucketRej = 0;
        for (var r = lo; r <= hi; r++)
        {
            bucketAcc += rankResults[r].Accepted;
            bucketRej += rankResults[r].Rejected;
        }
        var bucketTotal = bucketAcc + bucketRej;
        if (bucketTotal == 0)
        {
            break;
        }
        var pct = 100.0 * bucketAcc / bucketTotal;
        Console.WriteLine($"{"K=" + lo + "-" + hi,-15} {bucketAcc,10} {bucketRej,10} {pct,9:F1}%");
    }
}

host.Run();

// Local types for Pinecone REST API response (no SDK needed)
record PineconeFetchResponse(Dictionary<string, PineconeVector>? Vectors);
record PineconeVector(string Id, float[]? Values);
