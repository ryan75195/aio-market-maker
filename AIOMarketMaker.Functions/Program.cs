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

        // SQL Server database
        var sqlConnectionString = configuration.GetValue<string>("SqlConnectionString")
            ?? throw new InvalidOperationException("SqlConnectionString is required");

        // Run migrations on startup
        var migrationRunner = new MigrationRunner(sqlConnectionString, null, useSqlServer: true);
        migrationRunner.ApplyMigrations();

        services.AddDbContext<EtlDbContext>(options =>
            options.UseSqlServer(sqlConnectionString));

        // Azure Storage clients
        var storageConnectionString = configuration.GetValue<string>("StorageConnectionString") ?? "";
        if (!string.IsNullOrEmpty(storageConnectionString))
        {
            services.AddSingleton(new TableServiceClient(storageConnectionString));
            services.AddSingleton(new BlobServiceClient(storageConnectionString));
            services.AddSingleton<IJobRepository>(sp =>
                new AzureJobRepository(
                    sp.GetRequiredService<TableServiceClient>(),
                    sp.GetRequiredService<BlobServiceClient>(),
                    sp.GetRequiredService<ILogger<AzureJobRepository>>()));
        }

        // Core services
        services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
        services.AddSingleton<ISearchParser, EbaySearchParser>();
        services.AddSingleton<IListingParser, EbayListingParser>();

        // Web scraper client
        var scraperBaseUrl = configuration.GetValue<string>("ScraperApi:BaseUrl") ?? "";
        var scraperApiKey = configuration.GetValue<string>("ScraperApi:ApiKey") ?? "";
        services.AddHttpClient<IWebscraperClient, WebscraperClient>(client =>
        {
            client.BaseAddress = new Uri(scraperBaseUrl);
            client.DefaultRequestHeaders.Add("x-functions-key", scraperApiKey);
        });

        // eBay scraper
        services.AddSingleton<IEbayScraper, EbayScraper>();

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

        // Semantic search service (Pinecone)
        var pineconeConfig = new PineconeConfig(
            ApiKey: configuration.GetValue<string>("Pinecone:ApiKey") ?? "",
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

        // Pricing analysis service
        services.AddSingleton<IPricingAnalysisService, PricingAnalysisService>();

        // Job runner (scoped for DbContext)
        services.AddScoped<IJobRunner, JobRunner>();
    })
    .ConfigureLogging(logging =>
    {
        logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
    })
    .Build();

host.Run();
