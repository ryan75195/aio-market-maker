using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Functions.Activities;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class GetEnabledJobsActivityTests
{
    private EtlDbContext _dbContext = null!;
    private GetEnabledJobsActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _activity = new GetEnabledJobsActivity(
            _dbContext,
            NullLogger<GetEnabledJobsActivity>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task Should_return_only_enabled_jobs()
    {
        // Arrange
        _dbContext.ScrapeJobs.AddRange(
            new ScrapeJob { SearchTerm = "enabled1", IsEnabled = true },
            new ScrapeJob { SearchTerm = "disabled", IsEnabled = false },
            new ScrapeJob { SearchTerm = "enabled2", IsEnabled = true });
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _activity.Run(null, null!);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(j => j.SearchTerm), Is.EquivalentTo(new[] { "enabled1", "enabled2" }));
    }

    [Test]
    public async Task Should_return_empty_list_when_no_enabled_jobs()
    {
        // Arrange
        _dbContext.ScrapeJobs.Add(new ScrapeJob { SearchTerm = "disabled", IsEnabled = false });
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _activity.Run(null, null!);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Should_return_empty_list_when_no_jobs_exist()
    {
        // Act
        var result = await _activity.Run(null, null!);

        // Assert
        Assert.That(result, Is.Empty);
    }
}
