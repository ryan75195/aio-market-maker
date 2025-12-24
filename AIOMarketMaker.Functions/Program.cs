using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
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

        // SQL Server database - migrations are applied via the GHA pipeline
        var sqlConnectionString = configuration.GetValue<string>("SqlConnectionString");
        if (!string.IsNullOrEmpty(sqlConnectionString))
        {
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
        // Note: Azure uses __ in app settings which maps to : in config hierarchy
        var scraperBaseUrl = configuration.GetValue<string>("ScraperApi:BaseUrl") ?? "";
        var scraperApiKey = configuration.GetValue<string>("ScraperApi:ApiKey") ?? "";
        if (!string.IsNullOrEmpty(scraperBaseUrl))
        {
            services.AddSingleton(new ScraperApiConfig(scraperBaseUrl, scraperApiKey));
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
