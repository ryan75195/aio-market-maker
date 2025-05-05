using AIOMarketMaker.Api.Parsers;
using AIOMarketMaker.Models.Ebay;
using AIOMarketMaker.Services;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Moq;
using NUnit.Framework;
using System.Text.RegularExpressions;

namespace AIOMarketMaker.Tests.Unit
{
    public class SearchParserUnitTests
    {
        private ServiceProvider _provider = null!;
        private ISearchParser _serviceUnderTest = null!;
        private Mock<IHtmlFetcher> _mockFetcher;

        public string NormalizeWhitespace(string s) => Regex.Replace(s, @"\s+", " ").Trim();

        [SetUp]
        public async Task SetupAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
            services.AddSingleton<EbaySearchParser>();
            services.AddSingleton<IEbayScraper, EbayScraper>();

            _mockFetcher = new Mock<IHtmlFetcher>();
            services.AddSingleton<IHtmlFetcher>(_mockFetcher.Object);

            _provider = services.BuildServiceProvider();
            _serviceUnderTest = _provider.GetRequiredService<EbaySearchParser>();
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

        [Test]
        public async Task Should_ignore_price_range_listings()
        {
            var doc = await LoadTestHtmlDocumentAsync("SearchResultsContainingPriceRanges");
            var parser = (EbaySearchParser)_serviceUnderTest;
            var result = parser.ParseSearchResults(doc!);

            Assert.IsFalse(result.Any(x => x.ListingId == "item5a65fc4c6c"));
        }

        [Test]
        public async Task Should_not_include_new_window_tab_suffix_in_titles()
        {
            // Arrange
            var doc = await LoadTestHtmlDocumentAsync("Sold_With_Small_Number_of_Real_Results");
            var parser = (EbaySearchParser)_serviceUnderTest;
            var items = doc.QuerySelectorAll("li.s-item[id]:not([id=\"\"])");

            // Act
            var titles = items
                .Select(parser.ExtractTitle)
                .ToList();

            // Assert
            Assert.Multiple(() =>
            {
                Assert.That(titles, Is.Not.Empty,
                    "❌ Expected at least one title to validate");

                Assert.That(titles, Is.All.Not.Contains("Opens in a new window or tab"),
                    "❌ One or more titles still contained the unwanted suffix");
            });
        }

        [Test]
        public async Task Should_not_include_item_prefix_in_ids()
        {
            // Arrange
            var doc = await LoadTestHtmlDocumentAsync("Sold_With_Small_Number_of_Real_Results");
            var parser = (EbaySearchParser)_serviceUnderTest;
            var items = doc.QuerySelectorAll("li.s-item[id]:not([id=\"\"])");

            // Act
            var ids = items
                .Select(parser.GetListingId)
                .ToList();

            // Assert
            Assert.Multiple(() =>
            {
                Assert.That(ids, Is.Not.Empty,
                    "❌ Expected at least one id to validate");

                Assert.That(ids, Is.All.Not.Contains("item"),
                    "❌ One or more ids still contained the unwanted prefix");
            });
        }

        [Test]
        public async Task Should_parse_item_id()
        {
            var doc = await LoadTestHtmlDocumentAsync("Sold_With_Small_Number_of_Real_Results");
            var parser = (EbaySearchParser)_serviceUnderTest;
            var item = doc.QuerySelectorAll("li.s-item[id]:not([id=\"\"])").First();

            var id = parser.GetListingId(item);

            Assert.That(id, Is.EqualTo("156876090176"));
        }

        [Test]
        public async Task Should_not_contain_query_params_in_url()
        {
            // Arrange
            var doc = await LoadTestHtmlDocumentAsync("Sold_With_Small_Number_of_Real_Results");
            var parser = (EbaySearchParser)_serviceUnderTest;
            var items = doc.QuerySelectorAll("li.s-item[id]:not([id=\"\"])");

            // Act
            var urls = items
                .Select(parser.GetListingUrl)
                .ToList();

            // Assert
            Assert.Multiple(() =>
            {
                Assert.That(urls, Is.Not.Empty,
                    "❌ Expected at least one url to validate");

                Assert.That(urls, Is.All.Not.Contains("?"),
                    "❌ One or more urls still contained query parameters");
            });
        }

        [Test]
        public async Task Should_return_end_date_as_utc()
        {
            // Arrange
            var doc = await LoadTestHtmlDocumentAsync("Sold_With_Small_Number_of_Real_Results");
            var parser = (EbaySearchParser)_serviceUnderTest;
            var items = doc.QuerySelectorAll("li.s-item[id]:not([id=\"\"])");

            // Act
            var endDates = items
                .Select(parser.ExtractDate)
                .ToList();

            // Assert
            Assert.Multiple(() =>
            {
                Assert.That(endDates, Is.Not.Empty,
                    "❌ Expected at least one end date to validate");

                Assert.That(endDates,
                    Is.All.Matches<DateTime>(d => d.Kind == DateTimeKind.Utc),
                    "❌ One or more dates were not DateTimeKind.Utc");
            });
        }

        private async Task<IDocument> LoadTestHtmlDocumentAsync(string testCaseName)
        {
            var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Search"));
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