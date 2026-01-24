using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Functions.Activities;
using AIOMarketMaker.Functions.Contracts;
using AIOMarketMaker.Tests.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class UpdateSoldListingsActivityTests
{
    private EtlDbContext _dbContext = null!;
    private UpdateSoldListingsActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _activity = new UpdateSoldListingsActivity(
            _dbContext,
            NullLogger<UpdateSoldListingsActivity>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task Should_update_listing_status_to_sold()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var listing = new Listing { ListingId = "123", ScrapeJobId = job.Id, ListingStatus = "Active", Price = 50m };
        _dbContext.Listings.Add(listing);
        await _dbContext.SaveChangesAsync();

        var soldDate = DateTime.UtcNow;
        var input = new UpdateSoldListingsInput(job.Id, new List<ListingData>
        {
            new ListingData("123", null, 75m, null, null, null, "Sold", null, null, null, soldDate, null, null, null)
        });

        // Act
        var count = await _activity.Run(input, null!);

        // Assert
        var updated = await _dbContext.Listings.FirstAsync(l => l.ListingId == "123");
        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(1));
            Assert.That(updated.ListingStatus, Is.EqualTo("Sold"));
            Assert.That(updated.Price, Is.EqualTo(75m));
            Assert.That(updated.EndDateUtc, Is.EqualTo(soldDate));
            Assert.That(updated.UpdatedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Should_create_status_history_record_with_job_scrape_source()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var listing = new Listing { ListingId = "123", ScrapeJobId = job.Id, ListingStatus = "Active" };
        _dbContext.Listings.Add(listing);
        await _dbContext.SaveChangesAsync();

        var input = new UpdateSoldListingsInput(job.Id, new List<ListingData>
        {
            new ListingData("123", null, 100m, null, null, null, "Sold", null, null, null, DateTime.UtcNow, null, null, null)
        });

        // Act
        await _activity.Run(input, null!);

        // Assert
        var history = await _dbContext.ListingStatusHistory.FirstOrDefaultAsync();
        Assert.Multiple(() =>
        {
            Assert.That(history, Is.Not.Null);
            Assert.That(history!.ListingStatus, Is.EqualTo("Sold"));
            Assert.That(history.Price, Is.EqualTo(100m));
            Assert.That(history.Source, Is.EqualTo("JobScrape"));
        });
    }

    [Test]
    public async Task Should_skip_listings_with_empty_id()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var input = new UpdateSoldListingsInput(job.Id, new List<ListingData>
        {
            new ListingData("", null, 100m, null, null, null, "Sold", null, null, null, null, null, null, null)
        });

        // Act
        var count = await _activity.Run(input, null!);

        // Assert
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public async Task Should_skip_listings_not_found_in_database()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var input = new UpdateSoldListingsInput(job.Id, new List<ListingData>
        {
            new ListingData("nonexistent", null, 100m, null, null, null, "Sold", null, null, null, null, null, null, null)
        });

        // Act
        var count = await _activity.Run(input, null!);

        // Assert
        Assert.That(count, Is.EqualTo(0));
    }
}
