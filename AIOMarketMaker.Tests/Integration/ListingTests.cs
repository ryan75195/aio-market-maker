using AIOMarketMaker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AIOMarketMaker.Tests.Integration
{
    public class ListingTests
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
        public void TearDown()
        {
            _provider.Dispose();
        }

        [Test]
        public async Task Should_successfully_retrieve_active_listing()
        {
            var itemId = (await this._serviceUnderTest.SearchListings(
                "Baseball Card",
                new SearchFilter
                {
                    BuyingFormat = BuyingFormat.BUY_NOW,
                    Condition = Condition.NEW
                }))
                .First().id;

            var listing = await this._serviceUnderTest.GetItemFromListing(itemId);
            Assert.That(listing.id != null);
            Assert.That(listing.title != null);
            Assert.That(listing.price != null);
            Assert.That(listing.url != null);
            Assert.That(listing.Condition != null);
            Assert.That(listing.currency != null);
            Assert.That(listing.Description != null);
            Assert.That(listing.images != null);
            Assert.That(listing.ItemSpecifics != null);
            Assert.That(listing.shippingCost != null);
        }

        [Test]
        public async Task Should_successfully_retrieve_sold()
        {
            Assert.Fail();
        }
    }
}
