# Global Listing Deduplication Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace per-job listing deduplication with global upsert that protects status hierarchy

**Architecture:** Change from "filter by job + insert" to "global lookup + upsert with status protection". Status can only progress forward (Active → OutOfStock → Ended → Sold). Preserve CreatedUtc and ScrapeJobId from first writer.

**Tech Stack:** .NET 8, EF Core, SQLite, NUnit, Moq

---

## Task 1: Add Status Hierarchy Helper

**Files:**
- Create: `AIOMarketMaker.Core/Services/ListingStatusHelper.cs`
- Test: `AIOMarketMaker.Tests/UnitTests/ListingStatusHelperTests.cs`

### Step 1: Write failing tests

Create `AIOMarketMaker.Tests/UnitTests/ListingStatusHelperTests.cs`:

```csharp
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class ListingStatusHelperTests
{
    [TestCase("Active", "Sold", true, Description = "Active to Sold allowed")]
    [TestCase("Active", "Ended", true, Description = "Active to Ended allowed")]
    [TestCase("Active", "OutOfStock", true, Description = "Active to OutOfStock allowed")]
    [TestCase("Active", "Active", true, Description = "Active to Active allowed (data refresh)")]
    [TestCase("Sold", "Active", false, Description = "Sold to Active blocked")]
    [TestCase("Sold", "Ended", false, Description = "Sold to Ended blocked")]
    [TestCase("Ended", "Active", false, Description = "Ended to Active blocked")]
    [TestCase("OutOfStock", "Active", false, Description = "OutOfStock to Active blocked")]
    [TestCase(null, "Active", true, Description = "Null to Active allowed")]
    [TestCase("Unknown", "Sold", true, Description = "Unknown status to Sold allowed")]
    public void Should_enforce_status_hierarchy(string? existingStatus, string? newStatus, bool expected)
    {
        var result = ListingStatusHelper.CanUpdateStatus(existingStatus, newStatus);
        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCase("Active", 0)]
    [TestCase("OutOfStock", 1)]
    [TestCase("Ended", 2)]
    [TestCase("Sold", 3)]
    [TestCase("Unknown", -1)]
    [TestCase(null, -1)]
    public void Should_return_correct_status_rank(string? status, int expectedRank)
    {
        var result = ListingStatusHelper.GetStatusRank(status);
        Assert.That(result, Is.EqualTo(expectedRank));
    }
}
```

### Step 2: Run tests to verify they fail

Run: `dotnet test AIOMarketMaker.Tests --filter "ListingStatusHelperTests" -v n`

Expected: Build error - `ListingStatusHelper` does not exist

### Step 3: Implement ListingStatusHelper

Create `AIOMarketMaker.Core/Services/ListingStatusHelper.cs`:

```csharp
namespace AIOMarketMaker.Core.Services;

public static class ListingStatusHelper
{
    private static readonly Dictionary<string, int> StatusRank = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Active", 0 },
        { "OutOfStock", 1 },
        { "Ended", 2 },
        { "Sold", 3 }
    };

    public static int GetStatusRank(string? status)
    {
        if (string.IsNullOrEmpty(status))
            return -1;

        return StatusRank.GetValueOrDefault(status, -1);
    }

    public static bool CanUpdateStatus(string? existingStatus, string? newStatus)
    {
        var existingRank = GetStatusRank(existingStatus);
        var newRank = GetStatusRank(newStatus);

        // Unknown/null existing status can always be updated
        if (existingRank < 0)
            return true;

        // New status must be same or higher rank
        return newRank >= existingRank;
    }
}
```

### Step 4: Run tests to verify they pass

Run: `dotnet test AIOMarketMaker.Tests --filter "ListingStatusHelperTests" -v n`

Expected: All 16 tests pass

### Step 5: Commit

```bash
git add AIOMarketMaker.Core/Services/ListingStatusHelper.cs AIOMarketMaker.Tests/UnitTests/ListingStatusHelperTests.cs
git commit -m "feat: add ListingStatusHelper for status hierarchy enforcement"
```

---

## Task 2: Update FilterNewListingsActivity for Global Deduplication

**Files:**
- Modify: `AIOMarketMaker.Functions/Activities/FilterNewListingsActivity.cs`
- Modify: `AIOMarketMaker.Tests/UnitTests/Activities/FilterNewListingsActivityTests.cs`

### Step 1: Update tests to expect global behavior

Replace `AIOMarketMaker.Tests/UnitTests/Activities/FilterNewListingsActivityTests.cs`:

```csharp
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
```

### Step 2: Run tests to verify they fail

Run: `dotnet test AIOMarketMaker.Tests --filter "FilterNewListingsActivityTests" -v n`

Expected: `Should_filter_out_sold_listings_globally` and `Should_filter_ended_listings_globally` fail

### Step 3: Update FilterNewListingsActivity

Replace content of `AIOMarketMaker.Functions/Activities/FilterNewListingsActivity.cs`:

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Functions.Contracts;

namespace AIOMarketMaker.Functions.Activities;

public class FilterNewListingsActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<FilterNewListingsActivity> _logger;

    // Terminal statuses that should be filtered globally
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sold", "Ended", "OutOfStock"
    };

    public FilterNewListingsActivity(
        EtlDbContext dbContext,
        ILogger<FilterNewListingsActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(FilterNewListingsActivity))]
    public async Task<List<string>> Run(
        [ActivityTrigger] FilterNewListingsInput input,
        FunctionContext context)
    {
        _logger.LogInformation("Filtering {Count} listings for job {JobId}",
            input.ListingIds.Count, input.JobId);

        // Get all listings with terminal status (globally, not per-job)
        var terminalListingIds = await _dbContext.Listings
            .Where(l => input.ListingIds.Contains(l.ListingId))
            .Where(l => l.ListingStatus != null && TerminalStatuses.Contains(l.ListingStatus))
            .Select(l => l.ListingId)
            .ToListAsync();

        var terminalSet = terminalListingIds.ToHashSet();

        var newListingIds = input.ListingIds
            .Where(id => !terminalSet.Contains(id))
            .ToList();

        _logger.LogInformation("Found {NewCount} listings to process (filtered {TerminalCount} terminal)",
            newListingIds.Count, terminalSet.Count);

        return newListingIds;
    }
}
```

### Step 4: Run tests to verify they pass

Run: `dotnet test AIOMarketMaker.Tests --filter "FilterNewListingsActivityTests" -v n`

Expected: All 5 tests pass

### Step 5: Commit

```bash
git add AIOMarketMaker.Functions/Activities/FilterNewListingsActivity.cs AIOMarketMaker.Tests/UnitTests/Activities/FilterNewListingsActivityTests.cs
git commit -m "feat: change FilterNewListingsActivity to global terminal-status filtering"
```

---

## Task 3: Update SaveListingsActivity for Upsert with Status Protection

**Files:**
- Modify: `AIOMarketMaker.Functions/Activities/SaveListingsActivity.cs`
- Modify: `AIOMarketMaker.Tests/UnitTests/Activities/SaveListingsActivityTests.cs`

### Step 1: Add new tests for upsert behavior

Add these tests to `AIOMarketMaker.Tests/UnitTests/Activities/SaveListingsActivityTests.cs`:

```csharp
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
```

### Step 2: Run tests to verify they fail

Run: `dotnet test AIOMarketMaker.Tests --filter "SaveListingsActivityTests" -v n`

Expected: New tests fail (upsert not implemented)

### Step 3: Update SaveListingsActivity for upsert

Replace `AIOMarketMaker.Functions/Activities/SaveListingsActivity.cs`:

```csharp
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Functions.Contracts;

namespace AIOMarketMaker.Functions.Activities;

public class SaveListingsActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<SaveListingsActivity> _logger;

    public SaveListingsActivity(
        EtlDbContext dbContext,
        ILogger<SaveListingsActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(SaveListingsActivity))]
    public async Task Run(
        [ActivityTrigger] SaveListingsInput input,
        FunctionContext context)
    {
        _logger.LogInformation("Upserting {Count} listings for job {JobId}",
            input.Listings.Count, input.JobId);

        var validListings = input.Listings
            .Where(l => !string.IsNullOrEmpty(l.ListingId))
            .Where(l => !string.IsNullOrWhiteSpace(l.Title))
            .ToList();

        var insertCount = 0;
        var updateCount = 0;

        foreach (var listingData in validListings)
        {
            var existing = await _dbContext.Listings
                .FirstOrDefaultAsync(l => l.ListingId == listingData.ListingId);

            if (existing == null)
            {
                // INSERT new listing
                var newListing = MapToListing(listingData, input.JobId);
                _dbContext.Listings.Add(newListing);
                await _dbContext.SaveChangesAsync();

                // Create initial history record
                _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                {
                    ListingId = newListing.Id,
                    ListingStatus = newListing.ListingStatus ?? "Unknown",
                    Price = newListing.Price,
                    SoldDateUtc = newListing.EndDateUtc,
                    RecordedUtc = DateTime.UtcNow,
                    Source = "InitialScrape"
                });
                insertCount++;
            }
            else
            {
                // UPDATE existing listing with status protection
                var statusChanged = UpdateExistingListing(existing, listingData);

                if (statusChanged)
                {
                    // Add history record for status change
                    _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                    {
                        ListingId = existing.Id,
                        ListingStatus = existing.ListingStatus ?? "Unknown",
                        Price = existing.Price,
                        SoldDateUtc = existing.EndDateUtc,
                        RecordedUtc = DateTime.UtcNow,
                        Source = "StatusUpdate"
                    });
                }
                updateCount++;
            }
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Upserted listings: {Inserted} inserted, {Updated} updated",
            insertCount, updateCount);
    }

    private static Listing MapToListing(ListingData data, int jobId)
    {
        return new Listing
        {
            ListingId = data.ListingId,
            ScrapeJobId = jobId,
            Title = data.Title,
            Price = data.Price,
            Currency = data.Currency,
            ShippingCost = data.ShippingCost,
            Url = data.Url,
            Condition = data.Condition,
            ListingStatus = data.ListingStatus,
            PurchaseFormat = data.PurchaseFormat,
            Description = data.Description,
            ItemSpecifics = data.ItemSpecifics,
            Images = data.Images != null ? JsonSerializer.Serialize(data.Images) : null,
            Location = data.Location,
            EndDateUtc = data.EndDateUtc,
            CreatedUtc = DateTime.UtcNow
        };
    }

    private static bool UpdateExistingListing(Listing existing, ListingData data)
    {
        var statusChanged = false;

        // Only update status if it's a forward progression
        if (ListingStatusHelper.CanUpdateStatus(existing.ListingStatus, data.ListingStatus))
        {
            if (existing.ListingStatus != data.ListingStatus)
            {
                existing.ListingStatus = data.ListingStatus;
                existing.EndDateUtc = data.EndDateUtc;
                statusChanged = true;
            }
        }

        // Always update data fields (don't touch CreatedUtc or ScrapeJobId)
        existing.Title = data.Title;
        existing.Price = data.Price;
        existing.Currency = data.Currency;
        existing.ShippingCost = data.ShippingCost;
        existing.Url = data.Url;
        existing.Condition = data.Condition;
        existing.PurchaseFormat = data.PurchaseFormat;
        existing.Description = data.Description;
        existing.ItemSpecifics = data.ItemSpecifics;
        existing.Location = data.Location;
        if (data.Images != null)
        {
            existing.Images = JsonSerializer.Serialize(data.Images);
        }
        existing.UpdatedUtc = DateTime.UtcNow;

        return statusChanged;
    }
}
```

### Step 4: Run tests to verify they pass

Run: `dotnet test AIOMarketMaker.Tests --filter "SaveListingsActivityTests" -v n`

Expected: All tests pass

### Step 5: Commit

```bash
git add AIOMarketMaker.Functions/Activities/SaveListingsActivity.cs AIOMarketMaker.Tests/UnitTests/Activities/SaveListingsActivityTests.cs
git commit -m "feat: change SaveListingsActivity to upsert with status protection"
```

---

## Task 4: Update JobRunner for Global Deduplication

**Files:**
- Modify: `AIOMarketMaker.Core/Services/JobRunner.cs`

### Step 1: Update FilterNewListings method

Change `FilterNewListings` in `JobRunner.cs` to use global terminal status filtering:

```csharp
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sold", "Ended", "OutOfStock"
    };

    private async Task<string[]> FilterNewListings(
        int jobId,
        IEnumerable<IEbayProductSummary> searchResults,
        CancellationToken ct)
    {
        var searchResultIds = searchResults
            .Where(s => s.ListingId != null)
            .Select(s => s.ListingId!)
            .ToList();

        // Get listings with terminal status (globally, not per-job)
        var terminalListingIds = (await _dbContext.Listings
            .Where(l => searchResultIds.Contains(l.ListingId))
            .Where(l => l.ListingStatus != null && TerminalStatuses.Contains(l.ListingStatus))
            .Select(l => l.ListingId)
            .ToListAsync(ct))
            .ToHashSet();

        var newListingIds = searchResultIds
            .Where(id => !terminalListingIds.Contains(id))
            .ToArray();

        _logger.LogInformation("{NewCount} listings to process (filtered {TerminalCount} terminal)",
            newListingIds.Length, terminalListingIds.Count);

        return newListingIds;
    }
```

### Step 2: Update SaveEbayListings to use upsert

Replace `SaveEbayListings` method:

```csharp
    private async Task<(int Inserted, int Updated)> UpsertListings(
        IEnumerable<EbayProduct> ebayProducts,
        int jobId,
        CancellationToken ct)
    {
        var insertCount = 0;
        var updateCount = 0;

        foreach (var product in ebayProducts.Where(p => p.ListingId != null))
        {
            var existing = await _dbContext.Listings
                .FirstOrDefaultAsync(l => l.ListingId == product.ListingId, ct);

            if (existing == null)
            {
                // INSERT new listing
                var newListing = MapToListing(product, jobId);
                _dbContext.Listings.Add(newListing);
                await _dbContext.SaveChangesAsync(ct);

                // Create initial history record
                _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                {
                    ListingId = newListing.Id,
                    ListingStatus = newListing.ListingStatus ?? "Unknown",
                    Price = newListing.Price,
                    SoldDateUtc = newListing.EndDateUtc,
                    RecordedUtc = DateTime.UtcNow,
                    Source = "InitialScrape"
                });
                insertCount++;
            }
            else
            {
                // UPDATE existing listing with status protection
                var statusChanged = UpdateExistingListing(existing, product);

                if (statusChanged)
                {
                    _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                    {
                        ListingId = existing.Id,
                        ListingStatus = existing.ListingStatus ?? "Unknown",
                        Price = existing.Price,
                        SoldDateUtc = existing.EndDateUtc,
                        RecordedUtc = DateTime.UtcNow,
                        Source = "StatusUpdate"
                    });
                }
                updateCount++;
            }
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Upserted listings: {Inserted} inserted, {Updated} updated",
            insertCount, updateCount);

        return (insertCount, updateCount);
    }

    private static bool UpdateExistingListing(Listing existing, EbayProduct product)
    {
        var statusChanged = false;
        var newStatus = product.ListingStatus?.ToString();

        // Only update status if it's a forward progression
        if (ListingStatusHelper.CanUpdateStatus(existing.ListingStatus, newStatus))
        {
            if (existing.ListingStatus != newStatus)
            {
                existing.ListingStatus = newStatus;
                existing.EndDateUtc = product.EndDateUtc;
                statusChanged = true;
            }
        }

        // Always update data fields (don't touch CreatedUtc or ScrapeJobId)
        existing.Title = product.Title;
        existing.Price = product.Price;
        existing.Currency = product.Currency;
        existing.ShippingCost = product.ShippingCost;
        existing.Url = product.Url;
        existing.Condition = product.Condition?.ToString();
        existing.PurchaseFormat = product.PurchaseFormat?.ToString();
        existing.Description = product.Description;
        existing.ItemSpecifics = product.ItemSpecifics;
        existing.Location = product.Location;
        if (product.Images != null)
        {
            existing.Images = JsonSerializer.Serialize(product.Images);
        }
        existing.UpdatedUtc = DateTime.UtcNow;

        return statusChanged;
    }
```

### Step 3: Update RunJob to use new methods

Update `RunJob` method to call `UpsertListings` instead of `SaveEbayListings`:

```csharp
    public async Task<JobRunResult> RunJob(ScrapeJob job, CancellationToken ct = default)
    {
        _logger.LogInformation("Processing job {JobId}: '{SearchTerm}'",
            job.Id, job.SearchTerm);

        try
        {
            var (allResults, soldResultIds) = await SearchEbay(job, ct);

            // Detect and update Active→Sold transitions
            var statusUpdates = await DetectAndUpdateSoldListings(job.Id, soldResultIds, ct);

            var listingIdsToProcess = await FilterNewListings(job.Id, allResults, ct);

            if (listingIdsToProcess.Length == 0)
            {
                _logger.LogInformation("No listings to process for job {JobId}", job.Id);
                await UpdateJobTimestamp(job, ct);
                return new JobRunResult(job.Id, true, allResults.Count(), 0, statusUpdates, null);
            }

            var ebayProducts = await FetchEbayProducts(listingIdsToProcess);
            var (inserted, updated) = await UpsertListings(ebayProducts, job.Id, ct);

            await UpdateJobTimestamp(job, ct);

            return new JobRunResult(job.Id, true, allResults.Count(), inserted, statusUpdates + updated, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job {JobId}: {Message}", job.Id, ex.Message);
            return new JobRunResult(job.Id, false, 0, 0, 0, ex.Message);
        }
    }
```

### Step 4: Remove old SaveEbayListings and SaveInitialStatusHistory

Delete these methods as they're replaced by `UpsertListings`:
- `SaveEbayListings`
- `SaveInitialStatusHistory`

### Step 5: Build and verify

Run: `dotnet build AIOMarketMaker.Core`

Expected: Build succeeds

### Step 6: Run all tests

Run: `dotnet test AIOMarketMaker.Tests --filter "Category=Unit" -v n`

Expected: All tests pass

### Step 7: Commit

```bash
git add AIOMarketMaker.Core/Services/JobRunner.cs
git commit -m "feat: update JobRunner for global deduplication with upsert"
```

---

## Task 5: Update DetectAndUpdateSoldListings for Global Check

**Files:**
- Modify: `AIOMarketMaker.Core/Services/JobRunner.cs`

### Step 1: Update method to check global DB state first

Update `DetectAndUpdateSoldListings` in `JobRunner.cs`:

```csharp
    private async Task<int> DetectAndUpdateSoldListings(
        int jobId,
        HashSet<string> soldResultIds,
        CancellationToken ct)
    {
        // Get all active listings GLOBALLY (not per-job) that appear in sold search results
        var activeListings = await _dbContext.Listings
            .Where(l => l.ListingStatus == "Active")
            .Where(l => soldResultIds.Contains(l.ListingId))
            .Select(l => new { l.Id, l.ListingId })
            .ToListAsync(ct);

        if (activeListings.Count == 0)
        {
            _logger.LogInformation("No Active→Sold transitions detected globally");
            return 0;
        }

        // Check which ones are already marked as Sold (by another job that ran first)
        var alreadySoldIds = await _dbContext.Listings
            .Where(l => activeListings.Select(a => a.ListingId).Contains(l.ListingId))
            .Where(l => l.ListingStatus == "Sold")
            .Select(l => l.ListingId)
            .ToListAsync(ct);

        var needsRescrape = activeListings
            .Where(l => !alreadySoldIds.Contains(l.ListingId))
            .Select(l => l.ListingId)
            .ToArray();

        if (needsRescrape.Length == 0)
        {
            _logger.LogInformation("All {Count} transitions already processed by other jobs",
                alreadySoldIds.Count);
            return 0;
        }

        _logger.LogInformation("Detected {Count} Active→Sold transitions, re-scraping...",
            needsRescrape.Length);

        // Re-scrape those listings to get accurate sold price and date
        var soldProducts = await _ebayScraper.GetItemsFromListings(needsRescrape);

        var updatedCount = 0;
        foreach (var product in soldProducts)
        {
            if (product.ListingId == null) continue;

            // Update ALL matching listings (could be in multiple jobs' active list)
            var listings = await _dbContext.Listings
                .Where(l => l.ListingId == product.ListingId && l.ListingStatus == "Active")
                .ToListAsync(ct);

            foreach (var listing in listings)
            {
                listing.ListingStatus = product.ListingStatus?.ToString() ?? "Sold";
                listing.Price = product.Price;
                listing.EndDateUtc = product.EndDateUtc;
                listing.UpdatedUtc = DateTime.UtcNow;

                _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                {
                    ListingId = listing.Id,
                    ListingStatus = listing.ListingStatus,
                    Price = product.Price,
                    SoldDateUtc = product.EndDateUtc,
                    RecordedUtc = DateTime.UtcNow,
                    Source = "JobScrape"
                });

                updatedCount++;
            }

            _logger.LogInformation("Updated listing {ListingId}: Active → {NewStatus}",
                product.ListingId, product.ListingStatus);
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Updated {Count} listing records from Active to Sold", updatedCount);

        return updatedCount;
    }
```

### Step 2: Build and test

Run: `dotnet build AIOMarketMaker.Core && dotnet test AIOMarketMaker.Tests --filter "Category=Unit" -v n`

Expected: All tests pass

### Step 3: Commit

```bash
git add AIOMarketMaker.Core/Services/JobRunner.cs
git commit -m "feat: update DetectAndUpdateSoldListings for global state check"
```

---

## Task 6: Final Verification and Cleanup

### Step 1: Run all unit tests

Run: `dotnet test AIOMarketMaker.Tests --filter "Category=Unit" -v n`

Expected: All tests pass

### Step 2: Build entire solution

Run: `dotnet build AIOMarketMaker.sln`

Expected: Build succeeds with no errors

### Step 3: Final commit

```bash
git add -A
git commit -m "chore: complete global listing deduplication implementation"
```

---

## Summary

| Task | Description | Files Changed |
|------|-------------|---------------|
| 1 | Status hierarchy helper | +ListingStatusHelper.cs, +Tests |
| 2 | Global filter activity | FilterNewListingsActivity.cs, Tests |
| 3 | Upsert save activity | SaveListingsActivity.cs, Tests |
| 4 | JobRunner global filter + upsert | JobRunner.cs |
| 5 | Global sold detection | JobRunner.cs |
| 6 | Final verification | - |

**Key Behaviors After Implementation:**
- Sold/Ended/OutOfStock listings are filtered globally (not per-job)
- Active listings can still be processed by any job
- Upsert preserves `CreatedUtc` and `ScrapeJobId` (first-writer wins)
- Status can only progress forward: Active → OutOfStock → Ended → Sold
- History records track all status changes
