using AIOMarketMaker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using AIOMarketMaker.Tests.Utils;

namespace AIOMarketMaker.Tests.Contract
{
    public class ListingContractTests
    {
        private ServiceProvider _provider = null!;
        private IEbayScraper _serviceUnderTest = null!;

        [SetUp]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddEbayScraperPipeline();
            _provider = services.BuildServiceProvider();
            _serviceUnderTest = _provider.GetRequiredService<IEbayScraper>();
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
        public async Task Should_successfully_retrieve_active_listing()
        {
            var itemId = "135758131788";

            var listing = await this._serviceUnderTest.GetItemFromListing(itemId);
         
            ListingAssertions.AssertValidActiveListing(listing, itemId);
        }

        [Test]
        public async Task Should_successfully_retrieve_sold()
        {
            var itemId = "256918168190";
            var listing = await this._serviceUnderTest.GetItemFromListing(itemId);
            ListingAssertions.AssertValidSoldListing(listing, itemId);
        }
    }
}
