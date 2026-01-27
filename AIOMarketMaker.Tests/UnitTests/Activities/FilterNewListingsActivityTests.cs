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
    public async Task Should_filter_out_sold_listings_globally()
    {
        // Arrange - listing exists in different job but is Sold
        var job1 = new ScrapeJob { SearchTerm = "job1" };
        var job2 = new ScrapeJob { SearchTerm = "job2" };
        _dbContext.ScrapeJobs.AddRange(job1, job2);
        await _dbContext.SaveChangesAsync();

        _dbContext.Listings.Add(new Listing
        {
            ListingId = "222",
            ScrapeJobId = job1.Id,
            ListingStatus = "Sold"
        });
        await _dbContext.SaveChangesAsync();

        var input = new FilterNewListingsInput(job2.Id, new List<string> { "111", "222", "333" });

        // Act
        var result = await _activity.Run(input, null!);

        // Assert - "222" should be filtered out even though it's from a different job
        Assert.That(result, Is.EquivalentTo(new[] { "111", "333" }));
    }

    [Test]
    public async Task Should_not_filter_active_listings_from_different_job()
    {
        // Arrange - listing exists in different job but is Active
        var job1 = new ScrapeJob { SearchTerm = "job1" };
        var job2 = new ScrapeJob { SearchTerm = "job2" };
        _dbContext.ScrapeJobs.AddRange(job1, job2);
        await _dbContext.SaveChangesAsync();

        _dbContext.Listings.Add(new Listing
        {
            ListingId = "222",
            ScrapeJobId = job1.Id,
            ListingStatus = "Active"
        });
        await _dbContext.SaveChangesAsync();

        var input = new FilterNewListingsInput(job2.Id, new List<string> { "111", "222", "333" });

        // Act
        var result = await _activity.Run(input, null!);

        // Assert - "222" should NOT be filtered (active listings need monitoring)
        Assert.That(result, Is.EquivalentTo(new[] { "111", "222", "333" }));
    }

    [Test]
    public async Task Should_filter_ended_listings_globally()
    {
        // Arrange
        var job1 = new ScrapeJob { SearchTerm = "job1" };
        var job2 = new ScrapeJob { SearchTerm = "job2" };
        _dbContext.ScrapeJobs.AddRange(job1, job2);
        await _dbContext.SaveChangesAsync();

        _dbContext.Listings.Add(new Listing
        {
            ListingId = "222",
            ScrapeJobId = job1.Id,
            ListingStatus = "Ended"
        });
        await _dbContext.SaveChangesAsync();

        var input = new FilterNewListingsInput(job2.Id, new List<string> { "111", "222", "333" });

        // Act
        var result = await _activity.Run(input, null!);

        // Assert - Ended listings are terminal, filter globally
        Assert.That(result, Is.EquivalentTo(new[] { "111", "333" }));
    }

    [Test]
    public async Task Should_return_empty_list_when_all_listings_are_terminal()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        _dbContext.Listings.AddRange(
            new Listing { ListingId = "111", ScrapeJobId = job.Id, ListingStatus = "Sold" },
            new Listing { ListingId = "222", ScrapeJobId = job.Id, ListingStatus = "Ended" });
        await _dbContext.SaveChangesAsync();

        var input = new FilterNewListingsInput(job.Id, new List<string> { "111", "222" });

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.That(result, Is.Empty);
    }
}
