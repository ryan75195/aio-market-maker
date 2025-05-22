// HostHelper.cs
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using AIOMarketMaker.Services;      // your AddEbayScraperPipeline(...)
using ScraperWorker.Services;
using AIOMarketMaker.Api.Services;
using AIOMarketMaker.Api.Parsers;
using Azure.Data.Tables;
using Azure.Storage.Blobs;      // your BackgroundService or orchestrator

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
                    var connectionString = configuration.GetValue<string>("StorageConnectionString");

                    // Register the TableServiceClient  
                    services.AddSingleton(sp =>
                        new TableServiceClient(connectionString)
                    );

                    services.AddSingleton(sp =>
                        new BlobServiceClient(connectionString)
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
                })
                .Build();
        }
    }
}
