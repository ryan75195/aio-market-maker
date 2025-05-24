using AIOMarketMaker.Api.Parsers;
using AIOMarketMaker.Api.Services;
using AIOMarketMaker.Models.Ebay;
using AIOMarketMaker.Services;
using AIOMarketMaker.Tests.Utils;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ScraperWorker.Services;
using System.Reflection;
using System.Text.RegularExpressions;

namespace AIOMarketMaker.Tests.Unit
{
    public class EbayScraperIntegrationTests
    {
        private ServiceProvider _provider = null!;
        private IEbayScraper _serviceUnderTest = null!;
        private IEbayUrlBuilder _urlBuilder;

        public string NormalizeWhitespace(string s) => Regex.Replace(s, @"\s+", " ").Trim();

        [SetUp]
        public async Task SetupAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
            services.AddSingleton<ISearchParser, EbaySearchParser>();
            services.AddSingleton<IListingParser, EbayListingParser>();
            services.AddSingleton<IEbayScraper, EbayScraper>();
            services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();

            _provider = services.BuildServiceProvider();
            _serviceUnderTest = _provider.GetRequiredService<IEbayScraper>();
            _urlBuilder = _provider.GetRequiredService<IEbayUrlBuilder>();

        }

        [TearDown]
        public async Task TearDownAsync()
        {
            try
            {
                await _provider.DisposeAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected teardown exception: {ex.Message}");
            }
        }

        [Test]
        [Description("Integration test: SearchSoldListings with real HTML data")]
        public async Task Should_search_sold_listings_with_real_data()
        {
            var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Search"));
            var htmlPath = Path.Combine(dataDir, "Sold_With_Small_Number_of_Real_Results.htm");
            var html = await File.ReadAllTextAsync(htmlPath);

            var mockFetcher = new Mock<IWebscraperClient>();
            mockFetcher.Setup(x => x.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(html);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
            services.AddSingleton<ISearchParser, EbaySearchParser>();
            services.AddSingleton<IListingParser, EbayListingParser>();
            services.AddSingleton<IWebscraperClient>(mockFetcher.Object);
            services.AddSingleton(Mock.Of<IJobRepository>());
            services.AddSingleton<IEbayScraper, EbayScraper>();

            var provider = services.BuildServiceProvider();
            var scraper = provider.GetRequiredService<IEbayScraper>();

            var startDate = new DateTime(2023, 1, 1);
            var endDate = new DateTime(2025, 12, 31);

            var result = await scraper.SearchSoldListings("test query", BuyingFormat.BUY_NOW, Condition.USED, startDate, endDate);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Not.Null, "Should return results");
                Assert.That(result.All(x => !string.IsNullOrEmpty(x.ListingId)), Is.True, "All items should have listing IDs");
                Assert.That(result.All(x => !string.IsNullOrEmpty(x.Title)), Is.True, "All items should have titles");
            });

            await provider.DisposeAsync();
        }

        [Test]
        [Description("Integration test: SearchActiveListings with real HTML data")]
        public async Task Should_search_active_listings_with_real_data()
        {
            var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Search"));
            var htmlPath = Path.Combine(dataDir, "SearchResultsContainingPriceRanges.htm");
            
            if (!File.Exists(htmlPath))
            {
                Assert.Ignore($"Test data file not found: {htmlPath}");
                return;
            }

            var html = await File.ReadAllTextAsync(htmlPath);

            var mockFetcher = new Mock<IWebscraperClient>();
            mockFetcher.Setup(x => x.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(html);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
            services.AddSingleton<ISearchParser, EbaySearchParser>();
            services.AddSingleton<IListingParser, EbayListingParser>();
            services.AddSingleton<IWebscraperClient>(mockFetcher.Object);
            services.AddSingleton(Mock.Of<IJobRepository>());
            services.AddSingleton<IEbayScraper, EbayScraper>();

            var provider = services.BuildServiceProvider();
            var scraper = provider.GetRequiredService<IEbayScraper>();

            var result = await scraper.SearchActiveListings("test query", BuyingFormat.BUY_NOW, Condition.USED, 50);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Not.Null, "Should return results");
                Assert.That(result.Count(), Is.LessThanOrEqualTo(50), "Should respect item limit");
                Assert.That(result.All(x => !string.IsNullOrEmpty(x.ListingId)), Is.True, "All items should have listing IDs");
                Assert.That(result.All(x => !string.IsNullOrEmpty(x.Title)), Is.True, "All items should have titles");
            });

            await provider.DisposeAsync();
        }

        [Test]
        [Description("Integration test: SearchSoldListings date filtering with real data")]
        public async Task Should_search_sold_listings_filter_by_date_with_real_data()
        {
            var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Search"));
            var htmlPath = Path.Combine(dataDir, "Sold_With_Small_Number_of_Real_Results.htm");
            var html = await File.ReadAllTextAsync(htmlPath);

            var mockFetcher = new Mock<IWebscraperClient>();
            mockFetcher.Setup(x => x.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(html);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
            services.AddSingleton<ISearchParser, EbaySearchParser>();
            services.AddSingleton<IListingParser, EbayListingParser>();
            services.AddSingleton<IWebscraperClient>(mockFetcher.Object);
            services.AddSingleton(Mock.Of<IJobRepository>());
            services.AddSingleton<IEbayScraper, EbayScraper>();

            var provider = services.BuildServiceProvider();
            var scraper = provider.GetRequiredService<IEbayScraper>();

            var startDate = new DateTime(2024, 1, 1);
            var endDate = new DateTime(2024, 12, 31);

            var result = await scraper.SearchSoldListings("test", BuyingFormat.BUY_NOW, Condition.USED, startDate, endDate);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Not.Null, "Should return results");
                if (result.Any())
                {
                    Assert.That(result.All(x => x.EndDateUtc >= startDate && x.EndDateUtc <= endDate), Is.True,
                        "All results should be within the specified date range");
                }
            });

            await provider.DisposeAsync();
        }

        //[Test]
        //public async Task Should_get_active_listing()
        //{
        //    var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Listings"));
        //    var htmlPath = Path.Combine(dataDir, "ActiveBuyItNowListing.htm");
        //    var html = await File.ReadAllTextAsync(htmlPath);
        //    var id = "306278488042";
        //    var url = _urlBuilder.BuildListingUrl(id);

        //    var descriptionHtml = "<div class=\"x-item-description-child\">summy description text</div>";

        //    // stub the description fetch (matches any URL)
        //    Stub_ReturnsAsync(descriptionHtml);
        //    Stub_ReturnsAsync(html, url);

        //    var result = await _serviceUnderTest.GetItemFromListing(id);

        //    ListingAssertions.AssertValidActiveListing(result, id);
        //}

        //[Test]
        //public async Task Should_get_sold_listing()
        //{
        //    var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Listings"));
        //    var htmlPath = Path.Combine(dataDir, "SoldBuyNowListing.htm");
        //    var html = await File.ReadAllTextAsync(htmlPath);
        //    var id = "256918168190";
        //    var url = _urlBuilder.BuildListingUrl(id);

        //    var descriptionHtml = "<div class=\"x-item-description-child\">summy description text</div>";

        //    Stub_ReturnsAsync(descriptionHtml);
        //    Stub_ReturnsAsync(html, url);

        //    var result = await _serviceUnderTest.GetItemFromListing(id);

        //    ListingAssertions.AssertValidSoldListing(result, id);
        //}

        //[Test]
        //public async Task Should_return_results_in_date_range()
        //{
        //    var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Search"));
        //    var htmlPath = Path.Combine(dataDir, "Sold_With_Small_Number_of_Real_Results.htm");
        //    var html = await File.ReadAllTextAsync(htmlPath);

        //    var query = "";
        //    var url = _urlBuilder.BuildSearchUrl(query, true, 1, Condition.USED, BuyingFormat.BUY_NOW);
        //    Stub_ReturnsAsync(html);
        //    var start = new DateTime(2025, 5, 5).AddDays(-7);
        //    var end = new DateTime(2025, 5, 5);

        //    var filter = new SearchFilter(new SearchDateRange(start, end), BuyingFormat.BUY_NOW, Condition.USED);

        //    var result = await _serviceUnderTest.SearchListings(query, filter);

        //    Assert.Multiple(() =>
        //    {
        //        Assert.That(
        //            result.Select(x => x.EndDateUtc),
        //            Is.All.GreaterThanOrEqualTo(start),
        //            $"❌ Every EndDateUtc should be on or after {start:O}");

        //        Assert.That(
        //            result.Select(x => x.EndDateUtc),
        //            Is.All.LessThanOrEqualTo(end),
        //            $"❌ Every EndDateUtc should be on or before {end:O}");
        //    });
        //}

        //private void Stub_ReturnsAsync(string returnHtml, string? url = null)
        //{
        //    if (url is null)
        //    {
        //        _mockFetcher
        //            .Setup(x => x.GetStringAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
        //            .ReturnsAsync(returnHtml);
        //    }
        //    else
        //    {
        //        _mockFetcher
        //            .Setup(x => x.GetStringAsync(url, It.IsAny<CancellationToken>()))
        //            .ReturnsAsync(returnHtml);
        //    }
        //}

        private async Task<IDocument> LoadDocumentAsync(string html)
        {
            return await BrowsingContext.New(Configuration.Default)
                         .OpenAsync(req => req.Content(html))
                         .ConfigureAwait(false);
        }
    }
}