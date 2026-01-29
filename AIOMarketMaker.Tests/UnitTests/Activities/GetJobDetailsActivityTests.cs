using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class GetJobDetailsActivityTests
{
    private EtlDbContext _dbContext = null!;
    private const int DefaultLookbackDays = 90;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    private GetJobDetailsActivity CreateActivity(
        int? maxSoldListings = null,
        int? maxActiveListings = null)
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Scraping:DefaultLookbackDays"] = DefaultLookbackDays.ToString()
        };
        if (maxSoldListings.HasValue)
            configValues["Scraping:MaxSoldListings"] = maxSoldListings.Value.ToString();
        if (maxActiveListings.HasValue)
            configValues["Scraping:MaxActiveListings"] = maxActiveListings.Value.ToString();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        return new GetJobDetailsActivity(
            _dbContext,
            config,
            NullLogger<GetJobDetailsActivity>.Instance);
    }

    [Test]
    public async Task Should_return_null_when_job_not_found()
    {
        var activity = CreateActivity();
        var result = await activity.Run(new GetJobDetailsInput(999), null!);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_use_default_lookback_when_never_run()
    {
        var job = new ScrapeJob { SearchTerm = "test", LastRunUtc = null };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var activity = CreateActivity();
        var result = await activity.Run(new GetJobDetailsInput(job.Id), null!);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.LookbackDays, Is.EqualTo(DefaultLookbackDays));
            Assert.That(result.SearchTerm, Is.EqualTo("test"));
        });
    }

    [Test]
    public async Task Should_return_configured_max_sold_listings()
    {
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var activity = CreateActivity(maxSoldListings: 50);
        var result = await activity.Run(new GetJobDetailsInput(job.Id), null!);

        Assert.That(result!.MaxSoldListings, Is.EqualTo(50));
    }

    [Test]
    public async Task Should_return_configured_max_active_listings()
    {
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var activity = CreateActivity(maxActiveListings: 75);
        var result = await activity.Run(new GetJobDetailsInput(job.Id), null!);

        Assert.That(result!.MaxActiveListings, Is.EqualTo(75));
    }

    [Test]
    public async Task Should_return_null_limits_when_not_configured()
    {
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var activity = CreateActivity();
        var result = await activity.Run(new GetJobDetailsInput(job.Id), null!);

        Assert.Multiple(() =>
        {
            Assert.That(result!.MaxSoldListings, Is.Null);
            Assert.That(result!.MaxActiveListings, Is.Null);
        });
    }

    [Test]
    public async Task Should_use_runtime_override_for_max_sold_listings()
    {
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var activity = CreateActivity(maxSoldListings: 50);
        var input = new GetJobDetailsInput(job.Id, MaxSoldListings: 25);
        var result = await activity.Run(input, null!);

        Assert.That(result!.MaxSoldListings, Is.EqualTo(25));
    }

    [Test]
    public async Task Should_use_runtime_override_for_max_active_listings()
    {
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var activity = CreateActivity(maxActiveListings: 75);
        var input = new GetJobDetailsInput(job.Id, MaxActiveListings: 30);
        var result = await activity.Run(input, null!);

        Assert.That(result!.MaxActiveListings, Is.EqualTo(30));
    }
}
