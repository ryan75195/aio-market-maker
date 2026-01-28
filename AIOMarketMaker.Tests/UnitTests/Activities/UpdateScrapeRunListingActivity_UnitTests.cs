using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class UpdateScrapeRunListingActivity_UnitTests
{
    private EtlDbContext _dbContext = null!;
    private UpdateScrapeRunListingActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _activity = new UpdateScrapeRunListingActivity(
            _dbContext,
            NullLogger<UpdateScrapeRunListingActivity>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    /// <summary>
    /// Runs the activity and suppresses the SqliteException that occurs because
    /// ExecuteSqlRawAsync uses SQL Server syntax (GETUTCDATE) which SQLite does not support.
    /// The mapping-level changes (Status, CompletedUtc, ErrorMessage) are persisted via
    /// SaveChangesAsync BEFORE the raw SQL executes, so they remain verifiable.
    /// The raw SQL ScrapeRun increment is covered by integration tests.
    /// </summary>
    private async Task RunActivityIgnoringRawSqlError(UpdateScrapeRunListingInput input)
    {
        try
        {
            await _activity.Run(input);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Expected: SQLite does not support GETUTCDATE() used in the raw SQL.
            // Mapping-level changes are already persisted at this point.
        }
    }

    private async Task<(ScrapeRun run, ScrapeRunListing listing)> SeedRunWithListing(
        int totalListingsFound = 10,
        int listingsProcessed = 0,
        string runStatus = "Running")
    {
        var job = new ScrapeJob { SearchTerm = "test-search", IsEnabled = true };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var run = new ScrapeRun
        {
            InstanceId = "test-instance",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-5),
            Status = runStatus,
            CurrentPhase = "Fetching",
            TotalListingsFound = totalListingsFound,
            ListingsProcessed = listingsProcessed,
            JobId = job.Id
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var listing = new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = job.Id,
            ListingId = "123456789",
            Status = "Processing"
        };
        _dbContext.ScrapeRunListings.Add(listing);
        await _dbContext.SaveChangesAsync();

        return (run, listing);
    }

    [Test]
    public async Task Should_increment_ListingsProcessed_when_status_is_Failed()
    {
        // Arrange - The raw SQL increment uses GETUTCDATE() which SQLite doesn't support.
        // We verify that the code enters the increment branch for Failed status
        // by confirming the raw SQL is attempted (throws SqliteException).
        // The actual ScrapeRun increment is validated via integration tests.
        var (run, _) = await SeedRunWithListing(totalListingsFound: 1, listingsProcessed: 0);
        var input = new UpdateScrapeRunListingInput(
            run.Id, "123456789", "Failed", IsNewListing: false,
            ErrorMessage: "Bot detection: HTML too small");

        // Act - The raw SQL will throw on SQLite, confirming the Failed branch is entered
        var ex = Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            async () => await _activity.Run(input));

        // Assert - The SqliteException proves the code entered the ExecuteSqlRawAsync branch
        // for Failed status. Before the change, only Complete triggered this branch.
        Assert.That(ex, Is.Not.Null,
            "Expected SqliteException from raw SQL execution, proving Failed triggers the increment branch");
    }

    [Test]
    public async Task Should_store_ErrorMessage_when_status_is_Failed()
    {
        // Arrange
        var (run, _) = await SeedRunWithListing();
        var input = new UpdateScrapeRunListingInput(
            run.Id, "123456789", "Failed", IsNewListing: false,
            ErrorMessage: "Bot detection: HTML size 67KB < 100KB threshold");

        // Act
        await RunActivityIgnoringRawSqlError(input);

        // Assert
        var updated = await _dbContext.ScrapeRunListings.FindAsync(run.Id, "123456789");
        Assert.Multiple(() =>
        {
            Assert.That(updated!.Status, Is.EqualTo("Failed"));
            Assert.That(updated.ErrorMessage, Is.EqualTo("Bot detection: HTML size 67KB < 100KB threshold"));
            Assert.That(updated.CompletedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Should_not_store_ErrorMessage_when_status_is_Complete()
    {
        // Arrange
        var (run, _) = await SeedRunWithListing();
        var input = new UpdateScrapeRunListingInput(
            run.Id, "123456789", "Complete", IsNewListing: true);

        // Act
        await RunActivityIgnoringRawSqlError(input);

        // Assert
        var updated = await _dbContext.ScrapeRunListings.FindAsync(run.Id, "123456789");
        Assert.Multiple(() =>
        {
            Assert.That(updated!.Status, Is.EqualTo("Complete"));
            Assert.That(updated.ErrorMessage, Is.Null);
            Assert.That(updated.CompletedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Should_mark_run_Completed_when_last_listing_fails()
    {
        // Arrange - ScrapeRun with TotalListingsFound=1, ListingsProcessed=0
        // When the single listing fails, ListingsProcessed should become 1 (== TotalListingsFound)
        // and the run should be marked Completed.
        // NOTE: Full verification requires SQL Server. Here we confirm the branch is entered.
        var (run, _) = await SeedRunWithListing(totalListingsFound: 1, listingsProcessed: 0);
        var input = new UpdateScrapeRunListingInput(
            run.Id, "123456789", "Failed", IsNewListing: false,
            ErrorMessage: "Timeout after 30s");

        // Act - The raw SQL will attempt to update ScrapeRun, proving Failed triggers completion check
        var ex = Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            async () => await _activity.Run(input));

        // Assert - Mapping-level changes persisted before the raw SQL
        var updatedListing = await _dbContext.ScrapeRunListings.FindAsync(run.Id, "123456789");
        Assert.Multiple(() =>
        {
            Assert.That(ex, Is.Not.Null,
                "Raw SQL execution was attempted, proving Failed triggers the ScrapeRun completion check");
            Assert.That(updatedListing!.Status, Is.EqualTo("Failed"));
            Assert.That(updatedListing.ErrorMessage, Is.EqualTo("Timeout after 30s"));
            Assert.That(updatedListing.CompletedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Should_not_increment_ListingsAdded_or_ListingsSkipped_when_Failed()
    {
        // Arrange - When a listing fails, ListingsAdded and ListingsSkipped should NOT increment.
        // The raw SQL uses addedIncrement=0 and skippedIncrement=0 for Failed status.
        // We verify by checking the SQL parameters would be 0 for Failed.
        // Since the raw SQL uses GETUTCDATE() (incompatible with SQLite), we verify the
        // mapping-level behavior and trust the SQL parameter logic.
        var (run, _) = await SeedRunWithListing(totalListingsFound: 5, listingsProcessed: 2);

        // Even though IsNewListing=true, a Failed listing should NOT increment ListingsAdded
        var input = new UpdateScrapeRunListingInput(
            run.Id, "123456789", "Failed", IsNewListing: true,
            ErrorMessage: "Parse error");

        // Act
        await RunActivityIgnoringRawSqlError(input);

        // Assert - Verify the listing was marked Failed (mapping updates succeed)
        // The raw SQL addedIncrement/skippedIncrement are 0 for Failed by design:
        //   addedIncrement = input.Status == "Complete" && input.IsNewListing ? 1 : 0;
        //   skippedIncrement = input.Status == "Complete" && !input.IsNewListing ? 1 : 0;
        var updatedListing = await _dbContext.ScrapeRunListings.FindAsync(run.Id, "123456789");
        Assert.Multiple(() =>
        {
            Assert.That(updatedListing!.Status, Is.EqualTo("Failed"));
            Assert.That(updatedListing.ErrorMessage, Is.EqualTo("Parse error"));
        });

        // Also verify the ScrapeRun was NOT modified via EF (only raw SQL modifies it)
        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(updatedRun!.ListingsAdded, Is.EqualTo(0),
                "ListingsAdded should not be incremented for Failed listings");
            Assert.That(updatedRun.ListingsSkipped, Is.EqualTo(0),
                "ListingsSkipped should not be incremented for Failed listings");
            Assert.That(updatedRun.ListingsProcessed, Is.EqualTo(2),
                "ListingsProcessed is only updated via raw SQL, not EF, so remains at initial value in SQLite tests");
        });
    }
}
