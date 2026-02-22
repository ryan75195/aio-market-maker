using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Migrations;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Services;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Etl.Services;
using ScraperWorker.Services;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using System.Text.Json;
using System.Net;
using System.Threading.Channels;
using AngleSharp.Html.Parser;
using Serilog;
using Serilog.Formatting.Compact;

// Configure Serilog with optional file sink
var logSessionPath = Environment.GetEnvironmentVariable("LOG_SESSION_PATH");

var loggerConfig = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Component", "AIOMarketMaker.Etl")
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
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

            services.AddDbContextFactory<EtlDbContext>(options =>
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

            services.AddDbContextFactory<EtlDbContext>(options =>
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
            MergesPath: configuration.GetValue<string>("VariantClassifier:MergesPath") ?? "models/variant-classifier/merges.txt",
            MaxLength: configuration.GetValue<int?>("VariantClassifier:MaxLength") ?? 256);
        services.AddSingleton(classifierConfig);
        services.AddSingleton<VariantModelRunner>();

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

if (args.Contains("--benchmark"))
{
    await RunBenchmark(host);
    return;
}

if (args.Contains("--reindex-missing"))
{
    await ReindexMissing(host);
    return;
}

if (args.Contains("--clean-descriptions"))
{
    var limit = GetIntArg(args, "--limit");
    await CleanContaminatedDescriptions(host, limit);
    return;
}

if (args.Contains("--batch-label"))
{
    await RunBatchLabel(host, args);
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
    Console.WriteLine("Predictions are computed live via ListingPredictionService.");
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

    var batchSize = 100; // Keep URL under 8KB (12-char IDs × 100 ≈ 2KB query string)
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

        if (batchNum % 100 == 0 || batchNum == batchCount)
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

static async Task ReindexMissing(IHost host)
{
    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
    var vectorIndex = scope.ServiceProvider.GetRequiredService<IVectorIndex>();
    var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

    Console.WriteLine("=== Re-index Missing Listings ===");
    Console.WriteLine($"Current index size: {vectorIndex.Count:N0} vectors");
    Console.WriteLine();

    // Load all listings that are missing from the index
    Console.Write("Loading listings from database...");
    var allListings = await db.Listings
        .AsNoTracking()
        .Where(l => l.Title != null)
        .Select(l => new { l.ListingId, l.Title, l.Description, l.ListingStatus })
        .ToListAsync();
    Console.WriteLine($" {allListings.Count:N0} listings.");

    var missing = allListings.Where(l => !vectorIndex.Contains(l.ListingId)).ToList();
    Console.WriteLine($"Missing from index: {missing.Count:N0}");
    Console.WriteLine($"  Active: {missing.Count(l => l.ListingStatus == "Active"):N0}");
    Console.WriteLine($"  Sold:   {missing.Count(l => l.ListingStatus == "Sold"):N0}");

    if (missing.Count == 0)
    {
        Console.WriteLine("Nothing to do — all listings are indexed.");
        return;
    }

    Console.WriteLine();

    // Build embedding text for each (same logic as ListingIndexingService.BuildEmbeddingText)
    // Truncate to 6000 chars to stay well under text-embedding-3-large's 8192 token limit
    // (eBay HTML descriptions tokenize at ~3-4 chars/token, so 6000 chars ≈ 1500-2000 tokens)
    const int maxChars = 6_000;
    var items = missing
        .Select(l => new
        {
            l.ListingId,
            Text = TruncateText(
                string.Join(" ", new[] { l.Title, l.Description }.Where(s => !string.IsNullOrWhiteSpace(s))),
                maxChars)
        })
        .Where(x => !string.IsNullOrWhiteSpace(x.Text))
        .ToList();

    Console.WriteLine($"Embedding {items.Count:N0} listings in batches of 50...");

    var batchSize = 50;
    var embedded = 0;
    var errors = 0;
    var batches = items.Chunk(batchSize);
    var batchCount = (int)Math.Ceiling(items.Count / (double)batchSize);
    var batchNum = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();

    foreach (var batch in batches)
    {
        batchNum++;
        var texts = batch.Select(b => b.Text).ToList();
        float[][] embeddings;
        try
        {
            embeddings = await embeddingService.GetEmbeddings(texts);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Batch {batchNum} failed ({ex.Message}), falling back to individual...");
            embeddings = new float[batch.Length][];
            for (int j = 0; j < batch.Length; j++)
            {
                try
                {
                    embeddings[j] = await embeddingService.GetEmbedding(batch[j].Text);
                }
                catch
                {
                    errors++;
                }
            }
        }

        for (int i = 0; i < batch.Length; i++)
        {
            if (embeddings[i] != null)
            {
                vectorIndex.Upsert(batch[i].ListingId, embeddings[i]);
                embedded++;
            }
        }

        if (batchNum % 10 == 0 || batchNum == batchCount)
        {
            var elapsed = sw.Elapsed;
            var rate = embedded / elapsed.TotalSeconds;
            var remaining = (items.Count - embedded) / rate;
            Console.WriteLine($"  Batch {batchNum}/{batchCount}: {embedded:N0} embedded ({rate:F0}/sec, ~{remaining:F0}s remaining)");
        }
    }

    // Save the updated index
    Console.Write("Saving index...");
    vectorIndex.Save();
    Console.WriteLine(" done.");
    Console.WriteLine();
    Console.WriteLine($"Re-indexed {embedded:N0} listings in {sw.Elapsed.TotalSeconds:F0}s");
    if (errors > 0)
    {
        Console.WriteLine($"Failed to embed {errors:N0} listings (likely exceeding token limits)");
    }
    Console.WriteLine($"Index now contains {vectorIndex.Count:N0} vectors");
}

static int? GetIntArg(string[] args, string flag)
{
    var idx = Array.IndexOf(args, flag);
    if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var val))
    {
        return val;
    }
    return null;
}

static string? GetStringArg(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static async Task RunBatchLabel(IHost host, string[] args)
{
    var configuration = host.Services.GetRequiredService<IConfiguration>();
    var apiKey = configuration.GetValue<string>("OpenAi:ApiKey")
        ?? throw new InvalidOperationException("OpenAi:ApiKey is required");
    var logger = host.Services.GetRequiredService<ILogger<BatchLabeler>>();
    var labeler = new BatchLabeler(apiKey, logger);

    var csvPath = GetStringArg(args, "--csv")
        ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AIOMarketMaker.ML", "Training", "data", "labeled_pairs_v8.csv");
    var workingDir = GetStringArg(args, "--output-dir")
        ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AIOMarketMaker.ML", "Training", "data");

    Directory.CreateDirectory(workingDir);
    var statePath = Path.Combine(workingDir, "batch_state.json");
    var mergedCsv = Path.Combine(workingDir, "labeled_pairs_v10.csv");

    var subcommand = args.FirstOrDefault(a => a is "start" or "status") ?? "status";

    switch (subcommand)
    {
        case "start":
        {
            if (File.Exists(statePath))
            {
                Console.WriteLine($"Batch already in progress (state file exists at {statePath}).");
                Console.WriteLine("Run 'status' to check progress, or delete batch_state.json to start fresh.");
                return;
            }

            Console.WriteLine("Generating JSONL from v8 CSV...");
            var (chunkFiles, totalPairs) = await BatchLabeler.GenerateBatchInput(csvPath, workingDir);
            var chunkList = chunkFiles.ToList();
            Console.WriteLine($"Generated {totalPairs:N0} batch requests across {chunkList.Count} file(s)");

            var batches = new List<object>();
            for (var i = 0; i < chunkList.Count; i++)
            {
                Console.WriteLine($"Submitting batch {i + 1}/{chunkList.Count}...");
                var batchId = await labeler.SubmitBatch(chunkList[i], workingDir);
                Console.WriteLine($"  Batch {i + 1}: {batchId}");
                batches.Add(new { batchId, inputFile = Path.GetFileName(chunkList[i]) });
            }

            // Save state with all batch IDs (overwrite the single-batch state files)
            await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new { batches }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"\nAll {chunkList.Count} batches submitted. Run '--batch-label status' to check progress.");
            break;
        }

        case "status":
        {
            if (!File.Exists(statePath))
            {
                Console.WriteLine("No batch in progress. Run '--batch-label start' first.");
                return;
            }

            var stateJson = await File.ReadAllTextAsync(statePath);
            var state = JsonSerializer.Deserialize<JsonElement>(stateJson);
            var batchArray = state.GetProperty("batches");

            var allComplete = true;
            var allStatuses = new List<(string BatchId, BatchStatusResult Status)>();

            var totalCompleted = 0;
            var totalRequests = 0;
            var totalFailed = 0;

            for (var i = 0; i < batchArray.GetArrayLength(); i++)
            {
                var entry = batchArray[i];
                var batchId = entry.GetProperty("batchId").GetString()!;
                var status = await labeler.GetBatchStatus(batchId);
                allStatuses.Add((batchId, status));

                totalCompleted += status.Completed;
                totalRequests += status.Total;
                totalFailed += status.Failed;

                Console.WriteLine($"Batch {i + 1}/{batchArray.GetArrayLength()} ({batchId}):");
                Console.WriteLine($"  Status:    {status.Status}");
                Console.WriteLine($"  Completed: {status.Completed:N0} / {status.Total:N0}");
                if (status.Failed > 0)
                {
                    Console.WriteLine($"  Failed:    {status.Failed:N0}");
                }

                if (!status.IsTerminal)
                {
                    allComplete = false;
                }
                else if (status.Status != "completed")
                {
                    Console.WriteLine($"  ** Batch ended with status: {status.Status} **");
                }
            }

            Console.WriteLine();
            var pct = totalRequests > 0 ? 100.0 * totalCompleted / totalRequests : 0;
            Console.WriteLine($"Overall: {totalCompleted:N0} / {totalRequests:N0} ({pct:F1}%) completed, {totalFailed:N0} failed");

            if (!allComplete)
            {
                Console.WriteLine("\nBatches still running. Check back later.");
                return;
            }

            if (allStatuses.Any(s => s.Status.Status != "completed"))
            {
                Console.WriteLine("\nSome batches failed. Check output above.");
                return;
            }

            Console.WriteLine("\nAll batches complete! Download and merge results? (y/n)");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is not "y" and not "yes")
            {
                Console.WriteLine("Skipped. Run '--batch-label status' again when ready.");
                return;
            }

            var outputFiles = new List<string>();
            for (var i = 0; i < allStatuses.Count; i++)
            {
                var (batchId, status) = allStatuses[i];
                var outputPath = Path.Combine(workingDir, $"batch_output_{i}.jsonl");
                Console.WriteLine($"Downloading batch {i + 1}/{allStatuses.Count}...");
                await labeler.DownloadResults(status.OutputFileId!, outputPath);
                outputFiles.Add(outputPath);
            }

            Console.WriteLine("Merging with original CSV...");
            var mergeResult = await BatchLabeler.MergeResults(csvPath, outputFiles, mergedCsv);
            Console.WriteLine($"Merged {mergeResult.Total:N0} pairs: {mergeResult.Agreed:N0} agreed, {mergeResult.Disagreed:N0} disagreed, {mergeResult.Errors:N0} errors");

            Console.WriteLine("\nDisagreement analysis:");
            await BatchLabeler.AnalyzeDisagreements(mergedCsv);

            Console.WriteLine($"\nOutput: {mergedCsv}");
            break;
        }
    }
}

static async Task CleanContaminatedDescriptions(IHost host, int? limit)
{
    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var urlBuilder = scope.ServiceProvider.GetRequiredService<IEbayUrlBuilder>();
    var listingParser = scope.ServiceProvider.GetRequiredService<IListingParser>();
    var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
    var vectorIndex = scope.ServiceProvider.GetRequiredService<IVectorIndex>();

    // Configure HTTP client with residential proxy for direct fetching
    var proxyString = configuration.GetValue<string>("ResidentialProxy");
    HttpClient httpClient;
    if (!string.IsNullOrWhiteSpace(proxyString))
    {
        var parts = proxyString.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || !int.TryParse(parts[3], out var port))
        {
            throw new InvalidOperationException(
                "ResidentialProxy must be in format username:password:host:port");
        }

        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = new WebProxy($"http://{parts[2]}:{port}")
            {
                Credentials = new NetworkCredential(parts[0], parts[1])
            }
        };
        httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        Console.WriteLine($"Proxy configured: {parts[2]}:{port}");
    }
    else
    {
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        Console.WriteLine("WARNING: No proxy configured (ResidentialProxy not set). Using direct HTTP.");
    }

    Console.WriteLine("=== Clean Contaminated Descriptions ===");
    Console.WriteLine();

    // Step 1: Find contaminated listings
    Console.Write("Scanning for CSS-contaminated descriptions...");
    var allContaminated = await db.Listings
        .AsNoTracking()
        .Where(l => l.Description != null
            && (l.Description.Contains("font-size")
                || l.Description.Contains("margin:")
                || l.Description.Contains("{*zoom")
                || l.Description.Contains("border-radius")
                || l.Description.Contains("@media")))
        .OrderBy(l => l.Id)
        .Select(l => new ContaminatedListing(l.ListingId, l.Title, l.Description!.Length))
        .ToListAsync();
    Console.WriteLine($" {allContaminated.Count:N0} found.");

    if (allContaminated.Count == 0)
    {
        Console.WriteLine("No contaminated descriptions found. Nothing to do.");
        return;
    }

    var contaminated = limit.HasValue ? allContaminated.Take(limit.Value).ToList() : allContaminated;
    var total = contaminated.Count;

    // Step 2: Pre-run summary
    var embeddingBatches = (int)Math.Ceiling(total / 50.0);
    Console.WriteLine();
    Console.WriteLine($"Found:       {allContaminated.Count:N0} contaminated listings");
    Console.WriteLine($"Processing:  {total}{(limit.HasValue ? $" (--limit {limit.Value})" : " (all)")}");
    Console.WriteLine($"Concurrency: 50 parallel HTTP requests (direct, no browser)");
    Console.WriteLine();
    Console.WriteLine("Costs:");
    Console.WriteLine($"  HTTP requests:     {total} (via proxy)");
    Console.WriteLine($"  OpenAI embeddings: {embeddingBatches} batch(es) of up to 50 (~${embeddingBatches * 0.01m:F2})");
    Console.WriteLine($"  DB updates:        {total} rows");
    Console.WriteLine($"  Vector upserts:    {total}");
    Console.WriteLine();
    Console.Write("Proceed? (y/n) ");
    var response = Console.ReadLine()?.Trim().ToLowerInvariant();
    if (response != "y")
    {
        Console.WriteLine("Aborted.");
        return;
    }

    Console.WriteLine();

    // Bot detection keywords
    var botKeywords = new[] { "captcha", "blocked", "security check", "access denied", "rate limit", "too many requests", "please verify" };
    var consecutiveBotDetections = 0;
    const int maxConsecutiveBotDetections = 5;
    var aborted = false;

    // Step 3: Phase 1 — Fetch & update (producer/consumer with SemaphoreSlim(50))
    var channel = Channel.CreateUnbounded<CleanResult>();
    var concurrency = new SemaphoreSlim(50);
    var processed = 0;
    var cleaned = 0;
    var errors = 0;
    var botDetections = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var oldLengths = new List<int>();
    var newLengths = new List<int>();

    var producerTask = Task.Run(async () =>
    {
        var fetchTasks = contaminated.Select(async item =>
        {
            if (aborted)
            {
                return;
            }

            await concurrency.WaitAsync();
            string? description = null;
            Exception? error = null;
            try
            {
                var url = urlBuilder.BuildDescriptionUrl(item.ListingId);
                var html = await httpClient.GetStringAsync(url);

                // Bot detection: check for suspiciously small responses with bot keywords
                if (html.Length < 5_000)
                {
                    var htmlLower = html.ToLowerInvariant();
                    if (botKeywords.Any(kw => htmlLower.Contains(kw)))
                    {
                        Interlocked.Increment(ref botDetections);
                        var consecutive = Interlocked.Increment(ref consecutiveBotDetections);
                        error = new InvalidOperationException(
                            $"Bot detection: {html.Length} bytes, keywords found");
                        if (consecutive >= maxConsecutiveBotDetections)
                        {
                            aborted = true;
                            Console.WriteLine();
                            Console.WriteLine($"  ABORT: {maxConsecutiveBotDetections} consecutive bot detections. Stopping.");
                        }
                        return;
                    }
                }

                // Reset consecutive counter on success
                Interlocked.Exchange(ref consecutiveBotDetections, 0);

                var parser = new HtmlParser();
                var doc = await parser.ParseDocumentAsync(html);
                description = listingParser.ParseDescription(doc);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                concurrency.Release();
            }

            if (!aborted)
            {
                await channel.Writer.WriteAsync(
                    new CleanResult(item.ListingId, item.Title, description, item.DescLen, error));
            }
        });

        await Task.WhenAll(fetchTasks);
        channel.Writer.Complete();
    });

    // Consumer: update DB sequentially, collect for embedding
    var toEmbed = new List<(string ListingId, string Text)>();
    await foreach (var result in channel.Reader.ReadAllAsync())
    {
        if (result.Error != null)
        {
            errors++;
            Console.WriteLine($"  ERROR [{result.ListingId}]: {result.Error.Message}");
            processed++;
            continue;
        }

        var listing = await db.Listings.FirstAsync(l => l.ListingId == result.ListingId);
        listing.Description = result.NewDescription;
        listing.DescriptionStatus = result.NewDescription != null ? "complete" : "missing";
        listing.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Track lengths for summary
        oldLengths.Add(result.OldDescriptionLength);
        if (result.NewDescription != null)
        {
            newLengths.Add(result.NewDescription.Length);
        }

        // Collect for Phase 2 embedding
        var embeddingText = BuildEmbeddingText(result.Title, result.NewDescription);
        if (!string.IsNullOrWhiteSpace(embeddingText))
        {
            toEmbed.Add((result.ListingId, TruncateText(embeddingText, 6_000)));
        }

        processed++;
        if (result.NewDescription?.Length != result.OldDescriptionLength)
        {
            cleaned++;
        }

        if (processed % 10 == 0 || processed == total)
        {
            var elapsed = sw.Elapsed;
            var rate = processed / elapsed.TotalSeconds;
            var remaining = (total - processed) / rate;
            Console.WriteLine($"  {processed}/{total}: {cleaned} cleaned, {errors} failed ({rate:F1}/sec, ETA {remaining:F0}s)");
        }
    }
    await producerTask;

    // Step 4: Phase 2 — Batch re-embed
    Console.WriteLine();
    Console.WriteLine($"Phase 2: Re-embedding {toEmbed.Count} listings in batches of 50...");

    var embedded = 0;
    var embedErrors = 0;
    foreach (var batch in toEmbed.Chunk(50))
    {
        var texts = batch.Select(b => b.Text).ToList();
        try
        {
            var embeddings = await embeddingService.GetEmbeddings(texts);
            for (int i = 0; i < batch.Length; i++)
            {
                if (embeddings[i] != null)
                {
                    vectorIndex.Upsert(batch[i].ListingId, embeddings[i]);
                    embedded++;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Embedding batch failed: {ex.Message}");
            embedErrors += batch.Length;
        }
    }

    if (embedded > 0)
    {
        Console.Write("Saving vector index...");
        vectorIndex.Save();
        Console.WriteLine(" done.");
    }

    // Step 5: Summary
    sw.Stop();
    httpClient.Dispose();
    Console.WriteLine();
    Console.WriteLine("=== Results ===");
    Console.WriteLine($"Processed:    {processed} listings in {sw.Elapsed.TotalSeconds:F0}s");
    Console.WriteLine($"Cleaned:      {cleaned} (description updated)");
    Console.WriteLine($"Unchanged:    {processed - cleaned - errors} (description was already clean after re-parse)");
    Console.WriteLine($"Failed:       {errors} (fetch error)");
    Console.WriteLine($"Bot detected: {botDetections}");
    Console.WriteLine($"Embedded:     {embedded}");
    if (aborted)
    {
        Console.WriteLine($"ABORTED:      {total - processed} listings skipped due to bot detection");
    }

    if (oldLengths.Count > 0 && newLengths.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Before: avg {oldLengths.Average():N0} chars (with CSS noise)");
        Console.WriteLine($"After:  avg {newLengths.Average():N0} chars (clean text)");
    }
}

static string BuildEmbeddingText(string? title, string? description)
{
    return string.Join(" ", new[] { title, description }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

static string TruncateText(string text, int maxChars)
{
    if (text.Length <= maxChars)
    {
        return text;
    }
    return text[..maxChars];
}

static async Task RunBenchmark(IHost host)
{
    using var scope = host.Services.CreateScope();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
    var vectorIndex = scope.ServiceProvider.GetRequiredService<IVectorIndex>();

    // Pinecone credentials for comparison
    var pineconeApiKey = configuration.GetValue<string>("Pinecone:ApiKey")
        ?? configuration.GetValue<string>("Values:Pinecone:ApiKey")
        ?? throw new InvalidOperationException("Pinecone:ApiKey required for benchmark");
    var pineconeHost = configuration.GetValue<string>("Pinecone:Host")
        ?? configuration.GetValue<string>("Values:Pinecone:Host")
        ?? "arbitrage-d207f30.svc.aped-4627-b74a.pinecone.io";

    Console.WriteLine("=== Vector Search Benchmark: Local USearch vs Pinecone Cloud ===");
    Console.WriteLine($"Local index: {vectorIndex.Count:N0} vectors");
    Console.WriteLine($"Pinecone host: {pineconeHost}");
    Console.WriteLine();

    // Pick 20 random listings that exist in the local index
    var sampleIds = await db.Listings
        .AsNoTracking()
        .Where(l => l.ListingStatus == "Active")
        .OrderBy(l => Guid.NewGuid())
        .Select(l => l.ListingId)
        .Take(100)
        .ToListAsync();

    // Filter to only those in the local index
    var testIds = sampleIds.Where(id => vectorIndex.Contains(id)).Take(20).ToList();
    Console.WriteLine($"Testing with {testIds.Count} listings");
    Console.WriteLine();

    // Set up Pinecone HTTP client
    using var httpClient = new HttpClient();
    httpClient.DefaultRequestHeaders.Add("Api-Key", pineconeApiKey);
    httpClient.BaseAddress = new Uri($"https://{pineconeHost}");
    var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    var localTimes = new List<double>();
    var pineconeTimes = new List<double>();
    var sw = System.Diagnostics.Stopwatch.StartNew();

    Console.WriteLine($"{"#",-3} {"ListingId",-16} {"Local (ms)",10} {"Pinecone (ms)",14} {"Local Hits",11} {"PC Hits",8} {"Top Match",10}");
    Console.WriteLine(new string('-', 80));

    for (int i = 0; i < testIds.Count; i++)
    {
        var id = testIds[i];

        // Local USearch query
        sw.Restart();
        var localResults = vectorIndex.SearchById(id, 50).ToList();
        sw.Stop();
        var localMs = sw.Elapsed.TotalMilliseconds;
        localTimes.Add(localMs);

        // Pinecone query: first fetch the vector, then query
        sw.Restart();

        // Fetch vector from Pinecone
        var fetchUrl = $"/vectors/fetch?ids={Uri.EscapeDataString(id)}";
        var fetchResponse = await httpClient.GetAsync(fetchUrl);
        fetchResponse.EnsureSuccessStatusCode();
        var fetchJson = await fetchResponse.Content.ReadAsStringAsync();
        var fetchData = JsonSerializer.Deserialize<PineconeFetchResponse>(fetchJson, jsonOptions);
        var vector = fetchData?.Vectors?.GetValueOrDefault(id)?.Values;

        int pineconeHits = 0;
        float pineconeTopScore = 0;
        if (vector != null)
        {
            // Query Pinecone with the vector
            var queryBody = JsonSerializer.Serialize(new
            {
                vector = vector,
                topK = 50,
                includeValues = false
            });
            var queryResponse = await httpClient.PostAsync("/query",
                new StringContent(queryBody, System.Text.Encoding.UTF8, "application/json"));
            queryResponse.EnsureSuccessStatusCode();
            var queryJson = await queryResponse.Content.ReadAsStringAsync();
            var queryData = JsonSerializer.Deserialize<JsonElement>(queryJson);

            if (queryData.TryGetProperty("matches", out var matches))
            {
                pineconeHits = matches.GetArrayLength();
                if (pineconeHits > 0)
                {
                    pineconeTopScore = matches[0].GetProperty("score").GetSingle();
                }
            }
        }
        sw.Stop();
        var pineconeMs = sw.Elapsed.TotalMilliseconds;
        pineconeTimes.Add(pineconeMs);

        var localTopScore = localResults.FirstOrDefault()?.Score ?? 0;
        Console.WriteLine($"{i + 1,-3} {id,-16} {localMs,10:F3} {pineconeMs,14:F1} {localResults.Count,11} {pineconeHits,8} {(localTopScore == pineconeTopScore ? "match" : $"L:{localTopScore:F4} P:{pineconeTopScore:F4}"),10}");
    }

    Console.WriteLine(new string('-', 80));
    Console.WriteLine();
    Console.WriteLine("=== Summary ===");
    Console.WriteLine($"{"Metric",-25} {"Local USearch",15} {"Pinecone Cloud",15} {"Speedup",10}");
    Console.WriteLine(new string('-', 65));
    Console.WriteLine($"{"Mean (ms)",-25} {localTimes.Average(),15:F3} {pineconeTimes.Average(),15:F1} {pineconeTimes.Average() / localTimes.Average(),10:F0}x");
    Console.WriteLine($"{"Median (ms)",-25} {Median(localTimes),15:F3} {Median(pineconeTimes),15:F1} {Median(pineconeTimes) / Median(localTimes),10:F0}x");
    Console.WriteLine($"{"Min (ms)",-25} {localTimes.Min(),15:F3} {pineconeTimes.Min(),15:F1}");
    Console.WriteLine($"{"Max (ms)",-25} {localTimes.Max(),15:F3} {pineconeTimes.Max(),15:F1}");
    Console.WriteLine($"{"P95 (ms)",-25} {Percentile(localTimes, 95),15:F3} {Percentile(pineconeTimes, 95),15:F1}");
    Console.WriteLine();

    var totalLocal = localTimes.Sum();
    var totalPinecone = pineconeTimes.Sum();
    Console.WriteLine($"Total for {testIds.Count} queries: Local {totalLocal:F1}ms vs Pinecone {totalPinecone:F0}ms");
    Console.WriteLine($"Projected for 114K ETL queries: Local {localTimes.Average() * 114000 / 1000:F0}s vs Pinecone {pineconeTimes.Average() * 114000 / 1000 / 60:F0}min");
}

static double Median(List<double> values)
{
    var sorted = values.OrderBy(v => v).ToList();
    int mid = sorted.Count / 2;
    return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
}

static double Percentile(List<double> values, int percentile)
{
    var sorted = values.OrderBy(v => v).ToList();
    int index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
    return sorted[Math.Max(0, index)];
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

// Local types for --clean-descriptions
record CleanResult(string ListingId, string? Title, string? NewDescription, int OldDescriptionLength, Exception? Error);
record ContaminatedListing(string ListingId, string? Title, int DescLen);
