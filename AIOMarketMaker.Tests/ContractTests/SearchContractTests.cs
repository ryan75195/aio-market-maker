//using AIOMarketMaker.Services;
//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Playwright;

//namespace AIOMarketMaker.Tests.Contract
//{
//    public class SearchContractTests
//    {
//        private ServiceProvider _provider = null!;
//        private IEbayScraper _serviceUnderTest = null!;
//        static string searchQuery = "Playstation 5";

//        [SetUp]
//        public void Setup()
//        {
//            var services = new ServiceCollection();
//            services.AddEbayScraperPipeline();
//            _provider = services.BuildServiceProvider();
//            _serviceUnderTest = _provider.GetRequiredService<IEbayScraper>();
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
//        [TestCaseSource(nameof(BuyingFormatTestCases))]
//        public async Task Should_successfully_filter_buying_format(SearchTestCase testCase)
//        {
//            var results = await _serviceUnderTest.SearchListings(testCase.Query, testCase.Filters);
//            Assert.AreEqual(testCase.Filters.BuyingFormat, results.First().BuyingFormat);
//        }

//        private static IEnumerable<TestCaseData> BuyingFormatTestCases()
//        {
//            yield return new SearchTestCase(
//                    Query: searchQuery,
//                    Filters: new SearchFilter { BuyingFormat = BuyingFormat.BUY_NOW })
//                .ToTestCaseData();

//            yield return new SearchTestCase(
//                    Query: searchQuery,
//                    Filters: new SearchFilter { BuyingFormat = BuyingFormat.AUCTION })
//                .ToTestCaseData();
//        }

//        [Test]
//        public async Task Should_successfully_filter_sold()
//        {
//            var _now = DateTime.UtcNow;
//            var filter = new SearchFilter { SearchDateRange = new SearchDateRange(_now.AddDays(-1), _now) };
//            var results = await _serviceUnderTest.SearchListings(searchQuery, filter);
//            var startDate = filter.SearchDateRange.startDate;
//            var endDate = filter.SearchDateRange.endDate;

//            Assert.IsNotEmpty(results, "Expected at least one sold listing.");

//            Assert.IsTrue(
//                results.All(x => x.EndDateUtc.HasValue),
//                "All listings should have a non-null soldDateUtc when filtering sold items."
//            );

//            // Now that HasValue is guaranteed, use .Value for the range checks
//            Assert.IsFalse(
//                results.Any(x => x.EndDateUtc.Value < startDate),
//                "No listings should have a soldDateUtc earlier than the start date."
//            );

//            Assert.IsFalse(
//                results.Any(x => x.EndDateUtc.Value > endDate),
//                "No listings should have a soldDateUtc greater than the end date."
//            );
//        }

//        [Test]
//        [TestCaseSource(nameof(ConditionFilterTestCases))]
//        public async Task Should_successfully_filter_on_condition(SearchTestCase testCase)
//        {
//            var results = await _serviceUnderTest.SearchListings(testCase.Query, testCase.Filters);
//            Assert.AreEqual(testCase.Filters.Condition, results.First().Condition);
//        }

//        private static IEnumerable<TestCaseData> ConditionFilterTestCases()
//        {
//            yield return new SearchTestCase(
//                Query: searchQuery,
//                Filters: new SearchFilter { Condition = Condition.NEW })
//            .ToTestCaseData();

//            yield return new SearchTestCase(
//                Query: searchQuery,
//                Filters: new SearchFilter { Condition = Condition.OPENED_NEVER_USED })
//            .ToTestCaseData();

//            yield return new SearchTestCase(
//                Query: searchQuery,
//                Filters: new SearchFilter { Condition = Condition.EXCELLENT_REFURBISHED })
//            .ToTestCaseData();

//            yield return new SearchTestCase(
//                Query: searchQuery,
//                Filters: new SearchFilter { Condition = Condition.VERY_GOOD_REFURBISHED })
//            .ToTestCaseData();

//            yield return new SearchTestCase(
//                Query: searchQuery,
//                Filters: new SearchFilter { Condition = Condition.GOOD_REFURBISHED })
//            .ToTestCaseData();

//            yield return new SearchTestCase(
//                Query: searchQuery,
//                Filters: new SearchFilter { Condition = Condition.USED })
//            .ToTestCaseData();

//            yield return new SearchTestCase(
//                Query: searchQuery,
//                Filters: new SearchFilter { Condition = Condition.FOR_PARTS_NOT_WORKING })
//            .ToTestCaseData();
//        }

//        // Simplified TestCase record—no TestName, no SetName()
//        public record SearchTestCase(
//            string Query,
//            SearchFilter? Filters = null
//        )
//        {
//            public TestCaseData ToTestCaseData() => new TestCaseData(this);
//        }
//    }
//}
