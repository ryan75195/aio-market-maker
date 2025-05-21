// in AIOMarketMaker.Services (or a Shared DI project)
using AIOMarketMaker.Api.Parsers;
using AIOMarketMaker.Api.Services;
using AIOMarketMaker.Services;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ScraperWorker.Services;
using System.Net;

public static class ScraperServiceCollectionExtensions
{
    public static IServiceCollection AddEbayScraperPipeline(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
        services.AddSingleton<ISearchParser, EbaySearchParser>();
        services.AddHttpClient<IWebscraperClient, WebscraperClient>(client =>
        {
            client.BaseAddress = new Uri("http://localhost:7126");
        });
        services.AddSingleton<IListingParser, EbayListingParser>();
        services.AddSingleton<IEbayScraper, EbayScraper>();
        var connectionString = config.GetValue<string>("StorageConnectionString");

        // Register the TableServiceClient  
        services.AddSingleton(sp =>
            new TableServiceClient(connectionString)
        );

        services.AddSingleton(sp =>
            new BlobServiceClient(connectionString)
        );

        services.AddSingleton<IJobRepository, AzureJobRepository>();

        return services;
    }
}
