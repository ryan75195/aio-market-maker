using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Serilog;
using AIOMarketMaker.Core.Services;
using ScraperWorker.Services;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Migrations;
using Azure.Data.Tables;
using Azure.Storage.Blobs;

namespace AIOMarketMaker.Etl
{
    public static class HostHelper
    {
        public static IHost CreateHost(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .CreateLogger();

            return Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((hostingCtx, config) =>
                {
                    config.SetBasePath(Directory.GetCurrentDirectory())
                          .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                          .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                          .AddEnvironmentVariables();
                })
                .ConfigureLogging(logging =>
                {
                    // Suppress noisy HttpClient logs
                    logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
                    // Suppress verbose EF Core SQL command logging
                    logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
                })
                .UseSerilog()
                .ConfigureServices((hostingCtx, services) =>
                {
                    var configuration = hostingCtx.Configuration;
                    var storageConnectionString = configuration.GetValue<string>("StorageConnectionString");

                    // SQLite database connection
                    var dbPath = configuration.GetValue<string>("DatabasePath") ?? "etl.db";
                    var sqliteConnectionString = $"Data Source={dbPath}";

                    // Run migrations on startup
                    var migrationRunner = new MigrationRunner(sqliteConnectionString, null);
                    migrationRunner.ApplyMigrations();

                    // Enable WAL mode for concurrent access
                    using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(sqliteConnectionString))
                    {
                        connection.Open();
                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = "PRAGMA journal_mode=WAL;";
                        var result = cmd.ExecuteScalar();
                        Log.Information("SQLite journal mode set to: {Mode}", result);
                    }

                    // Register DbContext
                    services.AddDbContextFactory<EtlDbContext>(options =>
                        options.UseSqlite(sqliteConnectionString));

                    // Azure Storage clients
                    services.AddSingleton(sp => new TableServiceClient(storageConnectionString));
                    services.AddSingleton(sp => new BlobServiceClient(storageConnectionString));

                    // Core scraping services
                    services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
                    services.AddSingleton<IWebscraperClient, WebscraperClient>();
                    services.AddSingleton<ISearchParser, EbaySearchParser>();
                    services.AddSingleton<IListingParser, EbayListingParser>();
                    services.AddSingleton<IJobRepository, AzureJobRepository>();
                    services.AddSingleton<IEbayScraper, EbayScraper>();

                    // Embedding service
                    var openAiKey = configuration.GetValue<string>("OpenAi:ApiKey") ?? "";
                    var embeddingModel = configuration.GetValue<string>("Embedding:Model") ?? "text-embedding-3-small";
                    var embeddingDimensions = configuration.GetValue<int>("Embedding:Dimensions", 1536);
                    services.AddSingleton(new EmbeddingConfig(openAiKey, embeddingModel, embeddingDimensions));
                    services.AddSingleton<IEmbeddingService, EmbeddingService>();

                    // Clustering service
                    var clusteringConfig = new ClusteringConfig(
                        configuration.GetValue<int>("Clustering:MinClusterSize", 5),
                        configuration.GetValue<int>("Clustering:MinPoints", 3)
                    );
                    services.AddSingleton(clusteringConfig);
                    services.AddSingleton<IClusteringService, ClusteringService>();

                    // Vector index (local USearch)
                    var vectorIndexConfig = new VectorIndexConfig(
                        IndexPath: configuration.GetValue<string>("VectorIndex:IndexPath") ?? "./data/vectors.usearch",
                        IdMapPath: configuration.GetValue<string>("VectorIndex:IdMapPath") ?? "./data/vectors-idmap.json",
                        TopK: configuration.GetValue<int>("VectorIndex:TopK", 30),
                        SimilarityThreshold: configuration.GetValue<float>("VectorIndex:SimilarityThreshold", 0.80f));
                    services.AddSingleton(vectorIndexConfig);
                    services.AddSingleton<IVectorIndex>(sp =>
                    {
                        var config = sp.GetRequiredService<VectorIndexConfig>();
                        var index = new USearchVectorIndex(config);
                        if (File.Exists(config.IndexPath) && File.Exists(config.IdMapPath))
                        {
                            index.Load();
                        }
                        return index;
                    });
                    services.AddSingleton<ISemanticSearchService, SemanticSearchService>();

                    // HttpClient for WebscraperClient
                    var scraperBaseUrl = configuration.GetValue<string>("ScraperApi:BaseUrl") ?? "http://localhost:7126";
                    var scraperApiKey = configuration.GetValue<string>("ScraperApi:ApiKey") ?? "";

                    services.AddSingleton(new ScraperApiConfig(scraperBaseUrl, scraperApiKey));
                    services.AddHttpClient<IWebscraperClient, WebscraperClient>(client => {
                        client.BaseAddress = new Uri(scraperBaseUrl);
                    });

                })
                .Build();
        }
    }
}
