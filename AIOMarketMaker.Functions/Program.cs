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
        try
        {
            var configuration = context.Configuration;

            // Application Insights
            services.AddApplicationInsightsTelemetryWorkerService();
            services.ConfigureFunctionsApplicationInsights();

            // SQL Server database (required)
            var sqlConnectionString = configuration.GetValue<string>("SqlConnectionString");
            if (string.IsNullOrEmpty(sqlConnectionString))
            {
                Console.WriteLine("WARNING: SqlConnectionString not configured - database features disabled");
            }
            else
            {
                // Run migrations on startup (with error handling)
                try
                {
                    var migrationRunner = new MigrationRunner(sqlConnectionString, null, useSqlServer: true);
                    migrationRunner.ApplyMigrations();
                    Console.WriteLine("Database migrations completed successfully");
                }
                catch (Exception ex)
                {
                    // Log but don't fail startup - migrations may already be applied
                    Console.WriteLine($"Migration warning: {ex.Message}");
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
            var scraperBaseUrl = configuration.GetValue<string>("ScraperApi:BaseUrl") ?? "";
            var scraperApiKey = configuration.GetValue<string>("ScraperApi:ApiKey") ?? "";
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
                services.AddSingleton<IEbayScraper, EbayScraper>();
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

            // Job runner (scoped for DbContext)
            if (!string.IsNullOrEmpty(sqlConnectionString))
            {
                services.AddScoped<IJobRunner, JobRunner>();
            }

            Console.WriteLine("Service configuration completed successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR during service configuration: {ex}");
            // Re-throw to fail startup if critical configuration fails
            throw;
        }
    })
    .ConfigureLogging(logging =>
    {
        logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
    })
    .Build();

host.Run();
