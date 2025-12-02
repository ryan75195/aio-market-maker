// HostHelper.cs
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using AIOMarketMaker.Services;
using ScraperWorker.Services;
using AIOMarketMaker.Api.Services;
using AIOMarketMaker.Api.Parsers;
using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Etl.Data.Migrations;
using AIOMarketMaker.Etl.Configuration;
using AIOMarketMaker.Etl.Services;
using AIOMarketMaker.Etl.Services.EntityResolution;
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
                    services.AddSingleton<IEntityResolutionService, OpenAiEntityResolutionService>();
                    services.AddScoped<IJobRunner, JobRunner>();
                })
                .Build();
        }
    }
}
