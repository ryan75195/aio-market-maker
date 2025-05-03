using NUnit.Framework;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using AIOMarketMaker.Services;

[SetUpFixture]
public class IntegrationTestSetup
{
    public static IEbayScraper Scraper { get; private set; } = null!;
    private static IHost _host = null!;

    [OneTimeSetUp]
    public void GlobalSetup()
    {
        _host = new HostBuilder()
          // brings in the Functions worker defaults so AddHttpClient, logging, config, etc work the same
          .ConfigureFunctionsWorkerDefaults()
          .ConfigureServices(services =>
          {
              services.AddEbayScraperPipeline();
          })
          .Build();

        Scraper = _host.Services.GetRequiredService<IEbayScraper>();
    }

    [OneTimeTearDown]
    public void GlobalTeardown()
    {
        if (_host is IDisposable d) d.Dispose();
    }
}
