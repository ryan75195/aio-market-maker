using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ComparablesBatchStage_UnitTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<EtlDbContext> _contextOptions = null!;
    private ServiceProvider _serviceProvider = null!;
    private Mock<IComparablesEtlService> _etlServiceMock = null!;
    private Mock<ILogger<ComparablesBatchStage>> _loggerMock = null!;
    private ComparablesBatchStage _stage = null!;

    private static readonly DateTime BatchStartUtc = new(2026, 2, 27, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TestBatchId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<EtlDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = new EtlDbContext(_contextOptions))
        {
            ctx.Database.EnsureCreated();
        }

        _etlServiceMock = new Mock<IComparablesEtlService>();
        _etlServiceMock
            .Setup(e => e.RunForListings(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComparablesEtlResult(0, 0, 0, 0, 0, 0));

        // Build a real ServiceProvider so IServiceScopeFactory resolves EtlDbContext and IComparablesEtlService
        var services = new ServiceCollection();
        services.AddDbContext<EtlDbContext>(opts => opts.UseSqlite(_connection));
        services.AddScoped<IComparablesEtlService>(_ => _etlServiceMock.Object);
        _serviceProvider = services.BuildServiceProvider();

        _loggerMock = new Mock<ILogger<ComparablesBatchStage>>();

        _stage = new ComparablesBatchStage(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _loggerMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Test]
    public void Name_should_be_finding_comparables()
    {
        Assert.That(_stage.Name, Is.EqualTo("Finding Comparables"));
    }

    [Test]
    public async Task Should_collect_new_active_listings_across_all_runs_and_call_etl()
    {
        // Arrange: 2 jobs, 2 runs in the batch
        SeedScrapeJob(1, "PlayStation 5");
        SeedScrapeJob(2, "Xbox Series X");

        SeedScrapeRun(1, TestBatchId, jobId: 1, startedUtc: BatchStartUtc);
        SeedScrapeRun(2, TestBatchId, jobId: 2, startedUtc: BatchStartUtc.AddMinutes(1));

        // New listings (created AFTER batch start) - should be included
        SeedListing(101, "PS5 Console", "Active", jobId: 1, createdUtc: BatchStartUtc.AddMinutes(5));
        SeedListing(102, "Xbox Series X Console", "Active", jobId: 2, createdUtc: BatchStartUtc.AddMinutes(6));

        // Old listing (created BEFORE batch start) - should be excluded
        SeedListing(103, "PS5 Old Listing", "Active", jobId: 1, createdUtc: BatchStartUtc.AddDays(-1));

        // Sold listing (created AFTER batch start) - should be excluded (not Active)
        SeedListing(104, "PS5 Sold", "Sold", jobId: 1, createdUtc: BatchStartUtc.AddMinutes(7));

        var context = new BatchContext(
            TestBatchId,
            new[] { new ScrapeJobConfig(1, "PlayStation 5"), new ScrapeJobConfig(2, "Xbox Series X") },
            new[] { 1, 2 });

        // Act
        await _stage.Execute(context);

        // Assert: RunForListings called once with exactly the 2 new active listing IDs
        _etlServiceMock.Verify(
            e => e.RunForListings(
                It.Is<IEnumerable<int>>(ids =>
                    ids.Count() == 2 &&
                    ids.Contains(101) &&
                    ids.Contains(102)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Should_skip_when_no_new_listings()
    {
        // Arrange: 1 job, 1 run, but only old listings
        SeedScrapeJob(1, "PlayStation 5");
        SeedScrapeRun(1, TestBatchId, jobId: 1, startedUtc: BatchStartUtc);

        // Old listing (created BEFORE batch start)
        SeedListing(101, "PS5 Old Listing", "Active", jobId: 1, createdUtc: BatchStartUtc.AddDays(-1));

        var context = new BatchContext(
            TestBatchId,
            new[] { new ScrapeJobConfig(1, "PlayStation 5") },
            new[] { 1 });

        // Act
        await _stage.Execute(context);

        // Assert: RunForListings should NOT be called
        _etlServiceMock.Verify(
            e => e.RunForListings(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void SeedScrapeJob(int id, string searchTerm)
    {
        using var ctx = new EtlDbContext(_contextOptions);
        ctx.ScrapeJobs.Add(new ScrapeJob
        {
            Id = id,
            SearchTerm = searchTerm
        });
        ctx.SaveChanges();
    }

    private void SeedScrapeRun(int id, Guid batchId, int jobId, DateTime startedUtc)
    {
        using var ctx = new EtlDbContext(_contextOptions);
        ctx.ScrapeRuns.Add(new ScrapeRun
        {
            Id = id,
            BatchId = batchId,
            JobId = jobId,
            InstanceId = $"instance-{id}",
            TriggerType = "Manual",
            Status = "Running",
            StartedUtc = startedUtc
        });
        ctx.SaveChanges();
    }

    private void SeedListing(int id, string title, string status, int jobId, DateTime createdUtc)
    {
        using var ctx = new EtlDbContext(_contextOptions);
        ctx.Listings.Add(new Listing
        {
            Id = id,
            ListingId = id.ToString(),
            Title = title,
            ListingStatus = status,
            ScrapeJobId = jobId,
            CreatedUtc = createdUtc
        });
        ctx.SaveChanges();
    }
}
