using AIOMarketMaker.Api.Parsers;
using AIOMarketMaker.Models.Ebay;
using AIOMarketMaker.Services;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Moq;
using System.Text.RegularExpressions;

namespace AIOMarketMaker.Tests.Unit
{
    public class ListingParserUnitTests
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
            var doc = await LoadTestHtmlDocumentAsync(testCaseName);
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

        [Test, TestCaseSource(nameof(ParseTitleTestCases))]
        public async Task Should_parse_product_title(string testCaseName, string expectedResponse)
        {
            var doc = await LoadTestHtmlDocumentAsync(testCaseName);
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetProductTitle(doc!);

            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseTitleTestCases()
        {
            yield return new TestCaseData("ActiveAuctionListing", "Sony PS5 Blu-Ray Edition Console - White");
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", "SONY PlayStation 5 Slim Digital Edition 1TB White Game Console - PS5");
            yield return new TestCaseData("ActiveBuyItNowListing", "PlayStation 5 Pro, 2TB,With Disc Drive, Controller & Charging Doc.");
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", "Sony PlayStation 5 Digital Edition 825GB-WHITE Console With Headset");
            yield return new TestCaseData("BiddingEndedNoSale", "PlayStation 5 Digital Edition Slim");
            yield return new TestCaseData("SoldBidListing", "Sony PlayStation 5 Disc Edition Console 825GB Boxed with 2 Games ");
            yield return new TestCaseData("SoldBuyNowListing", "PlayStation 5 Slim Digital Edition Console");
        }


        [Test, TestCaseSource(nameof(ParseStatusTestCases))]
        public async Task Should_parse_listing_status(string testCaseName, EbayListingStatus expectedResponse)
        {
            var doc = await LoadTestHtmlDocumentAsync(testCaseName);
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetListingStatus(doc!);

            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseStatusTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", EbayListingStatus.Active);
            yield return new TestCaseData("ActiveBuyItNowListing", EbayListingStatus.Active);
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", EbayListingStatus.Active);
            yield return new TestCaseData("BiddingEndedNoSale", EbayListingStatus.Ended);
            yield return new TestCaseData("SoldBidListing", EbayListingStatus.Sold);
            yield return new TestCaseData("SoldBuyNowListing", EbayListingStatus.Sold);
        }

        [Test, TestCaseSource(nameof(ParsePriceTestCases))]
        public async Task Should_parse_product_price(string testCaseName, double expectedResponse)
        {
            var doc = await LoadTestHtmlDocumentAsync(testCaseName);
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetProductPrice(doc!);

            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParsePriceTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", 363.72d);
            yield return new TestCaseData("ActiveBuyItNowListing", 659.51d);
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", 247.01d);
            yield return new TestCaseData("BiddingEndedNoSale", 328.02d);
            yield return new TestCaseData("SoldBidListing", 316.34d);
            yield return new TestCaseData("SoldBuyNowListing", 265.92d);
        }

        [Test, TestCaseSource(nameof(PurchaseFormatTestCases))]
        public async Task Should_parse_purchase_format(string testCaseName, PurchaseFormat expectedResponse)
        {
            var doc = await LoadTestHtmlDocumentAsync(testCaseName);
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetPurchaseFormat(doc!);

            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> PurchaseFormatTestCases()
        {
            yield return new TestCaseData("ActiveAuctionListing", PurchaseFormat.Auction);
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", PurchaseFormat.AuctionWithBestOffer);
            yield return new TestCaseData("ActiveBuyItNowListing", PurchaseFormat.BuyItNow);
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", PurchaseFormat.BuyItNowWithBestOffer);
        }

        [Test, TestCaseSource(nameof(ParseEndDateTestCases))]
        public async Task Should_parse_end_date(string testCaseName, DateTime? expectedResponse)
        {
            var doc = await LoadTestHtmlDocumentAsync(testCaseName);
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetEndDate(doc!);

            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseEndDateTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", null);
            yield return new TestCaseData("ActiveBuyItNowListing", null);
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", null);
            yield return new TestCaseData("BiddingEndedNoSale", new DateTime(2025, 5, 4, 11, 8, 0));
            yield return new TestCaseData("SoldBidListing", new DateTime(2025, 5, 4, 11, 3, 0));
            yield return new TestCaseData("SoldBuyNowListing", new DateTime(2025, 5, 4, 10, 21, 0));
        }

        [Test, TestCaseSource(nameof(ParseCurrencyTestCases))]
        public async Task Should_parse_product_currency(string testCaseName, string expectedResponse)
        {
            var doc = await LoadTestHtmlDocumentAsync(testCaseName);
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetCurrency(doc!);

            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseCurrencyTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", "GBP");
            yield return new TestCaseData("ActiveBuyItNowListing", "GBP");
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", "GBP");
            yield return new TestCaseData("BiddingEndedNoSale", "GBP");
            yield return new TestCaseData("SoldBidListing", "GBP");
            yield return new TestCaseData("SoldBuyNowListing", "GBP");
        }


        [Test, TestCaseSource(nameof(ParseLocationTestCases))]
        public async Task Should_parse_product_location(string testCaseName, string expectedResponse)
        {
            var doc = await LoadTestHtmlDocumentAsync(testCaseName);
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetLocation(doc!);

            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseLocationTestCases()
        {
            yield return new TestCaseData("ActiveAuctionListing", "Sheffield, United Kingdom");
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", "Worthing, United Kingdom");
            yield return new TestCaseData("ActiveBuyItNowListing", "Leek, United Kingdom");
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", "Skelmersdale, United Kingdom");
            yield return new TestCaseData("BiddingEndedNoSale", "Croydon, United Kingdom");
            yield return new TestCaseData("SoldBidListing", "London, United Kingdom");
            yield return new TestCaseData("SoldBuyNowListing", "Preston, United Kingdom");
        }

        [Test, TestCaseSource(nameof(ParseShippingCostTestCases))]
        public async Task Should_parse_product_shipping_cost(string testCaseName, decimal expectedResponse)
        {
            var doc = await LoadTestHtmlDocumentAsync(testCaseName);
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetShippingPrice(doc!);

            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseShippingCostTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", 8.41m);
            yield return new TestCaseData("ActiveBuyItNowListing", 12.35m);
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", 8.41m);
            yield return new TestCaseData("BiddingEndedNoSale", 0m);
            yield return new TestCaseData("SoldBidListing", 8.41m);
            yield return new TestCaseData("SoldBuyNowListing", 0m);
        }

        [Test, TestCaseSource(nameof(ParseConditionTestCases))]
        public async Task Should_parse_product_condition(string testCaseName, Condition expectedResponse)
        {
            var doc = await LoadTestHtmlDocumentAsync(testCaseName);
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetProductCondition(doc!);

            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseConditionTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", Condition.NEW);
            yield return new TestCaseData("ActiveBuyItNowListing", Condition.USED);
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", Condition.USED);
            yield return new TestCaseData("BiddingEndedNoSale", Condition.NEW);
            yield return new TestCaseData("SoldBidListing", Condition.USED);
            yield return new TestCaseData("SoldBuyNowListing", Condition.USED);
        }


        [Test, TestCaseSource(nameof(ParseSpecificsTestCases))]
        public async Task Should_parse_product_item_specifics(string testCaseName, string expectedResponse)
        {
            var doc = await LoadTestHtmlDocumentAsync(testCaseName);
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetItemSpecifics(doc!);

            Assert.That(result.Contains(expectedResponse));
        }

        private static IEnumerable<object> ParseSpecificsTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", "ConditionNew: A brand-new, unused, unopened and undamaged item in original retail packaging");
            yield return new TestCaseData("ActiveBuyItNowListing", "ConditionOpened – never used: An item in excellent, new condition with no wear. The item may be missing the");
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", "ConditionUsed: An item that has been previously used. The item may have some signs of cosmetic wear, but is");
            yield return new TestCaseData("BiddingEndedNoSale", "ConditionNew: A brand-new, unused, unopened and undamaged item in original retail packaging");
            yield return new TestCaseData("SoldBidListing", "ConditionUsed: An item that has been previously used. The item may have some signs of cosmetic wear, but is");
            yield return new TestCaseData("SoldBuyNowListing", "ConditionUsed: An item that has been previously used. The item may have some signs of cosmetic wear, but is");
        }


        [Test, TestCaseSource(nameof(ParseImagesTestCases))]
        public async Task Should_parse_product_images(string testCaseName, string[] expectedResponse)
        {
            var doc = await LoadTestHtmlDocumentAsync(testCaseName);
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetProductImages(doc!);

            CollectionAssert.AreEquivalent(expectedResponse, result);
        }

        private static IEnumerable<object> ParseImagesTestCases()
        {
            yield return new TestCaseData(
                "ActiveAuctionWithOfferAvailable",
                new[]
                {
                    "ActiveAuctionWithOfferAvailable_files/s-l140_003.jpg",
                    "ActiveAuctionWithOfferAvailable_files/s-l140_004.jpg",
                    "ActiveAuctionWithOfferAvailable_files/s-l140.jpg",
                    "ActiveAuctionWithOfferAvailable_files/s-l140_002.jpg"
                }
            );

            yield return new TestCaseData(
                "ActiveBuyItNowListing",
                new[]
                {
                                "ActiveBuyItNowListing_files/s-l140_004.jpg",
                                "ActiveBuyItNowListing_files/s-l140_005.jpg",
                                "ActiveBuyItNowListing_files/s-l140_006.jpg",
                                "ActiveBuyItNowListing_files/s-l140.jpg",
                                "ActiveBuyItNowListing_files/s-l140_007.jpg",
                                "ActiveBuyItNowListing_files/s-l140_003.jpg",
                                "ActiveBuyItNowListing_files/s-l140_002.jpg"
                }
            );

            yield return new TestCaseData(
                "ActiveBuyNowListingWithOffer",
                new[]
                {
                    "ActiveBuyNowListingWithOffer_files/s-l140_004.jpg",
                    "ActiveBuyNowListingWithOffer_files/s-l140.jpg",
                    "ActiveBuyNowListingWithOffer_files/s-l140_005.jpg",
                    "ActiveBuyNowListingWithOffer_files/s-l140_002.jpg",
                    "ActiveBuyNowListingWithOffer_files/s-l140_006.jpg",
                    "ActiveBuyNowListingWithOffer_files/s-l140_003.jpg"
                }
            );

            yield return new TestCaseData(
                "BiddingEndedNoSale",
                new[]
                {
                    "BiddingEndedNoSale_files/s-l140.jpg",
                    "BiddingEndedNoSale_files/s-l140_005.jpg",
                    "BiddingEndedNoSale_files/s-l140_003.jpg",
                    "BiddingEndedNoSale_files/s-l140_004.jpg",
                    "BiddingEndedNoSale_files/s-l140_002.jpg"
                }
            );


            yield return new TestCaseData(
                "SoldBidListing",
                new[]
                {
                    "SoldBidListing_files/s-l140.jpg",
                    "SoldBidListing_files/s-l140_002.jpg",
                    "SoldBidListing_files/s-l140_003.jpg"
                }
            );


            yield return new TestCaseData(
                "SoldBuyNowListing",
                new[]
                {
                    "SoldBuyNowListing_files/s-l140.jpg",
                    "SoldBuyNowListing_files/s-l140_007.jpg",
                    "SoldBuyNowListing_files/s-l140_004.jpg",
                    "SoldBuyNowListing_files/s-l140_005.jpg",
                    "SoldBuyNowListing_files/s-l140_006.jpg",
                    "SoldBuyNowListing_files/s-l140_003.jpg",
                    "SoldBuyNowListing_files/s-l140_002.jpg"
                }
            );
        }

        [Test, TestCaseSource(nameof(ParseDescriptionTestCases))]
        public async Task Should_parse_product_description(string testCaseName, string expectedResponse)
        {

            var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Listings"));
            var descPath = Directory
                .EnumerateFiles(Path.Combine(dataDir, testCaseName + "_files"), "*_002.htm", SearchOption.TopDirectoryOnly)
                .FirstOrDefault()
                ?? throw new FileNotFoundException($"No *_002.htm in {testCaseName}_files");

            var html = await File.ReadAllTextAsync(descPath);
            var doc = await LoadDocumentAsync(html);

            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.ParseDescription(doc!);

            Assert.That(result.Contains(expectedResponse));
        }

        private static IEnumerable<object> ParseDescriptionTestCases()
        {
            //yield return new TestCaseData("ActiveAuctionWithOfferAvailable", "Bought direct from PlayStation but never opened or used.");
            yield return new TestCaseData("ActiveBuyItNowListing", "be advised that the PlayStation 5 Pro has been opened and used but has");
            //yield return new TestCaseData("ActiveBuyNowListingWithOffer", "Console comes with 1 controller, power cable and headset. All fully working");
            yield return new TestCaseData("BiddingEndedNoSale", "The new PS5 is slimmed down, but it's still just as fast. It's 24% lighter and 30% smaller than the original");
            yield return new TestCaseData("SoldBidListing", "Mint condition Sony PlayStation 5 BluRay disc edition, 825GB. Full original boxing.Comes with Spiderman Miles Morales");
            yield return new TestCaseData("SoldBuyNowListing", "Excellent condition, fully working. Items included:- Box- Controller- Power cable - HDMI cable  Feel free to ask any questions");
        }

        [Test]
        public async Task Should_return_end_date_as_utc()
        {
            var doc = await LoadTestHtmlDocumentAsync("SoldBidListing");
            var item = doc.QuerySelector("li.s-item[id]:not([id=\"\"])");

            var parser = (EbayListingParser)_serviceUnderTest;
            var endDate = parser.GetEndDate(doc);

            Assert.That(endDate.HasValue, Is.True, "❌ Expected a non-null end date");
            Assert.That(endDate.Value.Kind, Is.EqualTo(DateTimeKind.Utc),
                $"❌ Expected DateTimeKind.Utc but got {endDate.Value.Kind}");
        }

        private async Task<IDocument> LoadTestHtmlDocumentAsync(string testCaseName)
        {
            var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Listings"));
            var htmlPath = Path.Combine(dataDir, testCaseName + ".htm");
            var html = await File.ReadAllTextAsync(htmlPath);
            return await LoadDocumentAsync(html);
        }

        private async Task<IDocument> LoadDocumentAsync(string html)
        {
            return await BrowsingContext.New(Configuration.Default)
                         .OpenAsync(req => req.Content(html))
                         .ConfigureAwait(false);
        }
    }
}