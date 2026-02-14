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

        // Semantic search service (Pinecone - required)
        var pineconeApiKey = configuration.GetValue<string>("Pinecone:ApiKey")
            ?? configuration.GetValue<string>("Values:Pinecone:ApiKey")
            ?? throw new InvalidOperationException("Pinecone:ApiKey is required. Add it to local.settings.json.");
        var pineconeConfig = new PineconeConfig(
            ApiKey: pineconeApiKey,
            IndexName: configuration.GetValue<string>("Pinecone:IndexName")
                ?? configuration.GetValue<string>("Values:Pinecone:IndexName")
                ?? "arbitrage",
            TopK: configuration.GetValue<int?>("Pinecone:TopK")
                ?? configuration.GetValue<int?>("Values:Pinecone:TopK")
                ?? 30,
            SimilarityThreshold: configuration.GetValue<float?>("Pinecone:SimilarityThreshold")
                ?? configuration.GetValue<float?>("Values:Pinecone:SimilarityThreshold")
                ?? 0.80f);
        services.AddSingleton(pineconeConfig);
        services.AddSingleton<IPineconeIndexClient>(sp =>
        {
            var config = sp.GetRequiredService<PineconeConfig>();
            return new PineconeIndexClientWrapper(config.ApiKey, config.IndexName);
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
    Console.WriteLine($"Pinecone queries:       {result.PineconeQueries}");
    Console.WriteLine($"Candidate pairs found:  {result.CandidatePairsFound}");
    Console.WriteLine($"Cache hits:             {result.CacheHits}");
    Console.WriteLine($"LLM calls required:     {result.LlmCallsRequired}");
    Console.WriteLine($"LLM calls made:         {result.LlmCallsMade}");
    Console.WriteLine($"Comparables found:      {result.ComparablesFound}");
    Console.WriteLine($"Predictions written:    {result.PredictionsWritten}");
    return;
}

host.Run();
