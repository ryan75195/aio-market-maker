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
            // One-time database reset - drops all tables and lets migrations rebuild
            // Remove this flag after the reset is complete
            var resetDatabase = configuration.GetValue<bool>("ResetDatabase", false);
            if (resetDatabase)
            {
                try
                {
                    Console.WriteLine("DATABASE RESET: Dropping all tables...");
                    using var conn = new Microsoft.Data.SqlClient.SqlConnection(sqlConnectionString);
                    conn.Open();

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        DECLARE @sql NVARCHAR(MAX) = '';
                        -- Drop all foreign keys first
                        SELECT @sql += 'ALTER TABLE [' + OBJECT_SCHEMA_NAME(parent_object_id) + '].[' + OBJECT_NAME(parent_object_id) + '] DROP CONSTRAINT [' + name + '];' + CHAR(13)
                        FROM sys.foreign_keys;
                        -- Drop all tables
                        SELECT @sql += 'DROP TABLE [' + TABLE_SCHEMA + '].[' + TABLE_NAME + '];' + CHAR(13)
                        FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE';
                        EXEC sp_executesql @sql;
                    ";
                    cmd.ExecuteNonQuery();
                    Console.WriteLine("DATABASE RESET: All tables dropped successfully");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DATABASE RESET ERROR: {ex.Message}");
                }
            }

            // Fix schema issues: Convert IsEnabled from INT to BIT for proper boolean mapping
            try
            {
                using var conn = new Microsoft.Data.SqlClient.SqlConnection(sqlConnectionString);
                conn.Open();

                // Check if IsEnabled is still INT type
                using (var checkCmd = conn.CreateCommand())
                {
                    checkCmd.CommandText = "SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'ScrapeJobs' AND COLUMN_NAME = 'IsEnabled'";
                    var dataType = checkCmd.ExecuteScalar()?.ToString();
                    Console.WriteLine($"IsEnabled DATA_TYPE: {dataType}");

                    if (dataType?.ToLower() == "int")
                    {
                        Console.WriteLine("Converting IsEnabled from INT to BIT...");

                        // Step 0: Clean up any leftover temp column from failed previous runs
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "IF COL_LENGTH('ScrapeJobs', 'IsEnabled_Temp') IS NOT NULL ALTER TABLE ScrapeJobs DROP COLUMN IsEnabled_Temp";
                            cmd.ExecuteNonQuery();
                            Console.WriteLine("Step 0: Cleaned up any existing temp column");
                        }

                        // Step 1: Add temp column
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "ALTER TABLE ScrapeJobs ADD IsEnabled_Temp BIT NOT NULL DEFAULT 1";
                            cmd.ExecuteNonQuery();
                            Console.WriteLine("Step 1: Added IsEnabled_Temp column");
                        }

                        // Step 2: Copy data
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "UPDATE ScrapeJobs SET IsEnabled_Temp = CAST(IsEnabled AS BIT)";
                            cmd.ExecuteNonQuery();
                            Console.WriteLine("Step 2: Copied data to temp column");
                        }

                        // Step 3a: Drop indexes on IsEnabled first
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"
                                DECLARE @sql NVARCHAR(MAX) = '';
                                SELECT @sql += 'DROP INDEX ' + i.name + ' ON ScrapeJobs;' + CHAR(13)
                                FROM sys.indexes i
                                INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                                INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                                WHERE i.object_id = OBJECT_ID('ScrapeJobs') AND c.name = 'IsEnabled';
                                IF @sql <> '' EXEC sp_executesql @sql;
                            ";
                            cmd.ExecuteNonQuery();
                            Console.WriteLine("Step 3a: Dropped indexes on IsEnabled");
                        }

                        // Step 3b: Drop old column
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "ALTER TABLE ScrapeJobs DROP COLUMN IsEnabled";
                            cmd.ExecuteNonQuery();
                            Console.WriteLine("Step 3b: Dropped old IsEnabled column");
                        }

                        // Step 4: Rename temp to IsEnabled
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "EXEC sp_rename 'ScrapeJobs.IsEnabled_Temp', 'IsEnabled', 'COLUMN'";
                            cmd.ExecuteNonQuery();
                            Console.WriteLine("Step 4: Renamed temp column to IsEnabled");
                        }

                        Console.WriteLine("Schema fix: IsEnabled converted from INT to BIT");
                    }
                    else
                    {
                        Console.WriteLine($"Schema fix: IsEnabled is already {dataType}, no conversion needed");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Schema fix error: {ex.Message}");
                Console.WriteLine(ex.ToString());
            }

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
