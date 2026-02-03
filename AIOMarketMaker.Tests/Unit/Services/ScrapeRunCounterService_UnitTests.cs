using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Moq;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Etl.Services;
using AIOMarketMaker.Tests.Utils;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ScrapeRunCounterService_UnitTests
{
    private EtlDbContext _dbContext = null!;
    private Mock<ILogger<EfCoreScrapeRunCounterService>> _loggerMock = null!;

    [SetUp]
    public void Setup()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _loggerMock = new Mock<ILogger<EfCoreScrapeRunCounterService>>();
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    private EfCoreScrapeRunCounterService CreateService() =>
        new(_dbContext, _loggerMock.Object);

    [Test]
    public async Task Should_increment_ListingsAddedActive_when_status_is_added_and_not_sold()
    {
        _dbContext.ScrapeRuns.Add(new ScrapeRun { Id = 1, Status = "Indexing", CurrentPhase = "Indexing" });
        await _dbContext.SaveChangesAsync();

        await CreateService().Increment(1, "added", "Active");

        var run = await _dbContext.ScrapeRuns.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Multiple(() =>
        {
            Assert.That(run.ListingsProcessed, Is.EqualTo(1));
            Assert.That(run.ListingsAddedActive, Is.EqualTo(1));
            Assert.That(run.ListingsAddedSold, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Should_increment_ListingsAddedSold_when_status_is_added_and_sold()
    {
        _dbContext.ScrapeRuns.Add(new ScrapeRun { Id = 1, Status = "Indexing", CurrentPhase = "Indexing" });
        await _dbContext.SaveChangesAsync();

        await CreateService().Increment(1, "added", "Sold");

        var run = await _dbContext.ScrapeRuns.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Multiple(() =>
        {
            Assert.That(run.ListingsProcessed, Is.EqualTo(1));
            Assert.That(run.ListingsAddedSold, Is.EqualTo(1));
            Assert.That(run.ListingsAddedActive, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Should_increment_ListingsUpdated_when_status_is_updated()
    {
        _dbContext.ScrapeRuns.Add(new ScrapeRun { Id = 1, Status = "Indexing", CurrentPhase = "Indexing" });
        await _dbContext.SaveChangesAsync();

        await CreateService().Increment(1, "updated");

        var run = await _dbContext.ScrapeRuns.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.That(run.ListingsUpdated, Is.EqualTo(1));
    }

    [Test]
    public async Task Should_increment_ListingsSkipped_when_status_is_skipped()
    {
        _dbContext.ScrapeRuns.Add(new ScrapeRun { Id = 1, Status = "Indexing", CurrentPhase = "Indexing" });
        await _dbContext.SaveChangesAsync();

        await CreateService().Increment(1, "skipped");

        var run = await _dbContext.ScrapeRuns.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.That(run.ListingsSkipped, Is.EqualTo(1));
    }

    [Test]
    public async Task Should_increment_ListingsFailed_when_status_is_failed()
    {
        _dbContext.ScrapeRuns.Add(new ScrapeRun { Id = 1, Status = "Indexing", CurrentPhase = "Indexing" });
        await _dbContext.SaveChangesAsync();

        await CreateService().Increment(1, "failed");

        var run = await _dbContext.ScrapeRuns.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.That(run.ListingsFailed, Is.EqualTo(1));
    }

    [Test]
    public async Task Should_mark_completed_when_last_listing_processed()
    {
        _dbContext.ScrapeRuns.Add(new ScrapeRun
        {
            Id = 1, Status = "Indexing", CurrentPhase = "Indexing",
            TotalListingsFound = 3, ListingsFilteredPreQueue = 1,
            ListingsProcessed = 1
        });
        await _dbContext.SaveChangesAsync();

        await CreateService().Increment(1, "added", "Active");

        var run = await _dbContext.ScrapeRuns.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("Completed"));
            Assert.That(run.CurrentPhase, Is.EqualTo("Completed"));
            Assert.That(run.CompletedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Should_not_mark_completed_when_more_listings_remain()
    {
        _dbContext.ScrapeRuns.Add(new ScrapeRun
        {
            Id = 1, Status = "Indexing", CurrentPhase = "Indexing",
            TotalListingsFound = 5, ListingsFilteredPreQueue = 0,
            ListingsProcessed = 1
        });
        await _dbContext.SaveChangesAsync();

        await CreateService().Increment(1, "added", "Active");

        var run = await _dbContext.ScrapeRuns.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.That(run.Status, Is.EqualTo("Indexing"));
    }
}
