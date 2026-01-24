using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Functions.Activities;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class GetJobDetailsActivityTests
{
    private EtlDbContext _dbContext = null!;
    private GetJobDetailsActivity _activity = null!;
    private const int DefaultLookbackDays = 90;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scraping:DefaultLookbackDays"] = DefaultLookbackDays.ToString()
            })
            .Build();

        _activity = new GetJobDetailsActivity(
            _dbContext,
            config,
            NullLogger<GetJobDetailsActivity>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task Should_return_null_when_job_not_found()
    {
        // Act
        var result = await _activity.Run(999, null!);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_use_default_lookback_when_never_run()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test", LastRunUtc = null };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _activity.Run(job.Id, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.LookbackDays, Is.EqualTo(DefaultLookbackDays));
            Assert.That(result.SearchTerm, Is.EqualTo("test"));
        });
    }

    [Test]
    public async Task Should_calculate_lookback_as_days_since_last_run_plus_one()
    {
        // Arrange - ran 5 days ago
        var job = new ScrapeJob
        {
            SearchTerm = "test",
            LastRunUtc = DateTime.UtcNow.AddDays(-5)
        };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _activity.Run(job.Id, null!);

        // Assert - implementation uses Math.Ceiling on TotalDays, then adds 1
        // For -5 days: Ceiling(~5.0) = 5 or 6 depending on fractional part, then +1
        Assert.That(result!.LookbackDays, Is.InRange(6, 7));
    }

    [Test]
    public async Task Should_return_minimum_of_one_lookback_day_when_ran_today()
    {
        // Arrange - ran just now
        var job = new ScrapeJob
        {
            SearchTerm = "test",
            LastRunUtc = DateTime.UtcNow
        };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _activity.Run(job.Id, null!);

        // Assert
        Assert.That(result!.LookbackDays, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task Should_calculate_lookback_when_ran_yesterday()
    {
        // Arrange - ran yesterday
        var job = new ScrapeJob
        {
            SearchTerm = "test",
            LastRunUtc = DateTime.UtcNow.AddDays(-1)
        };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _activity.Run(job.Id, null!);

        // Assert - implementation uses Math.Ceiling on TotalDays, then adds 1
        // For -1 days: Ceiling(~1.0) = 1 or 2, then +1
        Assert.That(result!.LookbackDays, Is.InRange(2, 3));
    }
}
