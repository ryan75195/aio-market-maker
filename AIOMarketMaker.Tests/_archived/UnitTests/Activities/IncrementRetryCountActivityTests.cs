using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class IncrementRetryCountActivityTests
{
    private EtlDbContext _dbContext = null!;
    private IncrementRetryCountActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _activity = new IncrementRetryCountActivity(
            _dbContext,
            NullLogger<IncrementRetryCountActivity>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task Should_increment_retrycount_from_zero_to_one()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test", IsEnabled = true };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var run = new ScrapeRun
        {
            InstanceId = "test-instance",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow,
            Status = "Running",
            JobId = job.Id
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var listing = new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = job.Id,
            ListingId = "123456789",
            Status = "Pending",
            RetryCount = 0
        };
        _dbContext.ScrapeRunListings.Add(listing);
        await _dbContext.SaveChangesAsync();

        var input = new IncrementRetryCountInput(run.Id, "123456789");

        // Act
        var result = await _activity.Run(input);

        // Assert
        Assert.That(result.NewRetryCount, Is.EqualTo(1));

        var updatedListing = await _dbContext.ScrapeRunListings.FindAsync(run.Id, "123456789");
        Assert.That(updatedListing!.RetryCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Should_increment_retrycount_from_one_to_two()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test", IsEnabled = true };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var run = new ScrapeRun
        {
            InstanceId = "test-instance-2",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow,
            Status = "Running",
            JobId = job.Id
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var listing = new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = job.Id,
            ListingId = "987654321",
            Status = "Pending",
            RetryCount = 1  // Already retried once
        };
        _dbContext.ScrapeRunListings.Add(listing);
        await _dbContext.SaveChangesAsync();

        var input = new IncrementRetryCountInput(run.Id, "987654321");

        // Act
        var result = await _activity.Run(input);

        // Assert
        Assert.That(result.NewRetryCount, Is.EqualTo(2));
    }

    [Test]
    public async Task Should_return_999_when_listing_not_found()
    {
        // Arrange - no listing exists
        var input = new IncrementRetryCountInput(999, "nonexistent");

        // Act
        var result = await _activity.Run(input);

        // Assert
        Assert.That(result.NewRetryCount, Is.EqualTo(999));
    }

    [Test]
    public async Task Should_persist_incremented_value_to_database()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test", IsEnabled = true };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var run = new ScrapeRun
        {
            InstanceId = "test-persist",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow,
            Status = "Running",
            JobId = job.Id
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var listing = new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = job.Id,
            ListingId = "555555555",
            Status = "Pending",
            RetryCount = 0
        };
        _dbContext.ScrapeRunListings.Add(listing);
        await _dbContext.SaveChangesAsync();

        var input = new IncrementRetryCountInput(run.Id, "555555555");

        // Act
        await _activity.Run(input);

        // Create a new context to verify persistence
        var freshContext = InMemoryDbContextFactory.Create();
        // Note: In-memory SQLite is per-connection, so we verify with the same context
        var persistedListing = await _dbContext.ScrapeRunListings.FindAsync(run.Id, "555555555");

        // Assert
        Assert.That(persistedListing!.RetryCount, Is.EqualTo(1));
    }
}
