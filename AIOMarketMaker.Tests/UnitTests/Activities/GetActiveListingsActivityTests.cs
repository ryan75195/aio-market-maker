using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Functions.Activities;
using AIOMarketMaker.Functions.Contracts;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class GetActiveListingsActivityTests
{
    private EtlDbContext _dbContext = null!;
    private GetActiveListingsActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _activity = new GetActiveListingsActivity(
            _dbContext,
            NullLogger<GetActiveListingsActivity>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task Should_return_only_active_listings_for_job()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        _dbContext.Listings.AddRange(
            new Listing { ListingId = "active1", ScrapeJobId = job.Id, ListingStatus = "Active" },
            new Listing { ListingId = "sold1", ScrapeJobId = job.Id, ListingStatus = "Sold" },
            new Listing { ListingId = "active2", ScrapeJobId = job.Id, ListingStatus = "Active" });
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _activity.Run(new GetActiveListingsInput(job.Id), null!);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(l => l.ListingId), Is.EquivalentTo(new[] { "active1", "active2" }));
    }

    [Test]
    public async Task Should_not_return_listings_from_other_jobs()
    {
        // Arrange
        var job1 = new ScrapeJob { SearchTerm = "job1" };
        var job2 = new ScrapeJob { SearchTerm = "job2" };
        _dbContext.ScrapeJobs.AddRange(job1, job2);
        await _dbContext.SaveChangesAsync();

        _dbContext.Listings.AddRange(
            new Listing { ListingId = "job1-active", ScrapeJobId = job1.Id, ListingStatus = "Active" },
            new Listing { ListingId = "job2-active", ScrapeJobId = job2.Id, ListingStatus = "Active" });
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _activity.Run(new GetActiveListingsInput(job1.Id), null!);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].ListingId, Is.EqualTo("job1-active"));
    }

    [Test]
    public async Task Should_return_empty_list_when_no_active_listings()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        _dbContext.Listings.Add(new Listing { ListingId = "sold", ScrapeJobId = job.Id, ListingStatus = "Sold" });
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _activity.Run(new GetActiveListingsInput(job.Id), null!);

        // Assert
        Assert.That(result, Is.Empty);
    }
}
