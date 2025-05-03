using AIOMarketMaker.Services;

namespace AIOMarketMaker.Tests.Integration
{
    public class Tests
    {
        [SetUp]
        public void Setup()
        {
            var urlBuilder = new EbayUrlBuilder();
            var parser = new EbayItemParser();
            var httpClient = new HttpClient();
            var htmlFetcher = new HtmlFetcher(httpClient, null);
            var _serviceUnderTest = new EbayScraper(urlBuilder, htmlFetcher, parser, null);
        }

        [Test]
        [TestCaseSource(nameof(SearchTestCases))]
        public void Scraper_should_search_listings(SearchTestCase testCase)
        {
            var query = testCase.Query;
            var filters = testCase.Filters;
        }

        private static IEnumerable<TestCaseData> SearchTestCases()
        {
            yield return new SearchTestCase(
                Query: "ff",
                Filters: new SearchFilter { BuyingFormat = "dd" },
                TestName: "Search with buy it now format")
            .ToTestCaseData();

            yield return new SearchTestCase(
                Query: "ff",
                Filters: new SearchFilter { BuyingFormat = "dd" },
                TestName: "Search with buy it now format")
            .ToTestCaseData();
        }

        public record SoldFilter(DateTime startDate, DateTime endDate);

        public record SearchFilter
        {
            public SoldFilter? SoldFilter { get; init; }
            public string? BuyingFormat { get; init; }
            public string? Condition { get; init; }
        }

        public record SearchTestCase(
            string Query,
            SearchFilter? Filters = null,
            string? TestName = null   // ← new, optional override
        )
        {
            public TestCaseData ToTestCaseData()
            {
                // Use the explicit TestName if provided, otherwise build one
                var name = !string.IsNullOrWhiteSpace(TestName)
                    ? TestName!
                    : BuildDefaultName();

                return new TestCaseData(this)
                    .SetName(name);
            }

            private string BuildDefaultName()
            {
                var parts = new List<string> { Query };

                if (Filters?.BuyingFormat is { Length: > 0 } fmt)
                    parts.Add(fmt);
                if (Filters?.Condition is { Length: > 0 } cond)
                    parts.Add(cond);

                return "Scraper_should_search_listings__" + string.Join("_", parts);
            }
        }
    }
}