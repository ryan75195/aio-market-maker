using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Functions.Activities;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class UpdateScrapeRunActivityTests
{
    private EtlDbContext _dbContext = null!;
    private UpdateScrapeRunActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _activity = new UpdateScrapeRunActivity(
            _dbContext,
            NullLogger<UpdateScrapeRunActivity>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task Should_update_run_to_completed_when_success_is_true()
    {
        // Arrange
        var run = new ScrapeRun
        {
            InstanceId = "test-instance-123",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-5),
            Status = "Running"
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var input = new UpdateScrapeRunInput(
            InstanceId: "test-instance-123",
            Success: true,
            ListingsAdded: 42,
            ListingsSkipped: 5,
            ErrorMessage: null);

        // Act
        await _activity.Run(input);

        // Assert
        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updatedRun!.Status, Is.EqualTo("Completed"));
            Assert.That(updatedRun.CompletedUtc, Is.Not.Null);
            Assert.That(updatedRun.ListingsAdded, Is.EqualTo(42));
            Assert.That(updatedRun.ListingsSkipped, Is.EqualTo(5));
            Assert.That(updatedRun.ErrorMessage, Is.Null);
        });
    }

    [Test]
    public async Task Should_update_run_to_failed_when_success_is_false()
    {
        // Arrange
        var run = new ScrapeRun
        {
            InstanceId = "test-instance-456",
            TriggerType = "Nightly",
            StartedUtc = DateTime.UtcNow.AddMinutes(-10),
            Status = "Running"
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var input = new UpdateScrapeRunInput(
            InstanceId: "test-instance-456",
            Success: false,
            ListingsAdded: 10,
            ListingsSkipped: 0,
            ErrorMessage: "Job 1: Bot detection; Job 2: Timeout");

        // Act
        await _activity.Run(input);

        // Assert
        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updatedRun!.Status, Is.EqualTo("Failed"));
            Assert.That(updatedRun.CompletedUtc, Is.Not.Null);
            Assert.That(updatedRun.ListingsAdded, Is.EqualTo(10));
            Assert.That(updatedRun.ErrorMessage, Is.EqualTo("Job 1: Bot detection; Job 2: Timeout"));
        });
    }

    [Test]
    public async Task Should_not_throw_when_run_not_found()
    {
        // Arrange
        var input = new UpdateScrapeRunInput(
            InstanceId: "non-existent-instance",
            Success: true,
            ListingsAdded: 0,
            ListingsSkipped: 0,
            ErrorMessage: null);

        // Act & Assert - should not throw
        Assert.DoesNotThrowAsync(async () => await _activity.Run(input));
    }

    [Test]
    public async Task Should_preserve_trigger_type_when_updating()
    {
        // Arrange
        var run = new ScrapeRun
        {
            InstanceId = "nightly-run-789",
            TriggerType = "Nightly",
            StartedUtc = DateTime.UtcNow.AddMinutes(-15),
            Status = "Running"
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var input = new UpdateScrapeRunInput(
            InstanceId: "nightly-run-789",
            Success: true,
            ListingsAdded: 100,
            ListingsSkipped: 20,
            ErrorMessage: null);

        // Act
        await _activity.Run(input);

        // Assert
        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.That(updatedRun!.TriggerType, Is.EqualTo("Nightly"));
    }
}
