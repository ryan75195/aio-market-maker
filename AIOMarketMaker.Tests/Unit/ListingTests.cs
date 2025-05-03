using AIOMarketMaker.Api.Parsers;
using AIOMarketMaker.Services;
using AngleSharp.Attributes;
using CsvHelper;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;

namespace AIOMarketMaker.Tests.Unit
{


    public class ListingUnitTests
    {
        private ServiceProvider _provider = null!;
        private IEbayScraper _serviceUnderTest = null!;
        public string NormalizeWhitespace(string s) =>
Regex.Replace(s, @"\s+", " ").Trim();

        [SetUp]
        public async Task SetupAsync()
        {
            var testProductHtml = await File.ReadAllTextAsync("../../../Data/ProductListing.html");
            var testProductDescription = await File.ReadAllTextAsync("../../../Data/ProductListing_files/306278488042_002.htm");

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
            services.AddSingleton<ISearchParser, EbaySearchParser>();
            services.AddSingleton<IListingParser, EbayListingParser>();
            services.AddSingleton<IEbayScraper, EbayScraper>();

            var mockFetcher = new Mock<IHtmlFetcher>();
            mockFetcher
                .Setup(x => x.GetStringAsync("https://www.ebay.co.uk/itm/dummy_id", It.IsAny<CancellationToken>()))
                .ReturnsAsync(testProductHtml);
            mockFetcher
                .Setup(x => x.GetStringAsync("ProductListing_files/306278488042_002.htm", It.IsAny<CancellationToken>()))
                .ReturnsAsync(testProductDescription);

            services.AddSingleton<IHtmlFetcher>(mockFetcher.Object);

            _provider = services.BuildServiceProvider();
            _serviceUnderTest = _provider.GetRequiredService<IEbayScraper>();
        }

        [TearDown]
        public void TearDown()
        {
            _provider.Dispose();
        }

        [Test]
        public async Task Should_parse_product_id()
        {
            var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");
            Assert.AreEqual("306278488042", results.id);
        }

        [Test]
        public async Task Should_parse_product_priceAsync()
        {
            var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");
            Assert.AreEqual(659.51, results.price);
        }

        [Test]
        public async Task Should_parse_product_currencyAsync()
        {
            var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");
            Assert.AreEqual("£", results.currency);
        }

        [Test]
        public async Task Should_parse_product_shipping_costAsync()
        {
            var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");
            Assert.AreEqual(12.35, results.shippingCost);
        }

        [Test]
        public async Task Should_parse_product_conditionAsync()
        {
            var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");
            Assert.AreEqual(Condition.OPENED_NEVER_USED, results.Condition);
        }

        [Test]
        public async Task Should_parse_product_images()
        {
            var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");

            var expectedImages = new[]
            {
                "ProductListing_files/s-l140_004.jpg",
                "ProductListing_files/s-l140_005.jpg",
                "ProductListing_files/s-l140_006.jpg",
                "ProductListing_files/s-l140.jpg",
                "ProductListing_files/s-l140_007.jpg",
                "ProductListing_files/s-l140_003.jpg",
                "ProductListing_files/s-l140_002.jpg"
            };

            CollectionAssert.AreEquivalent(expectedImages, results.images);
        }

        [Test]
        public async Task Should_parse_product_item_specifics()
        {
            var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");
            Console.Write(results.ItemSpecifics);
            Assert.That(results.ItemSpecifics.Contains("ConditionOpened – never used: An item in excellent, new condition with no wear. The item may be missing the"));
        }

        [Test]
        public async Task Should_parse_product_descriptionAsync()
        {
            var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");
            Assert.That(results.Description.Contains("be advised that the PlayStation 5 Pro has been opened and used but has"));
        }

        [Test]
        public async Task Should_parse_product_urlAsync()
        {
            var results = await this._serviceUnderTest.GetItemFromListing("dummy_id");
            // verify this is correct in live
            Assert.AreEqual("http://localhost/", results.url);
        }
    }
}