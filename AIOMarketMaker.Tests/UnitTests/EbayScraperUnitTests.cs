using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Services;
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
                .Setup(x => x.NewJobAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<object>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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

        [Test]
        public async Task Should_return_results_in_date_range()
        {
            var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Search"));
            var htmlPath = Path.Combine(dataDir, "Sold_With_Small_Number_of_Real_Results.htm");
            var html = await File.ReadAllTextAsync(htmlPath);

            var query = "";
            var url = _urlBuilder.BuildSearchUrl(query, true, 1, Condition.USED, BuyingFormat.BUY_NOW);
            //Stub_ReturnsAsync(html, url);

            this._mockFetcher
                .Setup(x => x.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(html);

            this._mockFetcher
                .Setup(x => x.NewJobAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<object>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StartResponse("dummy_id"));

            this._mockFetcher
                .Setup(x => x.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new JobEntity("dummy_id", DateTime.UtcNow, JobStatusType.Success, 5, 5, 5, 0));

            this._mockFetcher
                .Setup(x => x.GetResultsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<JobItemEntity> { new JobItemEntity("dummy_id", JobStatusType.Success, url, DateTime.Now, "www.dummybloburl.com", "") });
            
            var start = new DateTime(2025, 5, 5).AddDays(-7);
            var end = new DateTime(2025, 5, 5);

            var result = await _serviceUnderTest.SearchSoldListings(query, BuyingFormat.BUY_NOW, Condition.USED, start, end);

            Assert.Multiple(() =>
            {
                Assert.That(
                    result.Select(x => x.EndDateUtc),
                    Is.All.GreaterThanOrEqualTo(start),
                    $"❌ Every EndDateUtc should be on or after {start:O}");

                Assert.That(
                    result.Select(x => x.EndDateUtc),
                    Is.All.LessThanOrEqualTo(end),
                    $"❌ Every EndDateUtc should be on or before {end:O}");
            });
        }

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