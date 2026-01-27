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
using ScraperWorker.Services;
using Azure.Data.Tables;
using Azure.Storage.Blobs;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
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
        var sqlConnectionString = configuration.GetValue<string>("SqlConnectionString");
        if (!string.IsNullOrEmpty(sqlConnectionString))
        {
            services.AddDbContext<EtlDbContext>(options =>
                options.UseSqlServer(sqlConnectionString));
        }

        // Core parsing services
        services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
        services.AddSingleton<ISearchParser, EbaySearchParser>();
        services.AddSingleton<IListingParser, EbayListingParser>();

        // Embedding service (optional)
        var openAiKey = configuration.GetValue<string>("OpenAi:ApiKey") ?? "";
        if (!string.IsNullOrEmpty(openAiKey))
        {
            var embeddingModel = configuration.GetValue<string>("Embedding:Model") ?? "text-embedding-3-large";
            var embeddingDimensions = configuration.GetValue<int>("Embedding:Dimensions", 3072);
            services.AddSingleton(new EmbeddingConfig(openAiKey, embeddingModel, embeddingDimensions));
            services.AddSingleton<IEmbeddingService, EmbeddingService>();
        }

        // Clustering service
        var clusteringConfig = new ClusteringConfig(
            configuration.GetValue<int>("Clustering:MinClusterSize", 8),
            configuration.GetValue<int>("Clustering:MinPoints", 4));
        services.AddSingleton(clusteringConfig);
        services.AddSingleton<IClusteringService, ClusteringService>();

        // Semantic search service (Pinecone) - optional
        var pineconeApiKey = configuration.GetValue<string>("Pinecone:ApiKey") ?? "";
        if (!string.IsNullOrEmpty(pineconeApiKey))
        {
            var pineconeConfig = new PineconeConfig(
                ApiKey: pineconeApiKey,
                IndexName: configuration.GetValue<string>("Pinecone:IndexName") ?? "arbitrage",
                TopK: configuration.GetValue<int>("Pinecone:TopK", 30),
                SimilarityThreshold: configuration.GetValue<float>("Pinecone:SimilarityThreshold", 0.80f));
            services.AddSingleton(pineconeConfig);
            services.AddSingleton<IPineconeIndexClient>(sp =>
            {
                var config = sp.GetRequiredService<PineconeConfig>();
                return new PineconeIndexClientWrapper(config.ApiKey, config.IndexName);
            });
            services.AddSingleton<ISemanticSearchService, SemanticSearchService>();
        }

        // Pricing analysis service
        services.AddSingleton<IPricingAnalysisService, PricingAnalysisService>();
    })
    .ConfigureLogging(logging =>
    {
        logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
    })
    .Build();

host.Run();
