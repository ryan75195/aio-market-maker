using AIOMarketMaker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AIOMarketMaker.Tests.Integration
{
    public class Tests
    {
        private ServiceProvider _provider = null!;
        private IEbayScraper _serviceUnderTest = null!;
        static string searchQuery = "Playstation 5";

        [SetUp]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddEbayScraperPipeline();
            _provider = services.BuildServiceProvider();
            _serviceUnderTest = _provider.GetRequiredService<IEbayScraper>();
        }

        [TearDown]
        public void TearDown()
        {
            _provider.Dispose();
        }

        [Test]
        [TestCaseSource(nameof(BuyingFormatTestCases))]
        public async Task Should_successfully_filter_buying_format(SearchTestCase testCase)
        {
            var results = await _serviceUnderTest.SearchListings(testCase.Query, testCase.Filters);
            Assert.AreEqual(testCase.Filters.BuyingFormat, results.First().buyingFormat);
        }

        private static IEnumerable<TestCaseData> BuyingFormatTestCases()
        {
            yield return new SearchTestCase(
                    Query: searchQuery,
                    Filters: new SearchFilter { BuyingFormat = BuyingFormat.BUY_NOW })
                .ToTestCaseData();

            yield return new SearchTestCase(
                    Query: searchQuery,
                    Filters: new SearchFilter { BuyingFormat = BuyingFormat.AUCTION })
                .ToTestCaseData();

            yield return new SearchTestCase(
                    Query: searchQuery,
                    Filters: new SearchFilter { BuyingFormat = BuyingFormat.ALL })
                .ToTestCaseData();
        }

        [Test]
        public async Task Should_successfully_filter_sold()
        {
            var _now = DateTime.UtcNow;
            var filter = new SearchFilter { SoldFilter = new SoldRange(_now.AddDays(-1), _now) };
            var results = await _serviceUnderTest.SearchListings(searchQuery, filter);
            var startDate = filter.SoldFilter.startDate;
            var endDate = filter.SoldFilter.endDate;

            Assert.IsNotEmpty(results, "Expected at least one sold listing.");

            Assert.IsTrue(
                results.All(x => x.soldDateUtc.HasValue),
                "All listings should have a non-null soldDateUtc when filtering sold items."
            );

            // Now that HasValue is guaranteed, use .Value for the range checks
            Assert.IsFalse(
                results.Any(x => x.soldDateUtc.Value < startDate),
                "No listings should have a soldDateUtc earlier than the start date."
            );

            Assert.IsFalse(
                results.Any(x => x.soldDateUtc.Value > endDate),
                "No listings should have a soldDateUtc greater than the end date."
            );
        }

        [Test]
        [TestCaseSource(nameof(ConditionFilterTestCases))]
        public async Task Should_successfully_filter_on_condition(SearchTestCase testCase)
        {
            var results = await _serviceUnderTest.SearchListings(testCase.Query, testCase.Filters);
            Assert.AreEqual(testCase.Filters.Condition, results.First().condition);
        }

        private static IEnumerable<TestCaseData> ConditionFilterTestCases()
        {
            yield return new SearchTestCase(
                    Query: searchQuery,
                    Filters: new SearchFilter { Condition = Condition.NEW })
                .ToTestCaseData();

            yield return new SearchTestCase(
                    Query: searchQuery,
                    Filters: new SearchFilter { Condition = Condition.USED })
                .ToTestCaseData();

            yield return new SearchTestCase(
                    Query: searchQuery,
                    Filters: new SearchFilter { Condition = Condition.ANY })
                .ToTestCaseData();

            yield return new SearchTestCase(
                    Query: searchQuery,
                    Filters: new SearchFilter { Condition = Condition.NOT_SPECIFIED })
                .ToTestCaseData();
        }

        // Simplified TestCase record—no TestName, no SetName()
        public record SearchTestCase(
            string Query,
            SearchFilter? Filters = null
        )
        {
            public TestCaseData ToTestCaseData() => new TestCaseData(this);
        }
    }
}
