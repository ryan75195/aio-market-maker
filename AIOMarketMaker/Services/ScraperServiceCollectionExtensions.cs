// in AIOMarketMaker.Services (or a Shared DI project)
using AIOMarketMaker.Services;
using Microsoft.Extensions.DependencyInjection;

public static class ScraperServiceCollectionExtensions
{
    public static IServiceCollection AddEbayScraperPipeline(this IServiceCollection services)
    {
        // 1) HttpClient for fetching
        services.AddHttpClient<IHtmlFetcher, HtmlFetcher>();

        // 2) URL builder, parser, store and orchestrator
        services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
        services.AddSingleton<IEbayItemParser, EbayItemParser>();
        services.AddSingleton<IEbayScraper, EbayScraper>();

        return services;
    }
}
