// HostHelper.cs
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using AIOMarketMaker.Core.Services;
using ScraperWorker.Services;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Etl.Data.Migrations;
using AIOMarketMaker.Core.Configuration;
using AIOMarketMaker.Etl.Services;
using AIOMarketMaker.Core.Services.EntityResolution;
using AIOMarketMaker.Core.Services.VectorSearch;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using OpenAI;

namespace AIOMarketMaker.Etl
{
    public static class HostHelper
    {
        public static IHost CreateHost(string[] args)
        {
            // 1. Configure Serilog
            Log.Logger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .WriteTo.Console()    // requires Serilog.Sinks.Console
                .CreateLogger();

            return Host.CreateDefaultBuilder(args)
                // 2. Load JSON & environment variables
                .ConfigureAppConfiguration((hostingCtx, config) =>
                {
                    // point at your working folder where appsettings.json or local.settings.json lives
                    config.SetBasePath(Directory.GetCurrentDirectory())
                          .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                          .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                          .AddEnvironmentVariables();
                })
                // 3. Wire up Serilog into Microsoft.Extensions.Logging
                .UseSerilog()
                // 4. Register your services
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

                    // Enable WAL mode for concurrent access from multiple processes
                    using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(sqliteConnectionString))
                    {
                        connection.Open();
                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = "PRAGMA journal_mode=WAL;";
                        var result = cmd.ExecuteScalar();
                        Log.Information("SQLite journal mode set to: {Mode}", result);
                    }

                    // Register DbContext
                    services.AddDbContext<EtlDbContext>(options =>
                        options.UseSqlite(sqliteConnectionString));

                    // Register the TableServiceClient
                    services.AddSingleton(sp =>
                        new TableServiceClient(storageConnectionString)
                    );

                    services.AddSingleton(sp =>
                        new BlobServiceClient(storageConnectionString)
                    );

                    services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
                    services.AddSingleton<IWebscraperClient, WebscraperClient>();
                    services.AddSingleton<ISearchParser, EbaySearchParser>();
                    services.AddSingleton<IListingParser, EbayListingParser>();
                    services.AddSingleton<IJobRepository, AzureJobRepository>();
                    services.AddSingleton<IEbayScraper, EbayScraper>();

                    // HttpClient for WebscraperClient (if it makes HTTP calls)
                    services.AddHttpClient<IWebscraperClient, WebscraperClient>(client => {
                        client.BaseAddress = new Uri("http://localhost:7126");
                    });

                    // OpenAI Entity Resolution (required - throws if not configured)
                    var openAiSettings = configuration.GetSection("OpenAi").Get<OpenAiSettings>()
                        ?? throw new InvalidOperationException("OpenAi configuration section is required in settings");

                    if (string.IsNullOrEmpty(openAiSettings.ApiKey))
                        throw new InvalidOperationException("OpenAi:ApiKey is required");

                    services.AddSingleton(openAiSettings);
                    services.AddSingleton(new OpenAIClient(openAiSettings.ApiKey));
                    services.AddSingleton<PromptBuilder>();

                    // Pinecone vector search (optional - uses no-op if not configured)
                    var pineconeSettings = configuration.GetSection("Pinecone").Get<PineconeSettings>();
                    var embeddingSettings = configuration.GetSection("Embedding").Get<EmbeddingSettings>()
                        ?? new EmbeddingSettings();

                    services.AddSingleton(embeddingSettings);

                    if (pineconeSettings != null && !string.IsNullOrEmpty(pineconeSettings.ApiKey))
                    {
                        services.AddSingleton(pineconeSettings);
                        services.AddSingleton<IEmbeddingService, OpenAiEmbeddingService>();
                        services.AddSingleton<IPineconeService, PineconeService>();
                        services.AddSingleton<IProductNameIndexer, ProductNameIndexer>();
                        Log.Information("Pinecone vector search enabled");
                    }
                    else
                    {
                        services.AddSingleton<IEmbeddingService, NoOpEmbeddingService>();
                        services.AddSingleton<IPineconeService, NoOpPineconeService>();
                        services.AddSingleton<IProductNameIndexer, NoOpProductNameIndexer>();
                        Log.Warning("Pinecone not configured - vector search disabled");
                    }

                    services.AddSingleton<IEntityResolutionService, OpenAiEntityResolutionService>();
                    services.AddScoped<IJobRunner, JobRunner>();
                })
                .Build();
        }
    }
}
