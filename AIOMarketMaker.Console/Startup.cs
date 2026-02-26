using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Formatting.Compact;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Migrations;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Services;
using AIOMarketMaker.Console.Tasks;
using ScraperWorker.Services;

namespace AIOMarketMaker.Console;

public static class HostHelper
{
    public static IHost CreateHost(string[] args)
    {
        ConfigureSerilog();

        return Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostingCtx, config) =>
            {
                var currentDir = Directory.GetCurrentDirectory();
                var baseDir = AppContext.BaseDirectory;

                config.SetBasePath(baseDir)
                      .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                      .AddJsonFile(Path.Combine(currentDir, "local.settings.json"), optional: true, reloadOnChange: false)
                      .AddJsonFile(Path.Combine(baseDir, "local.settings.json"), optional: true, reloadOnChange: false)
                      .AddEnvironmentVariables();

                // Azure Functions stores values under "Values" section - flatten them to root
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
            .UseSerilog()
            .ConfigureServices((hostingCtx, services) =>
            {
                var configuration = hostingCtx.Configuration;

                // Azure Storage clients
                var blobConnectionString = configuration.GetValue<string>("blobStorageConnectionString")
                    ?? configuration.GetValue<string>("AzureWebJobsStorage")
                    ?? "UseDevelopmentStorage=true";
                var tableConnectionString = configuration.GetValue<string>("tableStorageConnectionString")
                    ?? configuration.GetValue<string>("AzureWebJobsStorage")
                    ?? "UseDevelopmentStorage=true";
                services.AddSingleton(_ => new BlobServiceClient(blobConnectionString));
                services.AddSingleton(_ => new TableServiceClient(tableConnectionString));

                services.AddSingleton<IJobRepository>(sp =>
                    new AzureJobRepository(
                        sp.GetRequiredService<TableServiceClient>(),
                        sp.GetRequiredService<BlobServiceClient>(),
                        sp.GetRequiredService<ILogger<AzureJobRepository>>()));

                services.AddSingleton(new DbWriteGate(10));
                services.AddScoped<IScrapeJobProcessor, ScrapeJobProcessor>();

                // Database: SQLite (local dev) or SQL Server (Azure)
                var isSqlServerMigrate = args.Length > 0 && args[0].Equals("migrate", StringComparison.OrdinalIgnoreCase);

                var sqliteConnectionString = configuration.GetValue<string>("SqliteConnectionString");
                if (!string.IsNullOrEmpty(sqliteConnectionString))
                {
                    if (!isSqlServerMigrate)
                    {
                        var migrationRunner = new MigrationRunner(sqliteConnectionString, null);
                        migrationRunner.ApplyMigrations();
                    }

                    services.AddDbContextFactory<EtlDbContext>(options =>
                        options.UseSqlite(sqliteConnectionString));
                    services.AddDbContext<EtlDbContext>(options =>
                        options.UseSqlite(sqliteConnectionString));
                }

                var sqlConnectionString = configuration.GetValue<string>("SqlConnectionString")
                    ?? configuration.GetValue<string>("Values:SqlConnectionString");
                if (!string.IsNullOrEmpty(sqlConnectionString))
                {
                    if (!isSqlServerMigrate)
                    {
                        var migrationRunner = new MigrationRunner(sqlConnectionString, null, useSqlServer: true);
                        migrationRunner.ApplyMigrations();
                    }

                    services.AddDbContextFactory<EtlDbContext>(options =>
                        options.UseSqlServer(sqlConnectionString));
                    services.AddDbContext<EtlDbContext>(options =>
                        options.UseSqlServer(sqlConnectionString));
                }

                // Fallback: if neither SqliteConnectionString nor SqlConnectionString is set,
                // use the old default SQLite path for backward compatibility
                if (string.IsNullOrEmpty(sqliteConnectionString) && string.IsNullOrEmpty(sqlConnectionString))
                {
                    var dbPath = configuration.GetValue<string>("DatabasePath") ?? "etl.db";
                    var fallbackSqlite = $"Data Source={dbPath}";

                    if (!isSqlServerMigrate)
                    {
                        var migrationRunner = new MigrationRunner(fallbackSqlite, null);
                        migrationRunner.ApplyMigrations();
                    }

                    services.AddDbContextFactory<EtlDbContext>(options =>
                        options.UseSqlite(fallbackSqlite));
                    services.AddDbContext<EtlDbContext>(options =>
                        options.UseSqlite(fallbackSqlite));
                }

                // Core parsing services
                services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
                services.AddSingleton<ISearchParser, EbaySearchParser>();
                services.AddSingleton<IListingParser, EbayListingParser>();

                // Web scraper client
                var scraperBaseUrl = configuration.GetValue<string>("Scraper:BaseUrl") ?? "http://localhost:7126";
                var scraperApiKey = configuration.GetValue<string>("Scraper:ApiKey") ?? "";
                services.AddSingleton(new ScraperApiConfig(scraperBaseUrl, scraperApiKey));
                services.AddHttpClient<IWebscraperClient, WebscraperClient>(client =>
                {
                    client.BaseAddress = new Uri(scraperBaseUrl);
                    client.Timeout = TimeSpan.FromMinutes(5);
                });

                // Embedding service
                var openAiKey = configuration.GetValue<string>("OpenAi:ApiKey") ?? "";
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
                    services.AddSingleton<EnsembleConfig>(_ => null!);
                }
                services.AddSingleton<IVariantClassifierClient, VariantClassifier>();

                // ComparablesEtlService
                services.AddScoped<IComparablesEtlService, ComparablesEtlService>();

                // Task system
                services.AddTaskRunner();
                services.AddTask<SearchTask>();
                services.AddTask<SearchTestTask>();
                services.AddTask<PricingTask>();
                services.AddTask<MigrateTask>();
                services.AddTask<BackfillConfidenceTask>();
                services.AddTask<ComparablesTask>();
                services.AddTask<ReindexMissingTask>();
                services.AddTask<ValidationTask>();
                services.AddTask<KAnalysisTask>();
                services.AddTask<BatchLabelTask>();
            })
            .Build();
    }

    private static void ConfigureSerilog()
    {
        var logSessionPath = Environment.GetEnvironmentVariable("LOG_SESSION_PATH");

        var loggerConfig = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Component", "AIOMarketMaker.Console")
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.Console();

        if (!string.IsNullOrEmpty(logSessionPath))
        {
            Directory.CreateDirectory(logSessionPath);
            var logFile = Path.Combine(logSessionPath, "console.json");
            loggerConfig.WriteTo.File(
                new CompactJsonFormatter(),
                logFile,
                rollingInterval: RollingInterval.Hour,
                retainedFileCountLimit: null);
        }

        Log.Logger = loggerConfig.CreateLogger();
    }
}
