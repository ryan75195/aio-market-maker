using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Functions.Activities;
using AIOMarketMaker.Functions.Contracts;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class FilterNewListingsActivityTests
{
    private EtlDbContext _dbContext = null!;
    private FilterNewListingsActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _activity = new FilterNewListingsActivity(
            _dbContext,
            NullLogger<FilterNewListingsActivity>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task Should_return_all_ids_when_no_existing_listings()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var input = new FilterNewListingsInput(job.Id, new List<string> { "111", "222", "333" });

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.That(result, Is.EquivalentTo(new[] { "111", "222", "333" }));
    }

    [Test]
    public async Task Should_filter_out_existing_listings_for_same_job()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        _dbContext.Listings.Add(new Listing { ListingId = "222", ScrapeJobId = job.Id });
        await _dbContext.SaveChangesAsync();

        var input = new FilterNewListingsInput(job.Id, new List<string> { "111", "222", "333" });

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.That(result, Is.EquivalentTo(new[] { "111", "333" }));
    }

    [Test]
    public async Task Should_not_filter_listings_from_different_job()
    {
        // Arrange
        var job1 = new ScrapeJob { SearchTerm = "job1" };
        var job2 = new ScrapeJob { SearchTerm = "job2" };
        _dbContext.ScrapeJobs.AddRange(job1, job2);
        await _dbContext.SaveChangesAsync();

        _dbContext.Listings.Add(new Listing { ListingId = "222", ScrapeJobId = job1.Id });
        await _dbContext.SaveChangesAsync();

        var input = new FilterNewListingsInput(job2.Id, new List<string> { "111", "222", "333" });

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.That(result, Is.EquivalentTo(new[] { "111", "222", "333" }));
    }

    [Test]
    public async Task Should_return_empty_list_when_all_listings_exist()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        _dbContext.Listings.AddRange(
            new Listing { ListingId = "111", ScrapeJobId = job.Id },
            new Listing { ListingId = "222", ScrapeJobId = job.Id });
        await _dbContext.SaveChangesAsync();

        var input = new FilterNewListingsInput(job.Id, new List<string> { "111", "222" });

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.That(result, Is.Empty);
    }
}
