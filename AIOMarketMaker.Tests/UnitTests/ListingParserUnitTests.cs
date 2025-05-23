using AIOMarketMaker.Api.Parsers;
using AIOMarketMaker.Models.Ebay;
using AIOMarketMaker.Services;
using AIOMarketMaker.Tests.Utils;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Text.RegularExpressions;

namespace AIOMarketMaker.Tests.Unit
{
    public class ListingParserUnitTests
    {
        private ServiceProvider _provider = null!;
        private IListingParser _serviceUnderTest = null!;

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
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected teardown exception: {ex.Message}");
            }
        }

        [Test, TestCaseSource(nameof(ParseIdTestCases))]
        public async Task Should_parse_product_id(string testCaseName, string expectedResponse, IDocument testDoc)
        {
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetProductId(testDoc!);
            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseIdTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", "156939516763", PageBuilder.LoadTestHtmlDocument("ActiveAuctionWithOfferAvailable"));
            yield return new TestCaseData("ActiveBuyItNowListing", "306278488042", PageBuilder.LoadTestHtmlDocument("ActiveBuyItNowListing"));
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", "286542640730", PageBuilder.LoadTestHtmlDocument("ActiveBuyNowListingWithOffer"));
            yield return new TestCaseData("BiddingEndedNoSale", "365574691438", PageBuilder.LoadTestHtmlDocument("BiddingEndedNoSale"));
            yield return new TestCaseData("SoldBidListing", "256908584476", PageBuilder.LoadTestHtmlDocument("SoldBidListing"));
            yield return new TestCaseData("SoldBuyNowListing", "256918168190", PageBuilder.LoadTestHtmlDocument("SoldBuyNowListing"));
            yield return new TestCaseData("ReturnsNullIfParsingFails", null, PageBuilder.BuildEmptyDocument());
        }

        [Test, TestCaseSource(nameof(ParseTitleTestCases))]
        public async Task Should_parse_product_title(string testCaseName, string expectedResponse, IDocument testDoc)
        {
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetProductTitle(testDoc!);
            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseTitleTestCases()
        {
            yield return new TestCaseData("ActiveAuctionListing", "Sony PS5 Blu-Ray Edition Console - White", PageBuilder.LoadTestHtmlDocument("ActiveAuctionListing"));
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", "SONY PlayStation 5 Slim Digital Edition 1TB White Game Console - PS5", PageBuilder.LoadTestHtmlDocument("ActiveAuctionWithOfferAvailable"));
            yield return new TestCaseData("ActiveBuyItNowListing", "PlayStation 5 Pro, 2TB,With Disc Drive, Controller & Charging Doc.", PageBuilder.LoadTestHtmlDocument("ActiveBuyItNowListing"));
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", "Sony PlayStation 5 Digital Edition 825GB-WHITE Console With Headset", PageBuilder.LoadTestHtmlDocument("ActiveBuyNowListingWithOffer"));
            yield return new TestCaseData("BiddingEndedNoSale", "PlayStation 5 Digital Edition Slim", PageBuilder.LoadTestHtmlDocument("BiddingEndedNoSale"));
            yield return new TestCaseData("SoldBidListing", "Sony PlayStation 5 Disc Edition Console 825GB Boxed with 2 Games ", PageBuilder.LoadTestHtmlDocument("SoldBidListing"));
            yield return new TestCaseData("SoldBuyNowListing", "PlayStation 5 Slim Digital Edition Console", PageBuilder.LoadTestHtmlDocument("SoldBuyNowListing"));
            yield return new TestCaseData("MissingTitle", null, PageBuilder.BuildEmptyDocument());
        }

        [Test, TestCaseSource(nameof(ParseStatusTestCases))]
        public async Task Should_parse_listing_status(string testCaseName, EbayListingStatus expectedResponse, IDocument testDoc)
        {
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetListingStatus(testDoc!);
            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseStatusTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", EbayListingStatus.Active, PageBuilder.LoadTestHtmlDocument("ActiveAuctionWithOfferAvailable"));
            yield return new TestCaseData("ActiveBuyItNowListing", EbayListingStatus.Active, PageBuilder.LoadTestHtmlDocument("ActiveBuyItNowListing"));
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", EbayListingStatus.Active, PageBuilder.LoadTestHtmlDocument("ActiveBuyNowListingWithOffer"));
            yield return new TestCaseData("BiddingEndedNoSale", EbayListingStatus.Ended, PageBuilder.LoadTestHtmlDocument("BiddingEndedNoSale"));
            yield return new TestCaseData("SoldBidListing", EbayListingStatus.Sold, PageBuilder.LoadTestHtmlDocument("SoldBidListing"));
            yield return new TestCaseData("SoldBuyNowListing", EbayListingStatus.Sold, PageBuilder.LoadTestHtmlDocument("SoldBuyNowListing"));
            yield return new TestCaseData("MissingStatus", EbayListingStatus.Active, PageBuilder.BuildEmptyDocument());
        }

        [Test, TestCaseSource(nameof(ParsePriceTestCases))]
        public async Task Should_parse_product_price(string testCaseName, double? expectedResponse, IDocument testDoc)
        {
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetProductPrice(testDoc!);
            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParsePriceTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", 363.72d, PageBuilder.LoadTestHtmlDocument("ActiveAuctionWithOfferAvailable"));
            yield return new TestCaseData("ActiveBuyItNowListing", 659.51d, PageBuilder.LoadTestHtmlDocument("ActiveBuyItNowListing"));
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", 247.01d, PageBuilder.LoadTestHtmlDocument("ActiveBuyNowListingWithOffer"));
            yield return new TestCaseData("BiddingEndedNoSale", 328.02d, PageBuilder.LoadTestHtmlDocument("BiddingEndedNoSale"));
            yield return new TestCaseData("SoldBidListing", 316.34d, PageBuilder.LoadTestHtmlDocument("SoldBidListing"));
            yield return new TestCaseData("SoldBuyNowListing", 265.92d, PageBuilder.LoadTestHtmlDocument("SoldBuyNowListing"));
            yield return new TestCaseData("MissingPrice", null, PageBuilder.BuildEmptyDocument());
        }

        [Test, TestCaseSource(nameof(PurchaseFormatTestCases))]
        public async Task Should_parse_purchase_format(string testCaseName, PurchaseFormat expectedResponse, IDocument testDoc)
        {
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetPurchaseFormat(testDoc!);
            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> PurchaseFormatTestCases()
        {
            yield return new TestCaseData("ActiveAuctionListing", PurchaseFormat.Auction, PageBuilder.LoadTestHtmlDocument("ActiveAuctionListing"));
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", PurchaseFormat.AuctionWithBestOffer, PageBuilder.LoadTestHtmlDocument("ActiveAuctionWithOfferAvailable"));
            yield return new TestCaseData("ActiveBuyItNowListing", PurchaseFormat.BuyItNow, PageBuilder.LoadTestHtmlDocument("ActiveBuyItNowListing"));
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", PurchaseFormat.BuyItNowWithBestOffer, PageBuilder.LoadTestHtmlDocument("ActiveBuyNowListingWithOffer"));
            yield return new TestCaseData("MissingPurchaseFormat", PurchaseFormat.Unknown, PageBuilder.BuildEmptyDocument());
        }

        [Test, TestCaseSource(nameof(ParseEndDateTestCases))]
        public async Task Should_parse_end_date(string testCaseName, DateTime? expectedResponse, IDocument testDoc)
        {
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetEndDate(testDoc!);
            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseEndDateTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", null, PageBuilder.LoadTestHtmlDocument("ActiveAuctionWithOfferAvailable"));
            yield return new TestCaseData("ActiveBuyItNowListing", null, PageBuilder.LoadTestHtmlDocument("ActiveBuyItNowListing"));
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", null, PageBuilder.LoadTestHtmlDocument("ActiveBuyNowListingWithOffer"));
            yield return new TestCaseData("BiddingEndedNoSale", new DateTime(2025, 5, 4, 11, 8, 0), PageBuilder.LoadTestHtmlDocument("BiddingEndedNoSale"));
            yield return new TestCaseData("SoldBidListing", new DateTime(2025, 5, 4, 11, 3, 0), PageBuilder.LoadTestHtmlDocument("SoldBidListing"));
            yield return new TestCaseData("SoldBuyNowListing", new DateTime(2025, 5, 4, 10, 21, 0), PageBuilder.LoadTestHtmlDocument("SoldBuyNowListing"));
            yield return new TestCaseData("MissingEndDate", null, PageBuilder.BuildEmptyDocument());
        }

        [Test, TestCaseSource(nameof(ParseCurrencyTestCases))]
        public async Task Should_parse_product_currency(string testCaseName, string expectedResponse, IDocument testDoc)
        {
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetCurrency(testDoc!);
            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseCurrencyTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", "GBP", PageBuilder.LoadTestHtmlDocument("ActiveAuctionWithOfferAvailable"));
            yield return new TestCaseData("ActiveBuyItNowListing", "GBP", PageBuilder.LoadTestHtmlDocument("ActiveBuyItNowListing"));
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", "GBP", PageBuilder.LoadTestHtmlDocument("ActiveBuyNowListingWithOffer"));
            yield return new TestCaseData("BiddingEndedNoSale", "GBP", PageBuilder.LoadTestHtmlDocument("BiddingEndedNoSale"));
            yield return new TestCaseData("SoldBidListing", "GBP", PageBuilder.LoadTestHtmlDocument("SoldBidListing"));
            yield return new TestCaseData("SoldBuyNowListing", "GBP", PageBuilder.LoadTestHtmlDocument("SoldBuyNowListing"));
            yield return new TestCaseData("MissingCurrency", null, PageBuilder.BuildEmptyDocument());
        }


        [Test, TestCaseSource(nameof(ParseLocationTestCases))]
        public async Task Should_parse_product_location(string testCaseName, string expectedResponse, IDocument testDoc)
        {
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetLocation(testDoc!);
            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseLocationTestCases()
        {
            yield return new TestCaseData("ActiveAuctionListing", "Sheffield, United Kingdom", PageBuilder.LoadTestHtmlDocument("ActiveAuctionListing"));
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", "Worthing, United Kingdom", PageBuilder.LoadTestHtmlDocument("ActiveAuctionWithOfferAvailable"));
            yield return new TestCaseData("ActiveBuyItNowListing", "Leek, United Kingdom", PageBuilder.LoadTestHtmlDocument("ActiveBuyItNowListing"));
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", "Skelmersdale, United Kingdom", PageBuilder.LoadTestHtmlDocument("ActiveBuyNowListingWithOffer"));
            yield return new TestCaseData("BiddingEndedNoSale", "Croydon, United Kingdom", PageBuilder.LoadTestHtmlDocument("BiddingEndedNoSale"));
            yield return new TestCaseData("SoldBidListing", "London, United Kingdom", PageBuilder.LoadTestHtmlDocument("SoldBidListing"));
            yield return new TestCaseData("SoldBuyNowListing", "Preston, United Kingdom", PageBuilder.LoadTestHtmlDocument("SoldBuyNowListing"));
            yield return new TestCaseData("MissingLocation", null, PageBuilder.BuildEmptyDocument());
            yield return new TestCaseData("Location with no colon", "Some Location", PageBuilder.BuildProductPage(locationText: "Some Location", shippingCost: 0));
        }

        [Test, TestCaseSource(nameof(ParseShippingCostTestCases))]
        public async Task Should_parse_product_shipping_cost(string testCaseName, decimal expectedResponse, IDocument testDoc)
        {
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetShippingPrice(testDoc!);
            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseShippingCostTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", 8.41m, PageBuilder.LoadTestHtmlDocument("ActiveAuctionWithOfferAvailable"));
            yield return new TestCaseData("ActiveBuyItNowListing", 12.35m, PageBuilder.LoadTestHtmlDocument("ActiveBuyItNowListing"));
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", 8.41m, PageBuilder.LoadTestHtmlDocument("ActiveBuyNowListingWithOffer"));
            yield return new TestCaseData("BiddingEndedNoSale", 0m, PageBuilder.LoadTestHtmlDocument("BiddingEndedNoSale"));
            yield return new TestCaseData("SoldBidListing", 8.41m, PageBuilder.LoadTestHtmlDocument("SoldBidListing"));
            yield return new TestCaseData("SoldBuyNowListing", 0m, PageBuilder.LoadTestHtmlDocument("SoldBuyNowListing"));
            yield return new TestCaseData("MissingShippingCost", 0m, PageBuilder.BuildEmptyDocument());
        }

        [Test, TestCaseSource(nameof(ParseConditionTestCases))]
        public async Task Should_parse_product_condition(string testCaseName, Condition expectedResponse, IDocument testDoc)
        {
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetProductCondition(testDoc!);
            Assert.That(result, Is.EqualTo(expectedResponse));
        }

        private static IEnumerable<object> ParseConditionTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", Condition.NEW, PageBuilder.LoadTestHtmlDocument("ActiveAuctionWithOfferAvailable"));
            yield return new TestCaseData("ActiveBuyItNowListing", Condition.USED, PageBuilder.LoadTestHtmlDocument("ActiveBuyItNowListing"));
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", Condition.USED, PageBuilder.LoadTestHtmlDocument("ActiveBuyNowListingWithOffer"));
            yield return new TestCaseData("BiddingEndedNoSale", Condition.NEW, PageBuilder.LoadTestHtmlDocument("BiddingEndedNoSale"));
            yield return new TestCaseData("SoldBidListing", Condition.USED, PageBuilder.LoadTestHtmlDocument("SoldBidListing"));
            yield return new TestCaseData("SoldBuyNowListing", Condition.USED, PageBuilder.LoadTestHtmlDocument("SoldBuyNowListing"));
            yield return new TestCaseData("MissingCondition", Condition.NULL, PageBuilder.BuildEmptyDocument());
        }

        [Test, TestCaseSource(nameof(ParseSpecificsTestCases))]
        public async Task Should_parse_product_item_specifics(string testCaseName, string expectedResponse, IDocument testDoc)
        {
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetItemSpecifics(testDoc!);
            Assert.That(result?.Contains(expectedResponse) ?? expectedResponse == null);
        }

        private static IEnumerable<object> ParseSpecificsTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", "ConditionNew: A brand-new, unused, unopened and undamaged item in original retail packaging", PageBuilder.LoadTestHtmlDocument("ActiveAuctionWithOfferAvailable"));
            yield return new TestCaseData("ActiveBuyItNowListing", "ConditionOpened – never used: An item in excellent, new condition with no wear. The item may be missing the", PageBuilder.LoadTestHtmlDocument("ActiveBuyItNowListing"));
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", "ConditionUsed: An item that has been previously used. The item may have some signs of cosmetic wear, but is", PageBuilder.LoadTestHtmlDocument("ActiveBuyNowListingWithOffer"));
            yield return new TestCaseData("BiddingEndedNoSale", "ConditionNew: A brand-new, unused, unopened and undamaged item in original retail packaging", PageBuilder.LoadTestHtmlDocument("BiddingEndedNoSale"));
            yield return new TestCaseData("SoldBidListing", "ConditionUsed: An item that has been previously used. The item may have some signs of cosmetic wear, but is", PageBuilder.LoadTestHtmlDocument("SoldBidListing"));
            yield return new TestCaseData("SoldBuyNowListing", "ConditionUsed: An item that has been previously used. The item may have some signs of cosmetic wear, but is", PageBuilder.LoadTestHtmlDocument("SoldBuyNowListing"));
            yield return new TestCaseData("MissingSpecifics", null, PageBuilder.BuildEmptyDocument());
        }

        [Test, TestCaseSource(nameof(ParseImagesTestCases))]
        public async Task Should_parse_product_images(string testCaseName, string[] expectedResponse, IDocument testDoc)
        {
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.GetProductImages(testDoc!);
            CollectionAssert.AreEquivalent(expectedResponse, result);
        }

        private static IEnumerable<object> ParseImagesTestCases()
        {
            yield return new TestCaseData("ActiveAuctionWithOfferAvailable", new[] { "ActiveAuctionWithOfferAvailable_files/s-l140_003.jpg", "ActiveAuctionWithOfferAvailable_files/s-l140_004.jpg", "ActiveAuctionWithOfferAvailable_files/s-l140.jpg", "ActiveAuctionWithOfferAvailable_files/s-l140_002.jpg" }, PageBuilder.LoadTestHtmlDocument("ActiveAuctionWithOfferAvailable"));
            yield return new TestCaseData("ActiveBuyItNowListing", new[] { "ActiveBuyItNowListing_files/s-l140_004.jpg", "ActiveBuyItNowListing_files/s-l140_005.jpg", "ActiveBuyItNowListing_files/s-l140_006.jpg", "ActiveBuyItNowListing_files/s-l140.jpg", "ActiveBuyItNowListing_files/s-l140_007.jpg", "ActiveBuyItNowListing_files/s-l140_003.jpg", "ActiveBuyItNowListing_files/s-l140_002.jpg" }, PageBuilder.LoadTestHtmlDocument("ActiveBuyItNowListing"));
            yield return new TestCaseData("ActiveBuyNowListingWithOffer", new[] { "ActiveBuyNowListingWithOffer_files/s-l140_004.jpg", "ActiveBuyNowListingWithOffer_files/s-l140.jpg", "ActiveBuyNowListingWithOffer_files/s-l140_005.jpg", "ActiveBuyNowListingWithOffer_files/s-l140_002.jpg", "ActiveBuyNowListingWithOffer_files/s-l140_006.jpg", "ActiveBuyNowListingWithOffer_files/s-l140_003.jpg" }, PageBuilder.LoadTestHtmlDocument("ActiveBuyNowListingWithOffer"));
            yield return new TestCaseData("BiddingEndedNoSale", new[] { "BiddingEndedNoSale_files/s-l140.jpg", "BiddingEndedNoSale_files/s-l140_005.jpg", "BiddingEndedNoSale_files/s-l140_003.jpg", "BiddingEndedNoSale_files/s-l140_004.jpg", "BiddingEndedNoSale_files/s-l140_002.jpg" }, PageBuilder.LoadTestHtmlDocument("BiddingEndedNoSale"));
            yield return new TestCaseData("SoldBidListing", new[] { "SoldBidListing_files/s-l140.jpg", "SoldBidListing_files/s-l140_002.jpg", "SoldBidListing_files/s-l140_003.jpg" }, PageBuilder.LoadTestHtmlDocument("SoldBidListing"));
            yield return new TestCaseData("SoldBuyNowListing", new[] { "SoldBuyNowListing_files/s-l140.jpg", "SoldBuyNowListing_files/s-l140_007.jpg", "SoldBuyNowListing_files/s-l140_004.jpg", "SoldBuyNowListing_files/s-l140_005.jpg", "SoldBuyNowListing_files/s-l140_006.jpg", "SoldBuyNowListing_files/s-l140_003.jpg", "SoldBuyNowListing_files/s-l140_002.jpg" }, PageBuilder.LoadTestHtmlDocument("SoldBuyNowListing"));
            yield return new TestCaseData("MissingImages", Array.Empty<string>(), PageBuilder.BuildEmptyDocument());
        }

        [Test, TestCaseSource(nameof(ParseDescriptionTestCases))]
        public async Task Should_parse_product_description(string testCaseName, string? expectedResponse, IDocument testDoc)
        {
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.ParseDescription(testDoc!);

            if (expectedResponse is null)
            {
                // for a “missing” case, we expect no text back (empty or null)
                Assert.That(result, Is.Null.Or.Empty,
                    $"Expected no description for {testCaseName}, but got: '{result}'");
            }
            else
            {
                Assert.That(result, Does.Contain(expectedResponse),
                    $"Expected description for {testCaseName} to contain '{expectedResponse}'");
            }
        }

        private static IEnumerable<object> ParseDescriptionTestCases()
        {
            string dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Listings"));

            string loadHtml(string name)
            {
                var descPath = Directory
                    .EnumerateFiles(Path.Combine(dataDir, name + "_files"), "*_002.htm", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault()
                    ?? throw new FileNotFoundException($"No *_002.htm in {name}_files");
                return File.ReadAllText(descPath);
            }

            yield return new TestCaseData("ActiveBuyItNowListing", "be advised that the PlayStation 5 Pro has been opened and used but has", PageBuilder.LoadDocument(loadHtml("ActiveBuyItNowListing")));
            yield return new TestCaseData("BiddingEndedNoSale", "The new PS5 is slimmed down, but it's still just as fast. It's 24% lighter and 30% smaller than the original", PageBuilder.LoadDocument(loadHtml("BiddingEndedNoSale")));
            yield return new TestCaseData("SoldBidListing", "Mint condition Sony PlayStation 5 BluRay disc edition, 825GB. Full original boxing.Comes with Spiderman Miles Morales", PageBuilder.LoadDocument(loadHtml("SoldBidListing")));
            yield return new TestCaseData("SoldBuyNowListing", "Excellent condition, fully working. Items included:- Box- Controller- Power cable - HDMI cable Feel free to ask any questions.", PageBuilder.LoadDocument(loadHtml("SoldBuyNowListing")));
            yield return new TestCaseData("MissingDescription", null, PageBuilder.BuildEmptyDocument());
        }


        [Test]
        public async Task Should_return_end_date_as_utc()
        {
            var doc = PageBuilder.LoadTestHtmlDocument("SoldBidListing");
            var item = doc.QuerySelector("li.s-item[id]:not([id=\"\"])");

            var parser = (EbayListingParser)_serviceUnderTest;
            var endDate = parser.GetEndDate(doc);

            Assert.That(endDate.HasValue, Is.True, "❌ Expected a non-null end date");
            Assert.That(endDate.Value.Kind, Is.EqualTo(DateTimeKind.Utc),
                $"❌ Expected DateTimeKind.Utc but got {endDate.Value.Kind}");
        }

        [Test]
        public async Task Should_remove_nbsp_from_description()
        {
            var html = @"
                <div class=""x-item-description-child"">
                  First line&nbsp;with NBSP
                  <p>Second&nbsp;paragraph</p>
                </div>";

            var document = PageBuilder.LoadDocument(html);
            var parser = (EbayListingParser)_serviceUnderTest;
            var result = parser.ParseDescription(document);

            Assert.That(result, Does.Not.Contain("\u00A0"),
                "Parsed description should not contain non‑breaking spaces");
        }
    }
}