// in AIOMarketMaker.Services (or a Shared DI project)
using AIOMarketMaker.Api.Parsers;
using AIOMarketMaker.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

public static class ScraperServiceCollectionExtensions
{
    public static IServiceCollection AddEbayScraperPipeline(this IServiceCollection services)
    {
        // 1) HttpClient for fetching
        services
            .AddHttpClient<IHtmlFetcher, HtmlFetcher>()                         // your typed client
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler   // plug in a custom handler
            {
                AutomaticDecompression = DecompressionMethods.GZip
                                        | DecompressionMethods.Deflate
                                        | DecompressionMethods.Brotli,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            });

        // 2) URL builder, parser, store and orchestrator
        services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
        services.AddSingleton<ISearchParser, EbaySearchParser>();
        services.AddSingleton<IListingParser, EbayListingParser>();
        services.AddSingleton<IEbayScraper, EbayScraper>();

        return services;
    }
}
