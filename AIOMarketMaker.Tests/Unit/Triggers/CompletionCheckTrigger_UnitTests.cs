using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Functions.Worker;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Etl.Triggers;
using AIOMarketMaker.Tests.Utils;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Tests.Unit.Triggers;

[TestFixture]
[Category("Unit")]
public class CompletionCheckTrigger_UnitTests
{
    private Mock<ILogger<CompletionCheckTrigger>> _loggerMock;
    private EtlDbContext _dbContext;

    [SetUp]
    public void SetUp()
    {
        _loggerMock = new Mock<ILogger<CompletionCheckTrigger>>();
        _dbContext = InMemoryDbContextFactory.Create();
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext?.Dispose();
    }

    [Test]
    public void Should_construct_with_all_dependencies()
    {
        // Act
        var trigger = new CompletionCheckTrigger(
            _loggerMock.Object,
            _dbContext);

        // Assert
        Assert.That(trigger, Is.Not.Null);
    }

    [Test]
    public async Task Run_should_mark_run_as_completed_when_all_listings_processed()
    {
        // Arrange
        var scrapeRun = new ScrapeRun
        {
            JobId = 1,
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-10),
            Status = "Running",
            CurrentPhase = "Indexing",
            TotalListingsFound = 5,
            ListingsProcessed = 5
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        var trigger = new CompletionCheckTrigger(
            _loggerMock.Object,
            _dbContext);

        // Act
        await trigger.Run(null!);

        // Assert
        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(scrapeRun.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updatedRun!.Status, Is.EqualTo("Completed"));
            Assert.That(updatedRun.CurrentPhase, Is.EqualTo("Completed"));
            Assert.That(updatedRun.CompletedUtc, Is.Not.Null);
            Assert.That(updatedRun.CompletedUtc, Is.GreaterThan(DateTime.UtcNow.AddMinutes(-1)));
        });
    }

    [Test]
    public async Task Run_should_mark_indexing_status_run_as_completed_when_all_listings_processed()
    {
        // Arrange - Run with Status = "Indexing" instead of "Running"
        var scrapeRun = new ScrapeRun
        {
            JobId = 1,
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-10),
            Status = "Indexing",
            CurrentPhase = "Indexing",
            TotalListingsFound = 3,
            ListingsProcessed = 3
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        var trigger = new CompletionCheckTrigger(
            _loggerMock.Object,
            _dbContext);

        // Act
        await trigger.Run(null!);

        // Assert
        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(scrapeRun.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updatedRun!.Status, Is.EqualTo("Completed"));
            Assert.That(updatedRun.CurrentPhase, Is.EqualTo("Completed"));
            Assert.That(updatedRun.CompletedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Run_should_not_modify_incomplete_runs()
    {
        // Arrange - Run with fewer processed than found
        var incompleteRun = new ScrapeRun
        {
            JobId = 1,
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-10),
            Status = "Running",
            CurrentPhase = "Indexing",
            TotalListingsFound = 10,
            ListingsProcessed = 5  // Only half processed
        };
        _dbContext.ScrapeRuns.Add(incompleteRun);
        await _dbContext.SaveChangesAsync();

        var trigger = new CompletionCheckTrigger(
            _loggerMock.Object,
            _dbContext);

        // Act
        await trigger.Run(null!);

        // Assert
        var unchangedRun = await _dbContext.ScrapeRuns.FindAsync(incompleteRun.Id);
        Assert.Multiple(() =>
        {
            Assert.That(unchangedRun!.Status, Is.EqualTo("Running"));
            Assert.That(unchangedRun.CurrentPhase, Is.EqualTo("Indexing"));
            Assert.That(unchangedRun.CompletedUtc, Is.Null);
        });
    }

    [Test]
    public async Task Run_should_not_modify_runs_with_zero_listings_found()
    {
        // Arrange - Run with no listings found
        var emptyRun = new ScrapeRun
        {
            JobId = 1,
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-10),
            Status = "Running",
            CurrentPhase = "Indexing",
            TotalListingsFound = 0,  // No listings found
            ListingsProcessed = 0
        };
        _dbContext.ScrapeRuns.Add(emptyRun);
        await _dbContext.SaveChangesAsync();

        var trigger = new CompletionCheckTrigger(
            _loggerMock.Object,
            _dbContext);

        // Act
        await trigger.Run(null!);

        // Assert - Should NOT be marked complete (TotalListingsFound must be > 0)
        var unchangedRun = await _dbContext.ScrapeRuns.FindAsync(emptyRun.Id);
        Assert.Multiple(() =>
        {
            Assert.That(unchangedRun!.Status, Is.EqualTo("Running"));
            Assert.That(unchangedRun.CurrentPhase, Is.EqualTo("Indexing"));
            Assert.That(unchangedRun.CompletedUtc, Is.Null);
        });
    }

    [Test]
    public async Task Run_should_not_modify_already_completed_runs()
    {
        // Arrange - Already completed run
        var completedTime = DateTime.UtcNow.AddMinutes(-5);
        var completedRun = new ScrapeRun
        {
            JobId = 1,
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-10),
            CompletedUtc = completedTime,
            Status = "Completed",
            CurrentPhase = "Completed",
            TotalListingsFound = 5,
            ListingsProcessed = 5
        };
        _dbContext.ScrapeRuns.Add(completedRun);
        await _dbContext.SaveChangesAsync();

        var trigger = new CompletionCheckTrigger(
            _loggerMock.Object,
            _dbContext);

        // Act
        await trigger.Run(null!);

        // Assert - CompletedUtc should not be changed
        var unchangedRun = await _dbContext.ScrapeRuns.FindAsync(completedRun.Id);
        Assert.That(unchangedRun!.CompletedUtc, Is.EqualTo(completedTime));
    }

    [Test]
    public async Task Run_should_not_modify_runs_in_wrong_phase()
    {
        // Arrange - Run in "Searching" phase (not "Indexing")
        var searchingRun = new ScrapeRun
        {
            JobId = 1,
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-10),
            Status = "Running",
            CurrentPhase = "Searching",  // Not "Indexing"
            TotalListingsFound = 5,
            ListingsProcessed = 5
        };
        _dbContext.ScrapeRuns.Add(searchingRun);
        await _dbContext.SaveChangesAsync();

        var trigger = new CompletionCheckTrigger(
            _loggerMock.Object,
            _dbContext);

        // Act
        await trigger.Run(null!);

        // Assert - Should NOT be marked complete (wrong phase)
        var unchangedRun = await _dbContext.ScrapeRuns.FindAsync(searchingRun.Id);
        Assert.Multiple(() =>
        {
            Assert.That(unchangedRun!.Status, Is.EqualTo("Running"));
            Assert.That(unchangedRun.CurrentPhase, Is.EqualTo("Searching"));
            Assert.That(unchangedRun.CompletedUtc, Is.Null);
        });
    }

    [Test]
    public async Task Run_should_mark_run_as_completed_when_ListingsFilteredPreQueue_accounts_for_difference()
    {
        // Arrange - Run where TotalListingsFound includes pre-filtered listings
        // TotalListingsFound = 1665 (all found in search)
        // ListingsFilteredPreQueue = 709 (terminal status - Sold/Ended/OutOfStock)
        // Actual to process = 1665 - 709 = 956
        // ListingsProcessed = 956 (all actual listings processed)
        var scrapeRun = new ScrapeRun
        {
            JobId = 1,
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-10),
            Status = "Indexing",
            CurrentPhase = "Indexing",
            TotalListingsFound = 1665,
            ListingsFilteredPreQueue = 709,
            ListingsProcessed = 956  // 1665 - 709 = 956
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        var trigger = new CompletionCheckTrigger(
            _loggerMock.Object,
            _dbContext);

        // Act
        await trigger.Run(null!);

        // Assert
        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(scrapeRun.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updatedRun!.Status, Is.EqualTo("Completed"),
                "Run should be marked Completed when ListingsProcessed equals actual listings to process (TotalFound - FilteredPreQueue)");
            Assert.That(updatedRun.CurrentPhase, Is.EqualTo("Completed"));
            Assert.That(updatedRun.CompletedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Run_should_handle_multiple_runs_and_only_complete_eligible_ones()
    {
        // Arrange - Mix of eligible and ineligible runs
        var eligibleRun = new ScrapeRun
        {
            JobId = 1,
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-10),
            Status = "Running",
            CurrentPhase = "Indexing",
            TotalListingsFound = 5,
            ListingsProcessed = 5  // All processed - eligible
        };

        var incompleteRun = new ScrapeRun
        {
            JobId = 2,
            TriggerType = "Nightly",
            StartedUtc = DateTime.UtcNow.AddMinutes(-10),
            Status = "Running",
            CurrentPhase = "Indexing",
            TotalListingsFound = 10,
            ListingsProcessed = 3  // Not all processed - ineligible
        };

        var wrongPhaseRun = new ScrapeRun
        {
            JobId = 3,
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-10),
            Status = "Running",
            CurrentPhase = "Searching",  // Wrong phase - ineligible
            TotalListingsFound = 2,
            ListingsProcessed = 2
        };

        _dbContext.ScrapeRuns.AddRange(eligibleRun, incompleteRun, wrongPhaseRun);
        await _dbContext.SaveChangesAsync();

        var trigger = new CompletionCheckTrigger(
            _loggerMock.Object,
            _dbContext);

        // Act
        await trigger.Run(null!);

        // Assert
        var runs = await _dbContext.ScrapeRuns.ToListAsync();
        Assert.Multiple(() =>
        {
            // Eligible run should be completed
            var updatedEligible = runs.First(r => r.JobId == 1);
            Assert.That(updatedEligible.Status, Is.EqualTo("Completed"));
            Assert.That(updatedEligible.CurrentPhase, Is.EqualTo("Completed"));
            Assert.That(updatedEligible.CompletedUtc, Is.Not.Null);

            // Incomplete run should remain unchanged
            var unchangedIncomplete = runs.First(r => r.JobId == 2);
            Assert.That(unchangedIncomplete.Status, Is.EqualTo("Running"));
            Assert.That(unchangedIncomplete.CompletedUtc, Is.Null);

            // Wrong phase run should remain unchanged
            var unchangedWrongPhase = runs.First(r => r.JobId == 3);
            Assert.That(unchangedWrongPhase.Status, Is.EqualTo("Running"));
            Assert.That(unchangedWrongPhase.CompletedUtc, Is.Null);
        });
    }
}
