using AIOMarketMaker.Api.Parsers;
using AIOMarketMaker.Models.Ebay;
using AIOMarketMaker.Services;
using AngleSharp;
using AngleSharp.Attributes;
using AngleSharp.Dom;
using CsvHelper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Moq;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;
using static AIOMarketMaker.Tests.Integration.SearchIntegrationTests;

namespace AIOMarketMaker.Tests.Unit
{


    public class ListingUnitTests
    {
        private ServiceProvider _provider = null!;
        private IListingParser _serviceUnderTest = null!;
        private Mock<IHtmlFetcher> _mockFetcher;

        public string NormalizeWhitespace(string s) => Regex.Replace(s, @"\s+", " ").Trim();

        [SetUp]
        public async Task SetupAsync()
        {
    
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
            services.AddSingleton<ISearchParser, EbaySearchParser>();
            services.AddSingleton<EbayListingParser>();
            services.AddSingleton<IEbayScraper, EbayScraper>();

            _mockFetcher = new Mock<IHtmlFetcher>();
            services.AddSingleton<IHtmlFetcher>(_mockFetcher.Object);

            _provider = services.BuildServiceProvider();
            _serviceUnderTest = _provider.GetRequiredService<EbayListingParser>();
        }

        [TearDown]
        public async Task TearDownAsync()
        {
            try
            {
                await _provider.DisposeAsync();
            }
            catch (PlaywrightException ex)
            {
                Console.WriteLine($"Playwright exception during teardown: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected teardown exception: {ex.Message}");
            }
        }

        [Test, TestCaseSource(nameof(ParseIdTestCases))]
        public async Task Should_parse_product_id(string testCaseName, string expectedResponse)
        {
            var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Listings"));

            var htmlPath = Path.Combine(dataDir, testCaseName + ".htm");
            var html = await File.ReadAllTextAsync(htmlPath);
            var doc = await LoadDocumentAsync(html);

            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetProductId(doc!);

            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseIdTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", "156939516763");
            yield return new TestCaseData("ActiveBuyItNowListing", "306278488042");
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", "286542640730");
            yield return new TestCaseData("BiddingEndedNoSale", "365574691438");
            yield return new TestCaseData("SoldBidListing", "256908584476");
            yield return new TestCaseData("SoldBuyNowListing", "256918168190");
        }


        //[Test]
        //public async Task Should_parse_listing_status()
        //{
        //    var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");
        //    Assert.AreEqual(EbayListingStatus.Active, results.ListingStatus);
        //}

        //[Test]
        //public async Task Should_parse_product_priceAsync()
        //{
        //    var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");
        //    Assert.AreEqual(659.51, results.Price);
        //}

        //[Test]
        //public async Task Should_parse_product_currencyAsync()
        //{
        //    var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");
        //    Assert.AreEqual("£", results.Currency);
        //}

        //[Test]
        //public async Task Should_parse_product_shipping_costAsync()
        //{
        //    var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");
        //    Assert.AreEqual(12.35, results.ShippingCost);
        //}

        //[Test]
        //public async Task Should_parse_product_conditionAsync()
        //{
        //    var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");
        //    Assert.AreEqual(Condition.OPENED_NEVER_USED, results.Condition);
        //}

        //[Test]
        //public async Task Should_parse_product_images()
        //{
        //    var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");

        //    var expectedImages = new[]
        //    {
        //        "ActiveBuyItNowListing_files/s-l140_004.jpg",
        //        "ActiveBuyItNowListing_files/s-l140_005.jpg",
        //        "ActiveBuyItNowListing_files/s-l140_006.jpg",
        //        "ActiveBuyItNowListing_files/s-l140.jpg",
        //        "ActiveBuyItNowListing_files/s-l140_007.jpg",
        //        "ActiveBuyItNowListing_files/s-l140_003.jpg",
        //        "ActiveBuyItNowListing_files/s-l140_002.jpg"
        //    };

        //    CollectionAssert.AreEquivalent(expectedImages, results.Images);
        //}

        //[Test]
        //public async Task Should_parse_product_item_specifics()
        //{
        //    var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");
        //    Console.Write(results.ItemSpecifics);
        //    Assert.That(results.ItemSpecifics.Contains("ConditionOpened – never used: An item in excellent, new condition with no wear. The item may be missing the"));
        //}

        //[Test]
        //public async Task Should_parse_product_descriptionAsync()
        //{
        //    // 1) Compute the test-data directory at discovery & run time
        //    var dataDir = Path.GetFullPath(
        //        Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Listings"));

        //    // 2) Build your two paths
        //    var htmlPath = Path.Combine(dataDir, testCaseName + ".htm");
        //    var descPath = Directory
        //        .EnumerateFiles(Path.Combine(dataDir, testCaseName + "_files"), "*_002.htm", SearchOption.TopDirectoryOnly)
        //        .FirstOrDefault()
        //        ?? throw new FileNotFoundException($"No *_002.htm in {testCaseName}_files");

        //    // 3) Read & stub
        //    var html = await File.ReadAllTextAsync(htmlPath);
        //    var desc = await File.ReadAllTextAsync(descPath);

        //    // description URL is the *relative* path your code expects:
        //    var descUrl = descPath.Split("Listings\\")[1];
        //    var doc = await LoadDocumentAsync(html);

        //    // 4) Exercise & assert
        //    var parser = (EbayListingParser)_serviceUnderTest;
        //    var result = parser.GetProductId(doc!);

        //    Assert.AreEqual(
        //        Path.GetFileNameWithoutExtension(descPath).Split('_')[0],
        //        result
        //    );
        //}

        //[Test]
        //public async Task Should_parse_product_urlAsync()
        //{
        //    var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");
        //    // verify this is correct in live
        //    Assert.AreEqual("http://localhost/", results.Url);
        //}

        private async Task<IDocument> LoadDocumentAsync(string html)
        {
            return await BrowsingContext.New(Configuration.Default)
                         .OpenAsync(req => req.Content(html))
                         .ConfigureAwait(false);
        }
    }
}