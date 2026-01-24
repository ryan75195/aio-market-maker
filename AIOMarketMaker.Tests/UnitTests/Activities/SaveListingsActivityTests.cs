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
public class SaveListingsActivityTests
{
    private EtlDbContext _dbContext = null!;
    private SaveListingsActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _activity = new SaveListingsActivity(
            _dbContext,
            NullLogger<SaveListingsActivity>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task Should_save_listings_to_database()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var input = new SaveListingsInput(job.Id, new List<ListingData>
        {
            new ListingData(
                ListingId: "123456",
                Title: "Test Product",
                Price: 99.99m,
                Currency: "USD",
                ShippingCost: 5.00m,
                Condition: "New",
                ListingStatus: "Active",
                PurchaseFormat: "BuyItNow",
                Description: "Test description",
                Url: "https://ebay.com/itm/123456",
                EndDateUtc: null,
                Location: "USA",
                ItemSpecifics: "{\"Brand\":\"Test\"}",
                Images: new List<string> { "img1.jpg", "img2.jpg" })
        });

        // Act
        await _activity.Run(input, null!);

        // Assert
        var saved = await _dbContext.Listings.FirstOrDefaultAsync(l => l.ListingId == "123456");
        Assert.Multiple(() =>
        {
            Assert.That(saved, Is.Not.Null);
            Assert.That(saved!.Title, Is.EqualTo("Test Product"));
            Assert.That(saved.Price, Is.EqualTo(99.99m));
            Assert.That(saved.ScrapeJobId, Is.EqualTo(job.Id));
        });
    }

    [Test]
    public async Task Should_create_status_history_record()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var input = new SaveListingsInput(job.Id, new List<ListingData>
        {
            new ListingData(
                ListingId: "123456",
                Title: "Test",
                Price: 50.00m,
                Currency: "USD",
                ShippingCost: null,
                Condition: null,
                ListingStatus: "Sold",
                PurchaseFormat: null,
                Description: null,
                Url: null,
                EndDateUtc: DateTime.UtcNow,
                Location: null,
                ItemSpecifics: null,
                Images: null)
        });

        // Act
        await _activity.Run(input, null!);

        // Assert
        var history = await _dbContext.ListingStatusHistory.FirstOrDefaultAsync();
        Assert.Multiple(() =>
        {
            Assert.That(history, Is.Not.Null);
            Assert.That(history!.ListingStatus, Is.EqualTo("Sold"));
            Assert.That(history.Price, Is.EqualTo(50.00m));
            Assert.That(history.Source, Is.EqualTo("InitialScrape"));
        });
    }

    [Test]
    public async Task Should_skip_listings_with_empty_listing_id()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var input = new SaveListingsInput(job.Id, new List<ListingData>
        {
            new ListingData("", "Empty ID", null, null, null, null, null, null, null, null, null, null, null, null),
            new ListingData("valid123", "Valid", null, null, null, null, null, null, null, null, null, null, null, null)
        });

        // Act
        await _activity.Run(input, null!);

        // Assert
        var count = await _dbContext.Listings.CountAsync();
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task Should_serialize_images_as_json()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var images = new List<string> { "https://img1.jpg", "https://img2.jpg" };
        var input = new SaveListingsInput(job.Id, new List<ListingData>
        {
            new ListingData("123", "Test", null, null, null, null, null, null, null, null, null, null, null, images)
        });

        // Act
        await _activity.Run(input, null!);

        // Assert
        var saved = await _dbContext.Listings.FirstAsync();
        Assert.That(saved.Images, Does.Contain("https://img1.jpg"));
        Assert.That(saved.Images, Does.Contain("https://img2.jpg"));
    }
}
