// Program.cs
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AIOMarketMaker.Services;

var builder = FunctionsApplication.CreateBuilder(args);

// make sure Functions stuff is wired up
builder.ConfigureFunctionsWebApplication();

// ────────────────────────────────────────────────────────────────
// Register your scraper pipeline services here:

// 1) HttpClient for fetching
builder.Services
       .AddHttpClient<IHtmlFetcher, HtmlFetcher>();

// 2) URL builder, parser, store and orchestrator
builder.Services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
builder.Services.AddSingleton<IEbayItemParser, EbayItemParser>();
builder.Services.AddSingleton<IEbayScraper, EbayScraper>();

// ────────────────────────────────────────────────────────────────

var host = builder.Build();
host.Run();
