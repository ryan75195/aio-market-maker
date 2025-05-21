//using AIOMarketMaker.Api.Parsers;
//using AIOMarketMaker.Models.Ebay;
//using AIOMarketMaker.Services;
//using AIOMarketMaker.Tests.Utils;
//using AngleSharp;
//using AngleSharp.Dom;
//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Playwright;
//using Moq;
//using System.Reflection;
//using System.Text.RegularExpressions;

//namespace AIOMarketMaker.Tests.Unit
//{
//    public class EbayScraperUnitTests
//    {
//        private ServiceProvider _provider = null!;
//        private IEbayScraper _serviceUnderTest = null!;
//        private IEbayUrlBuilder _urlBuilder;
//        private Mock<IHtmlFetcher> _mockFetcher;

//        public string NormalizeWhitespace(string s) => Regex.Replace(s, @"\s+", " ").Trim();

//        [SetUp]
//        public async Task SetupAsync()
//        {
//            var services = new ServiceCollection();
//            services.AddLogging();
//            services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
//            services.AddSingleton<ISearchParser, EbaySearchParser>();
//            services.AddSingleton<IListingParser, EbayListingParser>();
//            services.AddSingleton<IEbayScraper, EbayScraper>();
//            services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();

//            _mockFetcher = new Mock<IHtmlFetcher>();
//            services.AddSingleton<IHtmlFetcher>(_mockFetcher.Object);

//            _provider = services.BuildServiceProvider();
//            _serviceUnderTest = _provider.GetRequiredService<IEbayScraper>();
//            _urlBuilder = _provider.GetRequiredService<IEbayUrlBuilder>();

//        }

//        [TearDown]
//        public async Task TearDownAsync()
//        {
//            try
//            {
//                await _provider.DisposeAsync();
//            }
//            catch (PlaywrightException ex)
//            {
//                Console.WriteLine($"Playwright exception during teardown: {ex.Message}");
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"Unexpected teardown exception: {ex.Message}");
//            }
//        }

//        [Test]
//        public async Task Should_get_active_listing()
//        {
//            var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Listings"));
//            var htmlPath = Path.Combine(dataDir, "ActiveBuyItNowListing.htm");
//            var html = await File.ReadAllTextAsync(htmlPath);
//            var id = "306278488042";
//            var url = _urlBuilder.BuildListingUrl(id);

//            var descriptionHtml = "<div class=\"x-item-description-child\">summy description text</div>";

//            // stub the description fetch (matches any URL)
//            Stub_ReturnsAsync(descriptionHtml);
//            Stub_ReturnsAsync(html, url);

//            var result = await _serviceUnderTest.GetItemFromListing(id);

//            ListingAssertions.AssertValidActiveListing(result, id);
//        }

//        [Test]
//        public async Task Should_get_sold_listing()
//        {
//            var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Listings"));
//            var htmlPath = Path.Combine(dataDir, "SoldBuyNowListing.htm");
//            var html = await File.ReadAllTextAsync(htmlPath);
//            var id = "256918168190";
//            var url = _urlBuilder.BuildListingUrl(id);

//            var descriptionHtml = "<div class=\"x-item-description-child\">summy description text</div>";

//            Stub_ReturnsAsync(descriptionHtml); 
//            Stub_ReturnsAsync(html, url);

//            var result = await _serviceUnderTest.GetItemFromListing(id);

//            ListingAssertions.AssertValidSoldListing(result, id);
//        }

//        [Test]
//        public async Task Should_return_results_in_date_range()
//        {
//            var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Search"));
//            var htmlPath = Path.Combine(dataDir, "Sold_With_Small_Number_of_Real_Results.htm");
//            var html = await File.ReadAllTextAsync(htmlPath);

//            var query = "";
//            var url = _urlBuilder.BuildSearchUrl(query, true, 1, Condition.USED, BuyingFormat.BUY_NOW);
//            Stub_ReturnsAsync(html);
//            var start = new DateTime(2025, 5, 5).AddDays(-7);
//            var end = new DateTime(2025, 5, 5);

//            var filter = new SearchFilter(new SearchDateRange(start, end), BuyingFormat.BUY_NOW, Condition.USED);

//            var result = await _serviceUnderTest.SearchListings(query, filter);

//            Assert.Multiple(() =>
//            {
//                Assert.That(
//                    result.Select(x => x.EndDateUtc),
//                    Is.All.GreaterThanOrEqualTo(start),
//                    $"❌ Every EndDateUtc should be on or after {start:O}");

//                Assert.That(
//                    result.Select(x => x.EndDateUtc),
//                    Is.All.LessThanOrEqualTo(end),
//                    $"❌ Every EndDateUtc should be on or before {end:O}");
//            });
//        }

//        private void Stub_ReturnsAsync(string returnHtml, string? url = null)
//        {
//            if (url is null)
//            {
//                _mockFetcher
//                    .Setup(x => x.GetStringAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
//                    .ReturnsAsync(returnHtml);
//            }
//            else
//            {
//                _mockFetcher
//                    .Setup(x => x.GetStringAsync(url, It.IsAny<CancellationToken>()))
//                    .ReturnsAsync(returnHtml);
//            }
//        }

//        private async Task<IDocument> LoadDocumentAsync(string html)
//        {
//            return await BrowsingContext.New(Configuration.Default)
//                         .OpenAsync(req => req.Content(html))
//                         .ConfigureAwait(false);
//        }
//    }
//}