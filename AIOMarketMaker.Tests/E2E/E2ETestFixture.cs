using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScraperWorker.Services;
using System.Net.Sockets;

namespace AIOMarketMaker.Tests.E2E;

public abstract class E2ETestFixture
{
    protected static MockEbayServer? MockServer;
    protected static LocalTestInfrastructure? Infrastructure;
    protected EtlDbContext DbContext = null!;
    protected IEbayScraper EbayScraper = null!;
    protected SqliteConnection Connection = null!;

    private ServiceProvider? _serviceProvider;
    private HttpClient? _httpClient;
    private ILoggerFactory? _loggerFactory;

    private const string AzuriteConnectionString = "UseDevelopmentStorage=true";
    private const string FunctionsApiUrl = "http://localhost:7071/";
    private const int MockEbayPort = 9999;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // Start mock eBay server (shared across all tests in fixture)
        MockServer = new MockEbayServer(MockEbayPort);
        MockServer.Start();

        // Check if infrastructure is already running (e.g., started manually)
        var azuriteRunning = IsPortInUse(LocalTestInfrastructure.AzuritePort);
        var functionsRunning = IsPortInUse(LocalTestInfrastructure.FunctionsPort);

        Console.WriteLine($"Port check - Azurite ({LocalTestInfrastructure.AzuritePort}): {azuriteRunning}, Functions ({LocalTestInfrastructure.FunctionsPort}): {functionsRunning}");

        if (azuriteRunning && functionsRunning)
        {
            Console.WriteLine("Infrastructure already running (Azurite and Functions detected)");
            return;
        }

        // If not all running, start what's missing via LocalTestInfrastructure
        Infrastructure = new LocalTestInfrastructure();

        try
        {
            if (!azuriteRunning)
            {
                await Infrastructure.StartAzuriteAsync();
            }

            if (!functionsRunning)
            {
                // Paths relative to test output directory
                var testDir = TestContext.CurrentContext.TestDirectory;
                var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
                var functionsProject = Path.Combine(repoRoot, "AIOWebScraper", "AIOWebScraper");

                if (!Directory.Exists(functionsProject))
                {
                    Assert.Ignore($"AIOWebScraper Functions project not found at: {functionsProject}. " +
                        "Start infrastructure manually or verify project paths.");
                }

                await Infrastructure.StartFunctionsApiAsync(functionsProject);

                // Also start the worker
                var workerProject = Path.Combine(repoRoot, "AIOWebScraper", "ScraperWorker");
                if (Directory.Exists(workerProject))
                {
                    await Infrastructure.StartWorkerAsync(workerProject);
                }
            }
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            Infrastructure?.Dispose();
            Infrastructure = null;
            Assert.Ignore($"E2E infrastructure not available: {ex.Message}. " +
                "Start Azurite and infrastructure manually, or run: docker run -p 10000-10002:10000-10002 mcr.microsoft.com/azure-storage/azurite");
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        MockServer?.Dispose();
        MockServer = null;

        // Only dispose infrastructure we started
        Infrastructure?.Dispose();
        Infrastructure = null;
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

        // Build services using production code paths
        var services = new ServiceCollection();

        // Create a shared logger factory (disposed in TearDown)
        _loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(_loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Use testable URL builder pointing to mock server
        services.AddSingleton<IEbayUrlBuilder>(new TestableEbayUrlBuilder(MockServer!.BaseUrl));

        // Real parsers
        services.AddSingleton<ISearchParser, EbaySearchParser>();
        services.AddSingleton<IListingParser, EbayListingParser>();

        // Production WebscraperClient pointing to Azure Functions API
        _httpClient = new HttpClient { BaseAddress = new Uri(FunctionsApiUrl) };
        var config = new ScraperApiConfig(FunctionsApiUrl, "");

        // Production AzureJobRepository pointing to Azurite
        var tableServiceClient = new TableServiceClient(AzuriteConnectionString);
        var blobServiceClient = new BlobServiceClient(AzuriteConnectionString);
        var jobRepoLogger = _loggerFactory.CreateLogger<AzureJobRepository>();
        var jobRepository = new AzureJobRepository(tableServiceClient, blobServiceClient, jobRepoLogger);
        services.AddSingleton<IJobRepository>(jobRepository);

        // Production WebscraperClient
        var webscraperLogger = _loggerFactory.CreateLogger<WebscraperClient>();
        services.AddSingleton<IWebscraperClient>(
            new WebscraperClient(_httpClient, config, jobRepository, webscraperLogger));

        // Real EbayScraper
        services.AddSingleton<IEbayScraper, EbayScraper>();

        // Database context
        services.AddSingleton(DbContext);

        _serviceProvider = services.BuildServiceProvider();
        EbayScraper = _serviceProvider.GetRequiredService<IEbayScraper>();
    }

    [TearDown]
    public async Task TearDown()
    {
        // Dispose in reverse order of creation
        _serviceProvider?.Dispose();
        _serviceProvider = null;

        _httpClient?.Dispose();
        _httpClient = null;

        _loggerFactory?.Dispose();
        _loggerFactory = null;

        await DbContext.DisposeAsync();
        await Connection.DisposeAsync();
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
}
