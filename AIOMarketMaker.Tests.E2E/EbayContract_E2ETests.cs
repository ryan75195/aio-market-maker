using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ScraperWorker.Services;
using System.Net.Sockets;

namespace AIOMarketMaker.Tests.E2E;

[TestFixture]
[Category("E2E")]
[Category("Contract")]
public class EbayContract_E2ETests
{
    private IEbayScraper _scraper = null!;
    private const string ScraperApiUrl = "http://localhost:7126/";

    [SetUp]
    public void SetUp()
    {
        // Check if scraper is available
        if (!IsScraperApiAvailable())
        {
            Assert.Ignore("AIOWebScraper not running on localhost:7126");
        }

        // Build services with REAL eBay URL builder
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

        // Real URL builder (hits actual eBay)
        services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();

        // Real parsers
        services.AddSingleton<ISearchParser, EbaySearchParser>();
        services.AddSingleton<IListingParser, EbayListingParser>();

        // Real WebscraperClient
        services.AddHttpClient<IWebscraperClient, WebscraperClient>(client =>
        {
            client.BaseAddress = new Uri(ScraperApiUrl);
        });
        services.AddSingleton(new ScraperApiConfig(ScraperApiUrl, ""));

        // Mock job repository
        var mockJobRepo = new Mock<IJobRepository>();
        services.AddSingleton(mockJobRepo.Object);

        services.AddSingleton<IEbayScraper, EbayScraper>();

        var provider = services.BuildServiceProvider();
        _scraper = provider.GetRequiredService<IEbayScraper>();
    }

    [Test]
    [Explicit]
    public async Task Should_parse_real_ebay_search_page()
    {
        // Act - search real eBay for something common
        var results = await _scraper.SearchActiveListings(
            "iphone case",
            BuyingFormat.BUY_NOW,
            Condition.NEW,
            itemLimit: 5);

        // Assert - should get at least one result if eBay HTML is still parseable
        var resultList = results.ToList();
        Assert.That(resultList, Is.Not.Empty,
            "PARSER MAY BE BROKEN: Could not parse any results from real eBay search page");
        Assert.That(resultList.First().ListingId, Is.Not.Null.And.Not.Empty,
            "PARSER MAY BE BROKEN: ListingId not extracted");
        Assert.That(resultList.First().Title, Is.Not.Null.And.Not.Empty,
            "PARSER MAY BE BROKEN: Title not extracted");
    }

    [Test]
    [Explicit]
    public async Task Should_parse_real_ebay_listing_page()
    {
        // First, find a real listing ID from search
        var searchResults = await _scraper.SearchActiveListings(
            "phone charger",
            BuyingFormat.BUY_NOW,
            Condition.NEW,
            itemLimit: 1);

        var searchList = searchResults.ToList();
        if (!searchList.Any())
        {
            Assert.Ignore("Could not find any listings to test");
        }

        var listingId = searchList.First().ListingId!;

        // Act - fetch the full listing
        var results = await _scraper.GetItemsFromListings(new[] { listingId });

        // Assert
        var resultList = results.ToList();
        Assert.That(resultList, Has.Count.EqualTo(1),
            "PARSER MAY BE BROKEN: Could not fetch listing details");
        Assert.That(resultList.First().Title, Is.Not.Null.And.Not.Empty,
            "PARSER MAY BE BROKEN: Title not extracted from listing page");
        Assert.That(resultList.First().Price, Is.GreaterThan(0),
            "PARSER MAY BE BROKEN: Price not extracted from listing page");
    }

    private static bool IsScraperApiAvailable()
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("localhost", 7126);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
