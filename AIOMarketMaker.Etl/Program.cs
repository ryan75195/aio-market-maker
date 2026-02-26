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
using AIOMarketMaker.Etl.Commands;
using AIOMarketMaker.Etl.Services;
using ScraperWorker.Services;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
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

        // Variant classifier (local ONNX model + ensemble calibration)
        var classifierConfig = new OnnxClassifierConfig(
            ModelPath: configuration.GetValue<string>("VariantClassifier:ModelPath") ?? "models/variant-classifier/model.onnx",
            VocabPath: configuration.GetValue<string>("VariantClassifier:VocabPath") ?? "models/variant-classifier/vocab.json",
            MergesPath: configuration.GetValue<string>("VariantClassifier:MergesPath") ?? "models/variant-classifier/merges.txt",
            MaxLength: configuration.GetValue<int?>("VariantClassifier:MaxLength") ?? 512);
        services.AddSingleton(classifierConfig);
        services.AddSingleton<VariantModelRunner>();
        services.AddSingleton<IVariantModelRunner>(sp => sp.GetRequiredService<VariantModelRunner>());

        var ensembleLogitWeight = configuration.GetValue<float>("VariantClassifier:Ensemble:LogitWeight");
        if (ensembleLogitWeight != 0)
        {
            services.AddSingleton(new EnsembleConfig(
                LogitWeight: ensembleLogitWeight,
                SimilarityWeight: configuration.GetValue<float>("VariantClassifier:Ensemble:SimilarityWeight"),
                Intercept: configuration.GetValue<float>("VariantClassifier:Ensemble:Intercept")));
        }
        else
        {
            // VariantClassifier accepts EnsembleConfig? — register null so DI resolves without ensemble config
            services.AddSingleton<EnsembleConfig>(_ => null!);
        }
        services.AddSingleton<IVariantClassifierClient, VariantClassifier>();

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
    await ExportVectorsCommand.Run(host, args);
    return;
}

if (args.Contains("--benchmark"))
{
    await BenchmarkCommand.Run(host);
    return;
}

if (args.Contains("--reindex-missing"))
{
    await ReindexMissingCommand.Run(host);
    return;
}

if (args.Contains("--clean-descriptions"))
{
    var limit = CommandHelpers.GetIntArg(args, "--limit");
    await CleanDescriptionsCommand.Run(host, limit);
    return;
}

if (args.Contains("--backfill-descriptions"))
{
    var limit = CommandHelpers.GetIntArg(args, "--limit");
    await BackfillDescriptionsCommand.Run(host, limit);
    return;
}

if (args.Contains("--batch-label"))
{
    await BatchLabelCommand.Run(host, args);
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
    await KAnalysisCommand.Run(host, args);
    return;
}

if (args.Contains("--validate"))
{
    await ValidationCommand.Run(host, args);
    return;
}

if (args.Contains("--backfill-confidence"))
{
    await BackfillConfidenceCommand.Run(host, args);
    return;
}
