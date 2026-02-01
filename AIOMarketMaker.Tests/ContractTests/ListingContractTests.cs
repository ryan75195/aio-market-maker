using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Parsers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Tests.Utils;
using ScraperWorker.Services;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using System.Net.Sockets;

namespace AIOMarketMaker.Tests.Contract
{
    /// <summary>
    /// Contract tests that validate the scraper works with real Azure infrastructure.
    /// Requires: Azure Storage account, ScraperWorker running on localhost:7071.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    [Category("Integration")]
    [Explicit("Requires ScraperWorker API running on localhost:7071 and Azure Storage")]
    public class ListingContractTests
    {
        private IEbayScraper _serviceUnderTest = null!;
        private ServiceProvider? _serviceProvider;
        private ILoggerFactory? _loggerFactory;

        private const string ScraperApiUrl = "http://localhost:7071/";
        private const int ScraperApiPort = 7071;

        // Uses Azurite for local dev, real Azure for CI
        private static string GetStorageConnectionString() =>
            Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
            ?? "UseDevelopmentStorage=true";

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // Check if scraper API is available
            if (!IsPortInUse(ScraperApiPort))
            {
                Assert.Ignore(
                    $"Contract tests require ScraperWorker API running on port {ScraperApiPort}. " +
                    "Start with: cd AIOWebScraper && func start (Azure Functions) or see README-LOCAL-DEV.md");
            }
        }

        [SetUp]
        public void Setup()
        {
            var storageConnectionString = GetStorageConnectionString();

            var services = new ServiceCollection();

            // Logging
            _loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton(_loggerFactory);

            // Azure Storage clients
            services.AddSingleton(new TableServiceClient(storageConnectionString));
            services.AddSingleton(new BlobServiceClient(storageConnectionString));

            // Parsers and URL builder
            services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
            services.AddSingleton<ISearchParser, EbaySearchParser>();
            services.AddSingleton<IListingParser, EbayListingParser>();

            // Job repository
            services.AddSingleton<IJobRepository, AzureJobRepository>();

            // Scraper API config (required by WebscraperClient)
            var config = new ScraperApiConfig(ScraperApiUrl, "");
            services.AddSingleton(config);

            // WebscraperClient with all dependencies
            services.AddHttpClient<IWebscraperClient, WebscraperClient>(client =>
            {
                client.BaseAddress = new Uri(ScraperApiUrl);
            });

            // EbayScraper
            services.AddSingleton<IEbayScraper, EbayScraper>();

            _serviceProvider = services.BuildServiceProvider();
            _serviceUnderTest = _serviceProvider.GetRequiredService<IEbayScraper>();
        }

        [TearDown]
        public void TearDown()
        {
            _serviceProvider?.Dispose();
            _loggerFactory?.Dispose();
        }

        private static bool IsPortInUse(int port)
        {
            try
            {
                using var client = new TcpClient();
                // Use 127.0.0.1 instead of localhost for more reliable Windows behavior
                var result = client.BeginConnect("127.0.0.1", port, null, null);
                var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));
                if (success)
                {
                    try
                    {
                        client.EndConnect(result);
                        return true;
                    }
                    catch
                    {
                        // EndConnect can throw if connection was refused
                        return false;
                    }
                }
                return false;
            }
            catch
            {
                // Any connection error means port is not available
                return false;
            }
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
