using AIOMarketMaker.Api.Parsers;
using AIOMarketMaker.Api.Services;
using AIOMarketMaker.Services;
using AIOMarketMaker.Tests.Utils;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ScraperWorker.Services;
using System.Text.RegularExpressions;

namespace AIOMarketMaker.Tests.Unit
{
    public class EbayScraperUnitTests
    {
        private ServiceProvider _provider = null!;
        private IEbayScraper _serviceUnderTest = null!;
        private IEbayUrlBuilder _urlBuilder;
        private Mock<IWebscraperClient> _mockFetcher;
        private Mock<IJobRepository> _mockJobRepository;

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
            //services.AddSingleton<TableServiceClient>();
            //services.AddSingleton<BlobServiceClient>();

            _mockFetcher = new Mock<IWebscraperClient>();
            _mockJobRepository = new Mock<IJobRepository>();

            services.AddSingleton<IWebscraperClient>(_mockFetcher.Object);
            services.AddSingleton<IJobRepository>(_mockJobRepository.Object);

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
        public async Task Should_get_active_listing()
        {
            var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Listings"));
            var htmlPath = Path.Combine(dataDir, "ActiveBuyItNowListing.htm");
            var html = await File.ReadAllTextAsync(htmlPath);
            var id = "306278488042";
            var url = _urlBuilder.BuildListingUrl(id);

            var descriptionHtml = "<div class=\"x-item-description-child\">dummy description text</div>";

            this._mockFetcher
                .Setup(x => x.RunJobAsync(It.IsAny<IEnumerable<string>>()))
                .ReturnsAsync(new List<JobItemEntity> { new JobItemEntity(id, JobStatusType.Success, url, DateTime.Now, "www.dummybloburl.com", "") });

            // stub the description fetch (matches any URL)
            //Stub_ReturnsAsync(descriptionHtml);
            Stub_ReturnsAsync(html);
            var result = await _serviceUnderTest.GetItemsFromListings([id]);

            ListingAssertions.AssertValidActiveListing(result.First(), id);
        }

        [Test]
        public async Task Should_get_sold_listing()
        {
            var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Listings"));
            var htmlPath = Path.Combine(dataDir, "SoldBuyNowListing.htm");
            var html = await File.ReadAllTextAsync(htmlPath);
            var id = "256918168190";
            var url = _urlBuilder.BuildListingUrl(id);

            var descriptionHtml = "<div class=\"x-item-description-child\">summy description text</div>";

            this._mockFetcher
                .Setup(x => x.NewJobAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<object>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StartResponse(id));

            this._mockFetcher
                .Setup(x => x.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new JobEntity(id, DateTime.UtcNow, JobStatusType.Success, 5, 5, 5, 0));

            this._mockFetcher
                .Setup(x => x.GetResultsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<JobItemEntity> { new JobItemEntity(id, JobStatusType.Success, url, DateTime.Now, "www.dummybloburl.com", "") });


            Stub_ReturnsAsync(html);

            var result = await _serviceUnderTest.GetItemsFromListings([id]);

            ListingAssertions.AssertValidSoldListing(result.First(), id);
        }

        #region SearchSoldListings Tests

        [Test]
        [Description("Verifies that SearchSoldListings returns only items within the specified date range")]
        public async Task Should_search_sold_listings_within_date_range()
        {
            var html = CreateMockSearchHtml();
            _mockFetcher.Setup(x => x.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(html);

            var startDate = new DateTime(2025, 1, 1);
            var endDate = new DateTime(2025, 1, 31);

            var result = await _serviceUnderTest.SearchSoldListings("test query", BuyingFormat.BUY_NOW, Condition.USED, startDate, endDate);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Not.Null);
                Assert.That(result.All(x => x.EndDateUtc >= startDate && x.EndDateUtc <= endDate), Is.True,
                    "All results should be within the specified date range");
            });
        }

        [Test]
        [Description("Verifies that SearchSoldListings handles empty search results gracefully")]
        public async Task Should_search_sold_listings_handle_empty_results()
        {
            var emptyHtml = CreateEmptySearchHtml();
            _mockFetcher.Setup(x => x.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(emptyHtml);

            var startDate = DateTime.UtcNow.AddDays(-7);
            var endDate = DateTime.UtcNow;

            var result = await _serviceUnderTest.SearchSoldListings("nonexistent item", BuyingFormat.AUCTION, Condition.NEW, startDate, endDate);

            Assert.That(result, Is.Empty, "Should return empty collection when no results found");
        }

        [Test]
        [TestCaseSource(nameof(BuyingFormatTestCases))]
        [Description("Verifies that SearchSoldListings works with different buying formats")]
        public async Task Should_search_sold_listings_with_buying_format(BuyingFormat format)
        {
            var html = CreateMockSearchHtml();
            _mockFetcher.Setup(x => x.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(html);

            var startDate = DateTime.UtcNow.AddDays(-30);
            var endDate = DateTime.UtcNow;

            var result = await _serviceUnderTest.SearchSoldListings("test", format, Condition.USED, startDate, endDate);

            Assert.That(result, Is.Not.Null, $"Should handle {format} buying format");
        }

        [Test]
        [TestCaseSource(nameof(ConditionTestCases))]
        [Description("Verifies that SearchSoldListings works with different item conditions")]
        public async Task Should_search_sold_listings_with_condition(Condition condition)
        {
            var html = CreateMockSearchHtml();
            _mockFetcher.Setup(x => x.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(html);

            var startDate = DateTime.UtcNow.AddDays(-7);
            var endDate = DateTime.UtcNow;

            var result = await _serviceUnderTest.SearchSoldListings("test", BuyingFormat.BUY_NOW, condition, startDate, endDate);

            Assert.That(result, Is.Not.Null, $"Should handle {condition} condition");
        }

        [Test]
        [Description("Verifies that SearchSoldListings removes duplicate listings across pages")]
        public async Task Should_search_sold_listings_remove_duplicates()
        {
            var htmlWithDuplicates = CreateMockSearchHtmlWithDuplicates();
            _mockFetcher.Setup(x => x.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(htmlWithDuplicates);

            var startDate = DateTime.UtcNow.AddDays(-7);
            var endDate = DateTime.UtcNow;

            var result = await _serviceUnderTest.SearchSoldListings("test", BuyingFormat.BUY_NOW, Condition.USED, startDate, endDate);

            var listingIds = result.Select(x => x.ListingId).ToList();
            Assert.That(listingIds.Distinct().Count(), Is.EqualTo(listingIds.Count), 
                "Should not contain duplicate listing IDs");
        }

        #endregion

        #region SearchActiveListings Tests

        [Test]
        [Description("Verifies that SearchActiveListings respects the item limit parameter")]
        public async Task Should_search_active_listings_respect_item_limit()
        {
            var html = CreateMockSearchHtml();
            _mockFetcher.Setup(x => x.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(html);

            const int itemLimit = 5;
            var result = await _serviceUnderTest.SearchActiveListings("test query", BuyingFormat.BUY_NOW, Condition.USED, itemLimit);

            Assert.That(result.Count(), Is.LessThanOrEqualTo(itemLimit), 
                $"Should not return more than {itemLimit} items");
        }

        [Test]
        [Description("Verifies that SearchActiveListings uses default limit when not specified")]
        public async Task Should_search_active_listings_use_default_limit()
        {
            var html = CreateMockSearchHtml();
            _mockFetcher.Setup(x => x.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(html);

            var result = await _serviceUnderTest.SearchActiveListings("test query", BuyingFormat.BUY_NOW, Condition.USED);

            Assert.That(result.Count(), Is.LessThanOrEqualTo(500), 
                "Should use default limit of 500 when not specified");
        }

        [Test]
        [Description("Verifies that SearchActiveListings handles empty search results")]
        public async Task Should_search_active_listings_handle_empty_results()
        {
            var emptyHtml = CreateEmptySearchHtml();
            _mockFetcher.Setup(x => x.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(emptyHtml);

            var result = await _serviceUnderTest.SearchActiveListings("nonexistent item", BuyingFormat.AUCTION, Condition.NEW, 10);

            Assert.That(result, Is.Empty, "Should return empty collection when no results found");
        }

        [Test]
        [TestCaseSource(nameof(BuyingFormatTestCases))]
        [Description("Verifies that SearchActiveListings works with different buying formats")]
        public async Task Should_search_active_listings_with_buying_format(BuyingFormat format)
        {
            var html = CreateMockSearchHtml();
            _mockFetcher.Setup(x => x.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(html);

            var result = await _serviceUnderTest.SearchActiveListings("test", format, Condition.USED, 10);

            Assert.That(result, Is.Not.Null, $"Should handle {format} buying format");
        }

        [Test]
        [TestCaseSource(nameof(ConditionTestCases))]
        [Description("Verifies that SearchActiveListings works with different item conditions")]
        public async Task Should_search_active_listings_with_condition(Condition condition)
        {
            var html = CreateMockSearchHtml();
            _mockFetcher.Setup(x => x.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(html);

            var result = await _serviceUnderTest.SearchActiveListings("test", BuyingFormat.BUY_NOW, condition, 10);

            Assert.That(result, Is.Not.Null, $"Should handle {condition} condition");
        }

        [Test]
        [Description("Verifies that SearchActiveListings removes duplicate listings across pages")]
        public async Task Should_search_active_listings_remove_duplicates()
        {
            var htmlWithDuplicates = CreateMockSearchHtmlWithDuplicates();
            _mockFetcher.Setup(x => x.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(htmlWithDuplicates);

            var result = await _serviceUnderTest.SearchActiveListings("test", BuyingFormat.BUY_NOW, Condition.USED, 50);

            var listingIds = result.Select(x => x.ListingId).ToList();
            Assert.That(listingIds.Distinct().Count(), Is.EqualTo(listingIds.Count), 
                "Should not contain duplicate listing IDs");
        }

        #endregion

        #region Test Data Sources

        private static IEnumerable<TestCaseData> BuyingFormatTestCases()
        {
            yield return new TestCaseData(BuyingFormat.BUY_NOW).SetName("BuyNow");
            yield return new TestCaseData(BuyingFormat.AUCTION).SetName("Auction");
            yield return new TestCaseData(BuyingFormat.ALL).SetName("All");
        }

        private static IEnumerable<TestCaseData> ConditionTestCases()
        {
            yield return new TestCaseData(Condition.NEW).SetName("New");
            yield return new TestCaseData(Condition.USED).SetName("Used");
            yield return new TestCaseData(Condition.FOR_PARTS_NOT_WORKING).SetName("ForParts");
            yield return new TestCaseData(Condition.EXCELLENT_REFURBISHED).SetName("ExcellentRefurb");
        }

        #endregion

        #region Helper Methods

        private string CreateMockSearchHtml()
        {
            return @"
                <html>
                <body>
                    <div class=""s-item"">
                        <div class=""s-item__wrapper"">
                            <h3 class=""s-item__title"">Test Item 1</h3>
                            <span class=""s-item__price"">$10.00</span>
                            <a class=""s-item__link"" href=""https://www.ebay.com/itm/123456789""></a>
                            <span class=""s-item__detail s-item__detail--primary"">
                                <span class=""NEGATIVE"">Sold</span>
                                <span class=""s-item__endedDate"">Jan 15, 2025</span>
                            </span>
                        </div>
                    </div>
                </body>
                </html>";
        }

        private string CreateEmptySearchHtml()
        {
            return @"
                <html>
                <body>
                    <div class=""srp-results"">
                        <div class=""srp-river-results"">
                            <!-- No results -->
                        </div>
                    </div>
                </body>
                </html>";
        }

        private string CreateMockSearchHtmlWithDuplicates()
        {
            return @"
                <html>
                <body>
                    <div class=""s-item"">
                        <div class=""s-item__wrapper"">
                            <h3 class=""s-item__title"">Test Item 1</h3>
                            <span class=""s-item__price"">$10.00</span>
                            <a class=""s-item__link"" href=""https://www.ebay.com/itm/123456789""></a>
                        </div>
                    </div>
                    <div class=""s-item"">
                        <div class=""s-item__wrapper"">
                            <h3 class=""s-item__title"">Test Item 1</h3>
                            <span class=""s-item__price"">$10.00</span>
                            <a class=""s-item__link"" href=""https://www.ebay.com/itm/123456789""></a>
                        </div>
                    </div>
                </body>
                </html>";
        }

        #endregion

        private void Stub_ReturnsAsync(string returnHtml, string? url = null)
        {
            if (url is null)
            {
                _mockJobRepository
                    .Setup(x => x.GetFileContentsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(returnHtml);
            }
            else
            {
                _mockJobRepository
                    .Setup(x => x.GetFileContentsAsync(It.IsAny<string>(), url, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(returnHtml);
            }
        }

        private async Task<IDocument> LoadDocumentAsync(string html)
        {
            return await BrowsingContext.New(Configuration.Default)
                         .OpenAsync(req => req.Content(html))
                         .ConfigureAwait(false);
        }
    }
}