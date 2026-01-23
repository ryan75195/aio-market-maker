using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ScraperWorker.Services;
using System.Net.Sockets;

namespace AIOMarketMaker.Tests.E2E;

public abstract class E2ETestFixture
{
    protected static MockEbayServer? MockServer;
    protected EtlDbContext DbContext = null!;
    protected IEbayScraper EbayScraper = null!;
    protected SqliteConnection Connection = null!;

    private const string ScraperApiUrl = "http://localhost:7126/";
    private const int MockEbayPort = 9999;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Start mock eBay server (shared across all tests in fixture)
        MockServer = new MockEbayServer(MockEbayPort);
        MockServer.Start();

        // Verify AIOWebScraper is running
        if (!IsScraperApiAvailable())
        {
            Assert.Ignore("AIOWebScraper not running on localhost:7126. Start with: dotnet run -- --dedicated-mode");
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        MockServer?.Dispose();
        MockServer = null;
    }

    [SetUp]
    public async Task SetUp()
    {
        // Create in-memory SQLite database
        Connection = new SqliteConnection("Data Source=:memory:");
        await Connection.OpenAsync();

        var options = new DbContextOptionsBuilder<EtlDbContext>()
            .UseSqlite(Connection)
            .Options;

        DbContext = new EtlDbContext(options);
        await DbContext.Database.EnsureCreatedAsync();

        // Build services with mock URL builder pointing to our mock server
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

        // Use testable URL builder pointing to mock server
        services.AddSingleton<IEbayUrlBuilder>(new TestableEbayUrlBuilder(MockServer!.BaseUrl));

        // Real parsers
        services.AddSingleton<ISearchParser, EbaySearchParser>();
        services.AddSingleton<IListingParser, EbayListingParser>();

        // Real WebscraperClient pointing to real AIOWebScraper
        services.AddHttpClient<IWebscraperClient, WebscraperClient>(client =>
        {
            client.BaseAddress = new Uri(ScraperApiUrl);
        });
        services.AddSingleton(new ScraperApiConfig(ScraperApiUrl, ""));

        // Mock job repository (we don't need Azure Storage for E2E tests)
        var mockJobRepo = new Mock<IJobRepository>();
        services.AddSingleton(mockJobRepo.Object);

        // Real EbayScraper
        services.AddSingleton<IEbayScraper, EbayScraper>();

        // Database context
        services.AddSingleton(DbContext);

        var provider = services.BuildServiceProvider();
        EbayScraper = provider.GetRequiredService<IEbayScraper>();
    }

    [TearDown]
    public async Task TearDown()
    {
        await DbContext.DisposeAsync();
        await Connection.DisposeAsync();
    }

    private static bool IsScraperApiAvailable()
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("localhost", 7126);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
