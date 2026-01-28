using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;
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
    public async Task Should_not_mark_complete_when_phase_is_not_completed_even_if_total_is_zero()
    {
        // Arrange - This is the bug scenario: TotalListingsFound=0 but CurrentPhase="Searching"
        // The old logic would mark this as Completed, which is wrong
        var run = new ScrapeRun
        {
            InstanceId = "test-instance-phase-check",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-5),
            Status = "Running",
            CurrentPhase = "Searching",  // NOT "Completed"
            TotalListingsFound = 0,      // Would trigger old bug
            ListingsProcessed = 0
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var input = new UpdateScrapeRunInput(
            InstanceId: "test-instance-phase-check",
            Success: true,
            ListingsAdded: 0,
            ListingsSkipped: 0,
            ErrorMessage: null);

        // Act
        await _activity.Run(input);

        // Assert - Status should remain "Running" because phase is not "Completed"
        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updatedRun!.Status, Is.EqualTo("Running"),
                "Status should remain Running when CurrentPhase is not Completed");
            Assert.That(updatedRun.CompletedUtc, Is.Null,
                "CompletedUtc should not be set when phase is incomplete");
        });
    }

    [Test]
    public async Task Should_mark_complete_when_phase_is_completed_and_total_is_zero()
    {
        // Arrange - Legitimate completion: phase IS "Completed" and no listings to process
        var run = new ScrapeRun
        {
            InstanceId = "test-instance-legit-complete",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-5),
            Status = "Running",
            CurrentPhase = "Completed",  // Phase indicates completion is valid
            TotalListingsFound = 0,
            ListingsProcessed = 0
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var input = new UpdateScrapeRunInput(
            InstanceId: "test-instance-legit-complete",
            Success: true,
            ListingsAdded: 0,
            ListingsSkipped: 0,
            ErrorMessage: null);

        // Act
        await _activity.Run(input);

        // Assert - Should be marked Completed because phase confirms it
        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updatedRun!.Status, Is.EqualTo("Completed"));
            Assert.That(updatedRun.CompletedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Should_not_mark_complete_when_phase_is_indexing_and_listings_still_pending()
    {
        // Arrange - Bug scenario from docs: TotalListingsFound=1047, ListingsProcessed=21, CurrentPhase="Searching"
        var run = new ScrapeRun
        {
            InstanceId = "test-instance-partial-progress",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-5),
            Status = "Running",
            CurrentPhase = "Indexing",   // Still processing
            TotalListingsFound = 1047,
            ListingsProcessed = 21       // Only 21 of 1047 processed
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var input = new UpdateScrapeRunInput(
            InstanceId: "test-instance-partial-progress",
            Success: true,
            ListingsAdded: 0,
            ListingsSkipped: 0,
            ErrorMessage: null);

        // Act
        await _activity.Run(input);

        // Assert - Should remain Running
        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updatedRun!.Status, Is.EqualTo("Running"),
                "Status should remain Running when listings are still being processed");
            Assert.That(updatedRun.CompletedUtc, Is.Null);
        });
    }

    [Test]
    public async Task Should_mark_complete_when_phase_is_completed_and_all_listings_processed()
    {
        // Arrange - Legitimate completion with all listings processed
        var run = new ScrapeRun
        {
            InstanceId = "test-instance-all-processed",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-5),
            Status = "Running",
            CurrentPhase = "Completed",
            TotalListingsFound = 100,
            ListingsProcessed = 100  // All processed
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var input = new UpdateScrapeRunInput(
            InstanceId: "test-instance-all-processed",
            Success: true,
            ListingsAdded: 100,
            ListingsSkipped: 0,
            ErrorMessage: null);

        // Act
        await _activity.Run(input);

        // Assert
        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updatedRun!.Status, Is.EqualTo("Completed"));
            Assert.That(updatedRun.CompletedUtc, Is.Not.Null);
            Assert.That(updatedRun.ListingsAdded, Is.EqualTo(100));
        });
    }

    [Test]
    public async Task Should_mark_failed_regardless_of_phase_when_success_is_false()
    {
        // Arrange - Failed runs should always be marked Failed
        var run = new ScrapeRun
        {
            InstanceId = "test-instance-failed",
            TriggerType = "Nightly",
            StartedUtc = DateTime.UtcNow.AddMinutes(-10),
            Status = "Running",
            CurrentPhase = "Searching"  // Could be any phase
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var input = new UpdateScrapeRunInput(
            InstanceId: "test-instance-failed",
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
}
