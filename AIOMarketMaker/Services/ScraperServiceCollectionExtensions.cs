// in AIOMarketMaker.Services (or a Shared DI project)
using AIOMarketMaker.Api.Parsers;
using AIOMarketMaker.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

public static class ScraperServiceCollectionExtensions
{
    public static IServiceCollection AddEbayScraperPipeline(this IServiceCollection services)
    {
        services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
        services.AddSingleton<ISearchParser, EbaySearchParser>();
        services.AddSingleton<IListingParser, EbayListingParser>();
        services.AddSingleton<IEbayScraper, EbayScraper>();

        return services;
    }
}
