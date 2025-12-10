using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Parsers;
using Microsoft.Extensions.DependencyInjection;
using AIOMarketMaker.Tests.Utils;
using ScraperWorker.Services;
using Azure.Data.Tables;
using Azure.Storage.Blobs;

namespace AIOMarketMaker.Tests.Contract
{
    public class ListingContractTests
    {
        private IEbayScraper _serviceUnderTest = null!;

        [SetUp]
        public void Setup()
        {
            var storageConnectionString = "DefaultEndpointsProtocol=https;AccountName=YOUR_STORAGE_ACCOUNT;AccountKey=REDACTED_STORAGE_KEY==;EndpointSuffix=core.windows.net";

            var services = new ServiceCollection();

            services.AddSingleton(new TableServiceClient(storageConnectionString));
            services.AddSingleton(new BlobServiceClient(storageConnectionString));
            services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
            services.AddSingleton<ISearchParser, EbaySearchParser>();
            services.AddSingleton<IListingParser, EbayListingParser>();
            services.AddSingleton<IJobRepository, AzureJobRepository>();
            services.AddHttpClient<IWebscraperClient, WebscraperClient>(client => {
                client.BaseAddress = new Uri("http://localhost:7126");
            });
            services.AddSingleton<IEbayScraper, EbayScraper>();

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
