using AIOMarketMaker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

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
            var validIsoOrSymbols = new[] { "GBP", "$", "£", "€", "USD", "EUR" };

            Assert.Multiple(() =>
            {
                // Identity
                Assert.That(listing, Is.Not.Null, "Listing must not be null");
                Assert.That(listing.ListingId, Is.EqualTo(itemId), "ID must match");
                Assert.That(listing.Url, Does.StartWith("https://").And.Contains(itemId));

                // Presence
                Assert.That(listing.Title, Is.Not.Null.And.Not.Empty);
                //Assert.That(listing.Condition, Is.Not.Null);
                Assert.That(listing.Description, Is.Not.Null);
                Assert.That(listing.ItemSpecifics, Is.Not.Null);

                // Value checks
                Assert.That(listing.Price, Is.GreaterThan(0));
                Assert.That(validIsoOrSymbols, Does.Contain(listing.Currency), "Currency should be valid ISO code or known symbol");
                Assert.That(listing.ShippingCost, Is.GreaterThanOrEqualTo(0));
                Assert.That(listing.Images, Is.Not.Null.And.Not.Empty);
            });
        }

        [Test]
        public async Task Should_successfully_retrieve_sold()
        {
            var itemId = "256918168190";
            var listing = await this._serviceUnderTest.GetItemFromListing(itemId);
        }
    }
}
