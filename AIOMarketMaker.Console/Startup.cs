using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Migrations;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Console.Tasks;

namespace AIOMarketMaker.Console;

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
                var basePath = AppContext.BaseDirectory;
                config.SetBasePath(basePath)
                      .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                      .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                      .AddEnvironmentVariables();
            })
            .UseSerilog()
            .ConfigureServices((hostingCtx, services) =>
            {
                var configuration = hostingCtx.Configuration;

                // SQLite database connection
                var dbPath = configuration.GetValue<string>("DatabasePath") ?? "etl.db";
                var sqliteConnectionString = $"Data Source={dbPath}";

                // Run migrations on startup
                var migrationRunner = new MigrationRunner(sqliteConnectionString, null);
                migrationRunner.ApplyMigrations();

                // Register DbContext
                services.AddDbContext<EtlDbContext>(options =>
                    options.UseSqlite(sqliteConnectionString));

                // Embedding service
                var openAiKey = configuration.GetValue<string>("OpenAi:ApiKey") ?? "";
                var embeddingModel = configuration.GetValue<string>("Embedding:Model") ?? "text-embedding-3-small";
                var embeddingDimensions = configuration.GetValue<int>("Embedding:Dimensions", 1536);
                services.AddSingleton(new EmbeddingConfig(openAiKey, embeddingModel, embeddingDimensions));
                services.AddSingleton<IEmbeddingService, EmbeddingService>();

                // Clustering service - balanced params for good cluster separation
                var clusteringConfig = new ClusteringConfig(
                    configuration.GetValue<int>("Clustering:MinClusterSize", 8),
                    configuration.GetValue<int>("Clustering:MinPoints", 4)
                );
                services.AddSingleton(clusteringConfig);
                services.AddSingleton<IClusteringService, ClusteringService>();

                // Semantic search service (Pinecone)
                var pineconeConfig = new PineconeConfig(
                    ApiKey: configuration.GetValue<string>("Pinecone:ApiKey") ?? "",
                    IndexName: configuration.GetValue<string>("Pinecone:IndexName") ?? "arbitrage",
                    TopK: configuration.GetValue<int>("Pinecone:TopK", 10)
                );
                services.AddSingleton(pineconeConfig);
                services.AddSingleton<IPineconeIndexClient>(sp =>
                {
                    var config = sp.GetRequiredService<PineconeConfig>();
                    return new PineconeIndexClientWrapper(config.ApiKey, config.IndexName);
                });
                services.AddSingleton<ISemanticSearchService, SemanticSearchService>();

                // Pricing analysis service
                services.AddSingleton<IPricingAnalysisService, PricingAnalysisService>();

                // Task system
                services.AddTaskRunner();
                services.AddTask<ExampleTask>();
                services.AddTask<SearchTask>();
                services.AddTask<SearchTestTask>();
                services.AddTask<PricingTask>();
                services.AddTask<TerminateOrchestrationsTask>();
            })
            .Build();
    }
}
