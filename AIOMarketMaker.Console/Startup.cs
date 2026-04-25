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
using AIOMarketMaker.Core.Services.Pipeline;
using AIOMarketMaker.Core.Services.Taxonomy;
using AIOMarketMaker.ML.Services;
using AIOMarketMaker.Console.Tasks;
using LLama.Native;
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
                services.AddSingleton(new ScrapingConfig());
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

                // Comparable pipeline disabled — replaced by taxonomy-based cell pricing.

                // Taxonomy decontamination
                services.AddSingleton<ITitleDecontaminator, TitleDecontaminator>();
                services.AddSingleton<IBrandTokenExtractor>(sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    var apiKey = config.GetValue<string>("OpenAi:ApiKey");
                    if (string.IsNullOrEmpty(apiKey))
                    {
                        return null!;
                    }
                    var model = config.GetValue<string>("OpenAi:TaxonomyModel") ?? "gpt-4o-mini";
                    var client = new OpenAI.Chat.ChatClient(model, apiKey);
                    var logger = sp.GetRequiredService<ILogger<LlmBrandTokenExtractor>>();
                    return new LlmBrandTokenExtractor(client, logger);
                });

                // Taxonomy pipeline — top-down extraction or bottom-up fallback
                var extractionModelPath = configuration.GetValue<string>("Extraction:ModelPath") ?? "";
                if (!string.IsNullOrEmpty(extractionModelPath) && File.Exists(extractionModelPath))
                {
                    var gpuLayers = configuration.GetValue<int>("Extraction:GpuLayers", -1);
                    if (gpuLayers != 0)
                    {
                        var cudaLibPath = Path.Combine(AppContext.BaseDirectory,
                            "runtimes", "win-x64", "native", "cuda12", "llama.dll");
                        NativeLibraryConfig.LLama
                            .WithLibrary(cudaLibPath)
                            .WithLogCallback((level, msg) =>
                                System.Console.Error.WriteLine($"[LLama-{level}] {msg}"));
                    }

                    var extractionConfig = new ExtractionConfig(
                        extractionModelPath,
                        configuration.GetValue<int>("Extraction:MaxTokens", 512),
                        configuration.GetValue<int>("Extraction:ContextSize", 1024),
                        configuration.GetValue<int>("Extraction:GpuLayers", -1));
                    services.AddSingleton(extractionConfig);
                    services.AddSingleton<IExtractionModelRunner, ExtractionModelRunner>();

                    var skeletonModel = configuration.GetValue<string>("OpenAi:SkeletonModel") ?? "gpt-5-nano";
                    services.AddSingleton<ISkeletonGenerator>(sp =>
                    {
                        var apiKey = sp.GetRequiredService<IConfiguration>().GetValue<string>("OpenAi:ApiKey")!;
                        var client = new OpenAI.Chat.ChatClient(skeletonModel, apiKey);
                        var logger = sp.GetRequiredService<ILogger<OpenAiSkeletonGenerator>>();
                        return new OpenAiSkeletonGenerator(client, logger);
                    });

                    services.AddSingleton<ITaxonomyService, TopDownTaxonomyService>();
                }
                else
                {
                    services.AddSingleton<INgramExtractor, NgramExtractor>();
                    services.AddSingleton<IMutualExclusivityAnalyzer, MutualExclusivityAnalyzer>();
                    services.AddSingleton<ICommunityDetector, LouvainCommunityDetector>();
                    services.AddSingleton<ITaxonomyService, TaxonomyService>();
                }
                services.AddScoped<ITaxonomyPersistenceService, TaxonomyPersistenceService>();
                services.AddSingleton<ITaxonomyRefiner>(sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    var apiKey = config.GetValue<string>("OpenAi:ApiKey");
                    if (string.IsNullOrEmpty(apiKey))
                    {
                        return null!;
                    }
                    var model = config.GetValue<string>("OpenAi:TaxonomyModel") ?? "gpt-4o-mini";
                    var client = new OpenAI.Chat.ChatClient(model, apiKey);
                    var logger = sp.GetRequiredService<ILogger<LlmTaxonomyRefiner>>();
                    return new LlmTaxonomyRefiner(client, logger);
                });
                services.AddSingleton<ICellPricingService, CellPricingService>();
                services.AddScoped<ITaxonomyQueryService, TaxonomyQueryService>();
                services.AddScoped<ITaxonomyOpportunityService, TaxonomyOpportunityService>();

                // Pricing options
                services.Configure<PricingOptions>(configuration.GetSection("Pricing"));

                // Task system
                services.AddTaskRunner();
                services.AddTask<MigrateTask>();
                services.AddTask<BatchLabelTask>();
                services.AddTask<BackfillPredictionsTask>();
                services.AddTask<TaxonomyTask>();
                services.AddTask<BackfillTaxonomyTask>();
                services.AddTask<ViewTaxonomyTask>();
                services.AddTask<ArbitrageTask>();
                services.AddTask<BackfillOpportunitiesTask>();
                services.AddTask<BackfillBrandTokensTask>();
                services.AddTask<TestExtractionTask>();
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
