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

    [Test]
    public async Task Should_skip_listings_with_null_or_empty_title()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var input = new SaveListingsInput(job.Id, new List<ListingData>
        {
            new ListingData("111", null, 10.00m, "USD", null, null, null, null, null, null, null, null, null, null),
            new ListingData("222", "", 20.00m, "USD", null, null, null, null, null, null, null, null, null, null),
            new ListingData("333", "   ", 30.00m, "USD", null, null, null, null, null, null, null, null, null, null),
            new ListingData("444", "Valid Title", 40.00m, "USD", null, null, null, null, null, null, null, null, null, null)
        });

        // Act
        await _activity.Run(input, null!);

        // Assert
        var count = await _dbContext.Listings.CountAsync();
        var saved = await _dbContext.Listings.FirstAsync();
        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(1), "Only the listing with a valid title should be saved");
            Assert.That(saved.ListingId, Is.EqualTo("444"));
            Assert.That(saved.Title, Is.EqualTo("Valid Title"));
        });
    }

    [Test]
    public async Task Should_update_existing_listing_instead_of_insert()
    {
        // Arrange - existing listing
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var existingListing = new Listing
        {
            ListingId = "123456",
            ScrapeJobId = job.Id,
            Title = "Old Title",
            Price = 50.00m,
            ListingStatus = "Active",
            CreatedUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.Listings.Add(existingListing);
        await _dbContext.SaveChangesAsync();

        var input = new SaveListingsInput(job.Id, new List<ListingData>
        {
            new ListingData(
                ListingId: "123456",
                Title: "New Title",
                Price: 75.00m,
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
        var count = await _dbContext.Listings.CountAsync();
        var saved = await _dbContext.Listings.FirstAsync(l => l.ListingId == "123456");

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(1), "Should not create duplicate");
            Assert.That(saved.Title, Is.EqualTo("New Title"), "Title should be updated");
            Assert.That(saved.Price, Is.EqualTo(75.00m), "Price should be updated");
            Assert.That(saved.ListingStatus, Is.EqualTo("Sold"), "Status should be updated (Active->Sold allowed)");
            Assert.That(saved.CreatedUtc, Is.EqualTo(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                "CreatedUtc should be preserved");
        });
    }

    [Test]
    public async Task Should_not_downgrade_status_from_sold_to_active()
    {
        // Arrange - existing SOLD listing
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var existingListing = new Listing
        {
            ListingId = "123456",
            ScrapeJobId = job.Id,
            Title = "Original Title",
            Price = 100.00m,
            ListingStatus = "Sold",
            EndDateUtc = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.Listings.Add(existingListing);
        await _dbContext.SaveChangesAsync();

        var input = new SaveListingsInput(job.Id, new List<ListingData>
        {
            new ListingData(
                ListingId: "123456",
                Title: "Updated Title",
                Price: 90.00m,
                Currency: "USD",
                ShippingCost: null,
                Condition: null,
                ListingStatus: "Active",  // Trying to downgrade!
                PurchaseFormat: null,
                Description: null,
                Url: null,
                EndDateUtc: null,
                Location: null,
                ItemSpecifics: null,
                Images: null)
        });

        // Act
        await _activity.Run(input, null!);

        // Assert
        var saved = await _dbContext.Listings.FirstAsync(l => l.ListingId == "123456");

        Assert.Multiple(() =>
        {
            Assert.That(saved.ListingStatus, Is.EqualTo("Sold"), "Status should NOT be downgraded");
            Assert.That(saved.EndDateUtc, Is.EqualTo(new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc)),
                "EndDateUtc should be preserved when status not updated");
            Assert.That(saved.Title, Is.EqualTo("Updated Title"), "Other fields can still be updated");
        });
    }

    [Test]
    public async Task Should_preserve_original_scrape_job_id_on_update()
    {
        // Arrange - listing owned by job1
        var job1 = new ScrapeJob { SearchTerm = "job1" };
        var job2 = new ScrapeJob { SearchTerm = "job2" };
        _dbContext.ScrapeJobs.AddRange(job1, job2);
        await _dbContext.SaveChangesAsync();

        var existingListing = new Listing
        {
            ListingId = "123456",
            ScrapeJobId = job1.Id,
            Title = "Original",
            ListingStatus = "Active"
        };
        _dbContext.Listings.Add(existingListing);
        await _dbContext.SaveChangesAsync();

        // Job2 tries to save the same listing
        var input = new SaveListingsInput(job2.Id, new List<ListingData>
        {
            new ListingData("123456", "Updated by job2", 50.00m, "USD", null, null, "Sold", null, null, null, DateTime.UtcNow, null, null, null)
        });

        // Act
        await _activity.Run(input, null!);

        // Assert
        var saved = await _dbContext.Listings.FirstAsync(l => l.ListingId == "123456");
        Assert.That(saved.ScrapeJobId, Is.EqualTo(job1.Id), "ScrapeJobId should remain with original job");
    }

    [Test]
    public async Task Should_add_history_record_on_status_change()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test" };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var existingListing = new Listing
        {
            ListingId = "123456",
            ScrapeJobId = job.Id,
            Title = "Test",
            ListingStatus = "Active"
        };
        _dbContext.Listings.Add(existingListing);
        await _dbContext.SaveChangesAsync();

        var input = new SaveListingsInput(job.Id, new List<ListingData>
        {
            new ListingData("123456", "Test", 100.00m, "USD", null, null, "Sold", null, null, null, DateTime.UtcNow, null, null, null)
        });

        // Act
        await _activity.Run(input, null!);

        // Assert
        var historyRecords = await _dbContext.ListingStatusHistory
            .Where(h => h.ListingId == existingListing.Id)
            .ToListAsync();

        Assert.That(historyRecords.Count, Is.EqualTo(1));
        Assert.That(historyRecords[0].ListingStatus, Is.EqualTo("Sold"));
        Assert.That(historyRecords[0].Source, Is.EqualTo("StatusUpdate"));
    }
}
