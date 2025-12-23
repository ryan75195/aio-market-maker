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

        // SQL Server database (required for the Jobs API)
        var sqlConnectionString = configuration.GetValue<string>("SqlConnectionString");
        if (!string.IsNullOrEmpty(sqlConnectionString))
        {
            // Run migrations on startup
            try
            {
                var migrationRunner = new MigrationRunner(sqlConnectionString, null, useSqlServer: true);
                migrationRunner.ApplyMigrations();
                Console.WriteLine("Database migrations applied successfully");
            }
            catch (Exception ex)
            {
                // Log migration error but don't prevent startup
                Console.WriteLine($"Migration error: {ex.Message}");
                Console.WriteLine(ex.ToString());
            }

            services.AddDbContext<EtlDbContext>(options =>
                options.UseSqlServer(sqlConnectionString));
        }

        // Azure Storage clients (optional)
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

        // Web scraper client (optional)
        var scraperBaseUrl = configuration.GetValue<string>("ScraperApi__BaseUrl") ?? "";
        var scraperApiKey = configuration.GetValue<string>("ScraperApi__ApiKey") ?? "";
        if (!string.IsNullOrEmpty(scraperBaseUrl))
        {
            services.AddHttpClient<IWebscraperClient, WebscraperClient>(client =>
            {
                client.BaseAddress = new Uri(scraperBaseUrl);
                if (!string.IsNullOrEmpty(scraperApiKey))
                {
                    client.DefaultRequestHeaders.Add("x-functions-key", scraperApiKey);
                }
            });

            // eBay scraper (depends on web scraper client)
            services.AddScoped<IEbayScraper, EbayScraper>();

            // Job runner (only register when both DbContext AND EbayScraper are available)
            if (!string.IsNullOrEmpty(sqlConnectionString))
            {
                services.AddScoped<IJobRunner, JobRunner>();
            }
        }

        // Embedding service (optional)
        var openAiKey = configuration.GetValue<string>("OpenAi__ApiKey") ?? "";
        if (!string.IsNullOrEmpty(openAiKey))
        {
            var embeddingModel = configuration.GetValue<string>("Embedding__Model") ?? "text-embedding-3-large";
            var embeddingDimensions = configuration.GetValue<int>("Embedding__Dimensions", 3072);
            services.AddSingleton(new EmbeddingConfig(openAiKey, embeddingModel, embeddingDimensions));
            services.AddSingleton<IEmbeddingService, EmbeddingService>();
        }

        // Clustering service
        var clusteringConfig = new ClusteringConfig(
            configuration.GetValue<int>("Clustering__MinClusterSize", 8),
            configuration.GetValue<int>("Clustering__MinPoints", 4));
        services.AddSingleton(clusteringConfig);
        services.AddSingleton<IClusteringService, ClusteringService>();

        // Semantic search service (Pinecone) - optional
        var pineconeApiKey = configuration.GetValue<string>("Pinecone__ApiKey") ?? "";
        if (!string.IsNullOrEmpty(pineconeApiKey))
        {
            var pineconeConfig = new PineconeConfig(
                ApiKey: pineconeApiKey,
                IndexName: configuration.GetValue<string>("Pinecone__IndexName") ?? "arbitrage",
                TopK: configuration.GetValue<int>("Pinecone__TopK", 30),
                SimilarityThreshold: configuration.GetValue<float>("Pinecone__SimilarityThreshold", 0.80f));
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
