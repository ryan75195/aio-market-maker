# Complete Activity Test Coverage Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add unit test coverage for all 11 remaining Azure Durable Functions activities.

**Architecture:** Three test groups: (1) DB activities using in-memory SQLite, (2) Pure logic activities using real implementations, (3) Scraper API activities using mocked IWebscraperClient/IJobRepository. Each activity gets its own test file following existing naming conventions.

**Tech Stack:** NUnit 3.14.0, Moq, in-memory SQLite via EF Core, Microsoft.Extensions.Logging.Abstractions, Microsoft.Extensions.Configuration.

---

## Task 1: GetEnabledJobsActivity Tests

**Files:**
- Create: `AIOMarketMaker.Tests/UnitTests/Activities/GetEnabledJobsActivityTests.cs`

**Step 1: Write the test class**

```csharp
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Functions.Activities;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class GetEnabledJobsActivityTests
{
    private EtlDbContext _dbContext = null!;
    private GetEnabledJobsActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _activity = new GetEnabledJobsActivity(
            _dbContext,
            NullLogger<GetEnabledJobsActivity>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task Should_return_only_enabled_jobs()
    {
        // Arrange
        _dbContext.ScrapeJobs.AddRange(
            new ScrapeJob { SearchTerm = "enabled1", IsEnabled = true },
            new ScrapeJob { SearchTerm = "disabled", IsEnabled = false },
            new ScrapeJob { SearchTerm = "enabled2", IsEnabled = true });
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _activity.Run(null, null!);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(j => j.SearchTerm), Is.EquivalentTo(new[] { "enabled1", "enabled2" }));
    }

    [Test]
    public async Task Should_return_empty_list_when_no_enabled_jobs()
    {
        // Arrange
        _dbContext.ScrapeJobs.Add(new ScrapeJob { SearchTerm = "disabled", IsEnabled = false });
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _activity.Run(null, null!);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Should_return_empty_list_when_no_jobs_exist()
    {
        // Act
        var result = await _activity.Run(null, null!);

        // Assert
        Assert.That(result, Is.Empty);
    }
}
```

**Step 2: Run tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~GetEnabledJobsActivityTests" -v n`
Expected: 3 tests pass

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/UnitTests/Activities/GetEnabledJobsActivityTests.cs
git commit -m "test: add unit tests for GetEnabledJobsActivity"
```

---

## Task 2: GetJobDetailsActivity Tests

**Files:**
- Create: `AIOMarketMaker.Tests/UnitTests/Activities/GetJobDetailsActivityTests.cs`

**Step 1: Write the test class**

```csharp
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Functions.Activities;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class GetJobDetailsActivityTests
{
    private EtlDbContext _dbContext = null!;
    private GetJobDetailsActivity _activity = null!;
    private const int DefaultLookbackDays = 90;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scraping:DefaultLookbackDays"] = DefaultLookbackDays.ToString()
            })
            .Build();

        _activity = new GetJobDetailsActivity(
            _dbContext,
            config,
            NullLogger<GetJobDetailsActivity>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task Should_return_null_when_job_not_found()
    {
        // Act
        var result = await _activity.Run(999, null!);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_use_default_lookback_when_never_run()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test", LastRunUtc = null };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _activity.Run(job.Id, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.LookbackDays, Is.EqualTo(DefaultLookbackDays));
            Assert.That(result.SearchTerm, Is.EqualTo("test"));
        });
    }

    [Test]
    public async Task Should_calculate_lookback_as_days_since_last_run_plus_one()
    {
        // Arrange - ran 5 days ago
        var job = new ScrapeJob
        {
            SearchTerm = "test",
            LastRunUtc = DateTime.UtcNow.AddDays(-5)
        };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _activity.Run(job.Id, null!);

        // Assert
        Assert.That(result!.LookbackDays, Is.EqualTo(6)); // 5 days + 1
    }

    [Test]
    public async Task Should_return_minimum_of_one_lookback_day_when_ran_today()
    {
        // Arrange - ran just now
        var job = new ScrapeJob
        {
            SearchTerm = "test",
            LastRunUtc = DateTime.UtcNow
        };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _activity.Run(job.Id, null!);

        // Assert
        Assert.That(result!.LookbackDays, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task Should_calculate_lookback_when_ran_yesterday()
    {
        // Arrange - ran yesterday
        var job = new ScrapeJob
        {
            SearchTerm = "test",
            LastRunUtc = DateTime.UtcNow.AddDays(-1)
        };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _activity.Run(job.Id, null!);

        // Assert
        Assert.That(result!.LookbackDays, Is.EqualTo(2)); // 1 day + 1
    }
}
```

**Step 2: Run tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~GetJobDetailsActivityTests" -v n`
Expected: 5 tests pass

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/UnitTests/Activities/GetJobDetailsActivityTests.cs
git commit -m "test: add unit tests for GetJobDetailsActivity"
```

---

## Task 3: GetActiveListingsActivity Tests

**Files:**
- Create: `AIOMarketMaker.Tests/UnitTests/Activities/GetActiveListingsActivityTests.cs`

**Step 1: Write the test class**

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
```

**Step 2: Run tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~GetActiveListingsActivityTests" -v n`
Expected: 3 tests pass

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/UnitTests/Activities/GetActiveListingsActivityTests.cs
git commit -m "test: add unit tests for GetActiveListingsActivity"
```

---

## Task 4: UpdateJobTimestampActivity Tests

**Files:**
- Create: `AIOMarketMaker.Tests/UnitTests/Activities/UpdateJobTimestampActivityTests.cs`

**Step 1: Write the test class**

```csharp
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Functions.Activities;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class UpdateJobTimestampActivityTests
{
    private EtlDbContext _dbContext = null!;
    private UpdateJobTimestampActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _activity = new UpdateJobTimestampActivity(
            _dbContext,
            NullLogger<UpdateJobTimestampActivity>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task Should_update_job_timestamp_to_current_utc()
    {
        // Arrange
        var job = new ScrapeJob { SearchTerm = "test", LastRunUtc = null };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var beforeUpdate = DateTime.UtcNow;

        // Act
        await _activity.Run(job.Id, null!);

        // Assert
        var afterUpdate = DateTime.UtcNow;
        var updatedJob = await _dbContext.ScrapeJobs.FindAsync(job.Id);

        Assert.Multiple(() =>
        {
            Assert.That(updatedJob!.LastRunUtc, Is.Not.Null);
            Assert.That(updatedJob.LastRunUtc, Is.GreaterThanOrEqualTo(beforeUpdate));
            Assert.That(updatedJob.LastRunUtc, Is.LessThanOrEqualTo(afterUpdate));
        });
    }

    [Test]
    public async Task Should_not_throw_when_job_not_found()
    {
        // Act & Assert - should not throw
        Assert.DoesNotThrowAsync(async () => await _activity.Run(999, null!));
    }
}
```

**Step 2: Run tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~UpdateJobTimestampActivityTests" -v n`
Expected: 2 tests pass

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/UnitTests/Activities/UpdateJobTimestampActivityTests.cs
git commit -m "test: add unit tests for UpdateJobTimestampActivity"
```

---

## Task 5: UpdateSoldListingsActivity Tests

**Files:**
- Create: `AIOMarketMaker.Tests/UnitTests/Activities/UpdateSoldListingsActivityTests.cs`

**Step 1: Write the test class**

```csharp
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
            new ListingData("", null, 100m, null, null, null, "Sold", null, null, null, null, null, null, null),
            new ListingData(null!, null, 100m, null, null, null, "Sold", null, null, null, null, null, null, null)
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
```

**Step 2: Run tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~UpdateSoldListingsActivityTests" -v n`
Expected: 4 tests pass

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/UnitTests/Activities/UpdateSoldListingsActivityTests.cs
git commit -m "test: add unit tests for UpdateSoldListingsActivity"
```

---

## Task 6: BuildUrlsActivity Tests

**Files:**
- Create: `AIOMarketMaker.Tests/UnitTests/Activities/BuildUrlsActivityTests.cs`

**Step 1: Write the test class**

```csharp
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Functions.Activities;
using AIOMarketMaker.Functions.Contracts;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class BuildUrlsActivityTests
{
    private BuildUrlsActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        var urlBuilder = new EbayUrlBuilder();
        _activity = new BuildUrlsActivity(urlBuilder);
    }

    [Test]
    public void Should_build_active_search_url()
    {
        // Arrange
        var input = new BuildSearchUrlInput("PlayStation 5", false, 1);

        // Act
        var url = _activity.BuildSearchUrlActivity(input, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(url, Does.Contain("ebay.com"));
            Assert.That(url, Does.Contain("PlayStation"));
            Assert.That(url, Does.Not.Contain("LH_Sold=1"));
        });
    }

    [Test]
    public void Should_build_sold_search_url()
    {
        // Arrange
        var input = new BuildSearchUrlInput("PlayStation 5", true, 1);

        // Act
        var url = _activity.BuildSearchUrlActivity(input, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(url, Does.Contain("ebay.com"));
            Assert.That(url, Does.Contain("LH_Sold=1"));
            Assert.That(url, Does.Contain("LH_Complete=1"));
        });
    }

    [Test]
    public void Should_build_search_url_with_page_number()
    {
        // Arrange
        var input = new BuildSearchUrlInput("test", false, 3);

        // Act
        var url = _activity.BuildSearchUrlActivity(input, null!);

        // Assert
        Assert.That(url, Does.Contain("_pgn=3"));
    }

    [Test]
    public void Should_build_listing_url_from_id()
    {
        // Arrange
        var listingId = "123456789012";

        // Act
        var url = _activity.BuildListingUrlActivity(listingId, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(url, Does.Contain("ebay.com/itm/"));
            Assert.That(url, Does.Contain(listingId));
        });
    }
}
```

**Step 2: Run tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~BuildUrlsActivityTests" -v n`
Expected: 4 tests pass

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/UnitTests/Activities/BuildUrlsActivityTests.cs
git commit -m "test: add unit tests for BuildUrlsActivity"
```

---

## Task 7: ParseDescriptionActivity Tests

**Files:**
- Create: `AIOMarketMaker.Tests/UnitTests/Activities/ParseDescriptionActivityTests.cs`

**Step 1: Write the test class**

```csharp
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Functions.Activities;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class ParseDescriptionActivityTests
{
    private ParseDescriptionActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        var listingParser = new EbayListingParser();
        _activity = new ParseDescriptionActivity(
            listingParser,
            NullLogger<ParseDescriptionActivity>.Instance);
    }

    [Test]
    public async Task Should_return_null_for_empty_html()
    {
        // Act
        var result = await _activity.Run("", null!);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_return_null_for_null_html()
    {
        // Act
        var result = await _activity.Run(null!, null!);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_parse_description_from_html()
    {
        // Arrange
        var html = @"
            <html>
            <body>
                <div id='ds_div'>
                    <p>This is a test product description.</p>
                    <p>It has multiple paragraphs.</p>
                </div>
            </body>
            </html>";

        // Act
        var result = await _activity.Run(html, null!);

        // Assert
        Assert.That(result, Does.Contain("test product description"));
    }

    [Test]
    public async Task Should_handle_malformed_html_gracefully()
    {
        // Arrange
        var html = "<html><body><div>Unclosed tags<p>No closing";

        // Act & Assert - should not throw
        Assert.DoesNotThrowAsync(async () => await _activity.Run(html, null!));
    }
}
```

**Step 2: Run tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ParseDescriptionActivityTests" -v n`
Expected: 4 tests pass (may need to adjust based on actual parser behavior)

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/UnitTests/Activities/ParseDescriptionActivityTests.cs
git commit -m "test: add unit tests for ParseDescriptionActivity"
```

---

## Task 8: SubmitScrapeJobActivity Tests

**Files:**
- Create: `AIOMarketMaker.Tests/UnitTests/Activities/SubmitScrapeJobActivityTests.cs`

**Step 1: Write the test class**

```csharp
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Functions.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class SubmitScrapeJobActivityTests
{
    private Mock<IWebscraperClient> _mockWebScraper = null!;
    private SubmitScrapeJobActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _mockWebScraper = new Mock<IWebscraperClient>();
        _activity = new SubmitScrapeJobActivity(
            _mockWebScraper.Object,
            NullLogger<SubmitScrapeJobActivity>.Instance);
    }

    [Test]
    public async Task Should_return_job_id_from_scraper()
    {
        // Arrange
        var expectedJobId = "job-123-456";
        _mockWebScraper
            .Setup(x => x.NewJobAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new NewJobResponse(expectedJobId));

        // Act
        var result = await _activity.Run("https://ebay.com/itm/123", null!);

        // Assert
        Assert.That(result, Is.EqualTo(expectedJobId));
    }

    [Test]
    public async Task Should_pass_url_to_scraper()
    {
        // Arrange
        var url = "https://ebay.com/itm/123456";
        _mockWebScraper
            .Setup(x => x.NewJobAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new NewJobResponse("job-id"));

        // Act
        await _activity.Run(url, null!);

        // Assert
        _mockWebScraper.Verify(x => x.NewJobAsync(
            It.Is<IEnumerable<string>>(urls => urls.Contains(url))), Times.Once);
    }

    [Test]
    public void Should_propagate_exception_from_scraper()
    {
        // Arrange
        _mockWebScraper
            .Setup(x => x.NewJobAsync(It.IsAny<IEnumerable<string>>()))
            .ThrowsAsync(new HttpRequestException("Scraper unavailable"));

        // Act & Assert
        Assert.ThrowsAsync<HttpRequestException>(async () =>
            await _activity.Run("https://ebay.com/itm/123", null!));
    }
}
```

**Step 2: Run tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~SubmitScrapeJobActivityTests" -v n`
Expected: 3 tests pass

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/UnitTests/Activities/SubmitScrapeJobActivityTests.cs
git commit -m "test: add unit tests for SubmitScrapeJobActivity"
```

---

## Task 9: CheckScrapeJobStatusActivity Tests

**Files:**
- Create: `AIOMarketMaker.Tests/UnitTests/Activities/CheckScrapeJobStatusActivityTests.cs`

**Step 1: Write the test class**

```csharp
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Functions.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class CheckScrapeJobStatusActivityTests
{
    private Mock<IWebscraperClient> _mockWebScraper = null!;
    private CheckScrapeJobStatusActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _mockWebScraper = new Mock<IWebscraperClient>();
        _activity = new CheckScrapeJobStatusActivity(
            _mockWebScraper.Object,
            NullLogger<CheckScrapeJobStatusActivity>.Instance);
    }

    [Test]
    public async Task Should_return_not_found_when_status_is_null()
    {
        // Arrange
        _mockWebScraper
            .Setup(x => x.GetStatusAsync(It.IsAny<string>()))
            .ReturnsAsync((JobStatusResponse?)null);

        // Act
        var result = await _activity.Run("job-123", null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.JobId, Is.EqualTo("job-123"));
            Assert.That(result.Status, Is.EqualTo("NotFound"));
            Assert.That(result.IsComplete, Is.False);
        });
    }

    [Test]
    public async Task Should_return_complete_true_when_status_is_success()
    {
        // Arrange
        _mockWebScraper
            .Setup(x => x.GetStatusAsync(It.IsAny<string>()))
            .ReturnsAsync(new JobStatusResponse(JobStatus.Success));

        // Act
        var result = await _activity.Run("job-123", null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo("Success"));
            Assert.That(result.IsComplete, Is.True);
        });
    }

    [Test]
    public async Task Should_return_complete_true_when_status_is_failure()
    {
        // Arrange
        _mockWebScraper
            .Setup(x => x.GetStatusAsync(It.IsAny<string>()))
            .ReturnsAsync(new JobStatusResponse(JobStatus.Failure));

        // Act
        var result = await _activity.Run("job-123", null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo("Failure"));
            Assert.That(result.IsComplete, Is.True);
        });
    }

    [Test]
    public async Task Should_return_complete_false_when_status_is_pending()
    {
        // Arrange
        _mockWebScraper
            .Setup(x => x.GetStatusAsync(It.IsAny<string>()))
            .ReturnsAsync(new JobStatusResponse(JobStatus.Pending));

        // Act
        var result = await _activity.Run("job-123", null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo("Pending"));
            Assert.That(result.IsComplete, Is.False);
        });
    }
}
```

**Step 2: Run tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~CheckScrapeJobStatusActivityTests" -v n`
Expected: 4 tests pass

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/UnitTests/Activities/CheckScrapeJobStatusActivityTests.cs
git commit -m "test: add unit tests for CheckScrapeJobStatusActivity"
```

---

## Task 10: GetScrapedHtmlActivity Tests

**Files:**
- Create: `AIOMarketMaker.Tests/UnitTests/Activities/GetScrapedHtmlActivityTests.cs`

**Step 1: Write the test class**

```csharp
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Functions.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ScraperWorker.Services;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class GetScrapedHtmlActivityTests
{
    private Mock<IWebscraperClient> _mockWebScraper = null!;
    private Mock<IJobRepository> _mockJobRepository = null!;
    private GetScrapedHtmlActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _mockWebScraper = new Mock<IWebscraperClient>();
        _mockJobRepository = new Mock<IJobRepository>();
        _activity = new GetScrapedHtmlActivity(
            _mockWebScraper.Object,
            _mockJobRepository.Object,
            NullLogger<GetScrapedHtmlActivity>.Instance);
    }

    [Test]
    public async Task Should_return_html_from_blob_storage()
    {
        // Arrange
        var jobId = "job-123";
        var url = "https://ebay.com/itm/123";
        var expectedHtml = "<html><body>Test content</body></html>";

        _mockWebScraper
            .Setup(x => x.GetResultsAsync(jobId))
            .ReturnsAsync(new List<JobResultResponse> { new JobResultResponse(url, "Success") });

        _mockJobRepository
            .Setup(x => x.GetFileContentsAsync(jobId, url, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedHtml);

        // Act
        var result = await _activity.Run(new GetScrapedHtmlInput(jobId), null!);

        // Assert
        Assert.That(result, Is.EqualTo(expectedHtml));
    }

    [Test]
    public async Task Should_return_null_when_no_results()
    {
        // Arrange
        _mockWebScraper
            .Setup(x => x.GetResultsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<JobResultResponse>());

        // Act
        var result = await _activity.Run(new GetScrapedHtmlInput("job-123"), null!);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_return_null_when_html_is_empty()
    {
        // Arrange
        var jobId = "job-123";
        var url = "https://ebay.com/itm/123";

        _mockWebScraper
            .Setup(x => x.GetResultsAsync(jobId))
            .ReturnsAsync(new List<JobResultResponse> { new JobResultResponse(url, "Success") });

        _mockJobRepository
            .Setup(x => x.GetFileContentsAsync(jobId, url, It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        // Act
        var result = await _activity.Run(new GetScrapedHtmlInput(jobId), null!);

        // Assert
        Assert.That(result, Is.Null);
    }
}
```

**Step 2: Run tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~GetScrapedHtmlActivityTests" -v n`
Expected: 3 tests pass

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/UnitTests/Activities/GetScrapedHtmlActivityTests.cs
git commit -m "test: add unit tests for GetScrapedHtmlActivity"
```

---

## Task 11: Final Verification

**Step 1: Run all unit tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit" -v n`
Expected: All tests pass (should be ~55+ unit tests total)

**Step 2: Run all activity tests specifically**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ActivityTests" -v n`
Expected: All 48 activity tests pass

---

## Summary

| Task | Activity | Tests |
|------|----------|-------|
| 1 | GetEnabledJobsActivity | 3 |
| 2 | GetJobDetailsActivity | 5 |
| 3 | GetActiveListingsActivity | 3 |
| 4 | UpdateJobTimestampActivity | 2 |
| 5 | UpdateSoldListingsActivity | 4 |
| 6 | BuildUrlsActivity | 4 |
| 7 | ParseDescriptionActivity | 4 |
| 8 | SubmitScrapeJobActivity | 3 |
| 9 | CheckScrapeJobStatusActivity | 4 |
| 10 | GetScrapedHtmlActivity | 3 |
| **Total** | **10 activities** | **35 tests** |

Combined with existing 17 activity tests = **52 activity tests total**
