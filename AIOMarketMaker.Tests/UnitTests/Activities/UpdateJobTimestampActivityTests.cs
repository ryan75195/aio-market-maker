using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Functions.Activities;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class UpdateJobTimestampActivityTests
{
    private EtlDbContext _dbContext = null!;
    private UpdateJobTimestampActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _activity = new UpdateJobTimestampActivity(
            _dbContext,
            NullLogger<UpdateJobTimestampActivity>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task Should_update_job_timestamp_to_current_utc()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test", LastRunUtc = null };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var beforeUpdate = DateTime.UtcNow;

        // Act
        await _activity.Run(job.Id, null!);

        // Assert
        var afterUpdate = DateTime.UtcNow;
        var updatedJob = await _dbContext.ScrapeJobs.FindAsync(job.Id);

        Assert.Multiple(() =>
        {
            Assert.That(updatedJob!.LastRunUtc, Is.Not.Null);
            Assert.That(updatedJob.LastRunUtc, Is.GreaterThanOrEqualTo(beforeUpdate));
            Assert.That(updatedJob.LastRunUtc, Is.LessThanOrEqualTo(afterUpdate));
        });
    }

    [Test]
    public async Task Should_not_throw_when_job_not_found()
    {
        // Act & Assert - should not throw
        Assert.DoesNotThrowAsync(async () => await _activity.Run(999, null!));
    }
}
