using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AIOMarketMaker.Tests.Contract
{
    public class ListingContractTests
    {
        private IEbayScraper _serviceUnderTest = null!;

        [SetUp]
        public void Setup()
        {
            Environment.SetEnvironmentVariable(
              "StorageConnectionString",
              "DefaultEndpointsProtocol=https;AccountName=webscraperstorageacc;AccountKey=zio92liWbgYZN9oS/L65JV2RZp21eXanu19X1G+ioDO7UI0qAMj5wAICuaSPwOcwnM+fk4Y3pvgs+AStZmZOHg==;EndpointSuffix=core.windows.net"
            );

            var config = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();

            var services = new ServiceCollection();
            services.AddEbayScraperPipeline(config);
            var provider = services.BuildServiceProvider();
            _serviceUnderTest = provider.GetRequiredService<IEbayScraper>();
        }

        [Test]
        public async Task Should_successfully_retrieve_active_listing()
        {
            var itemId = "135758131788";

            var listing = await this._serviceUnderTest.GetItemsFromListings([itemId]);
         
            ListingAssertions.AssertValidActiveListing(listing.First(), itemId);
        }

        [Test]
        public async Task Should_successfully_retrieve_sold()
        {
            var itemId = "256918168190";
            var listing = await this._serviceUnderTest.GetItemsFromListings([itemId]);
            ListingAssertions.AssertValidSoldListing(listing.First(), itemId);
        }
    }
}
