using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Models.Ebay;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Services;
using AIOMarketMaker.Tests.Utils;
using AngleSharp.Dom;

namespace AIOMarketMaker.Tests.Unit.Services;

/// <summary>
/// Tests for the inline description fetching pipeline in ScrapeJobProcessor.
/// The existing ScrapeJobProcessor_UnitTests cover search/classify/update logic.
/// These tests focus on the FetchAndProcessDescriptions flow and the CreateRun/Execute lifecycle.
/// </summary>
[TestFixture]
[Category("Unit")]
public class ScrapeJobProcessor_InlineTests
{
    private EtlDbContext _dbContext = null!;
    private Mock<ILogger<ScrapeJobProcessor>> _loggerMock = null!;
    private Mock<IWebscraperClient> _webscraperClientMock = null!;
    private Mock<ISearchParser> _searchParserMock = null!;
    private Mock<IListingParser> _listingParserMock = null!;
    private Mock<IEbayUrlBuilder> _urlBuilderMock = null!;
    private Mock<IListingIndexingService> _indexingServiceMock = null!;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "Test", IsEnabled = true, CreatedUtc = DateTime.UtcNow });
        _dbContext.SaveChanges();

        _loggerMock = new Mock<ILogger<ScrapeJobProcessor>>();
        _webscraperClientMock = new Mock<IWebscraperClient>();
        _searchParserMock = new Mock<ISearchParser>();
        _listingParserMock = new Mock<IListingParser>();
        _urlBuilderMock = new Mock<IEbayUrlBuilder>();
        _indexingServiceMock = new Mock<IListingIndexingService>();

        _indexingServiceMock
            .Setup(i => i.Index(It.IsAny<Listing>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexingResult(IndexingAction.Embedded));

        // Default: return empty HTML for all GetPageHtmlAsync calls
        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html></html>");

        // Default: no search results (stops pagination immediately)
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(Enumerable.Empty<IEbayProductSummary>());

        _listingParserMock
            .Setup(p => p.ParseDescription(It.IsAny<IDocument>()))
            .Returns("Test description");

        _urlBuilderMock
            .Setup(u => u.BuildListingUrl(It.IsAny<string>()))
            .Returns<string>(id => $"https://ebay.co.uk/itm/{id}");
        _urlBuilderMock
            .Setup(u => u.BuildDescriptionUrl(It.IsAny<string>()))
            .Returns<string>(id => $"https://vi.vipr.ebaydesc.com/ws/eBayISAPI.dll?item={id}");
        _urlBuilderMock
            .Setup(u => u.BuildSearchUrl(It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<int>(), It.IsAny<Condition>(), It.IsAny<BuyingFormat>()))
            .Returns("https://ebay.co.uk/sch/test");
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    private ScrapeJobProcessor CreateProcessor() => new(
        _loggerMock.Object, _dbContext, _webscraperClientMock.Object,
        _searchParserMock.Object, _listingParserMock.Object,
        _urlBuilderMock.Object, _indexingServiceMock.Object);

    private static EbayProductSummary CreateSummary(
        string listingId, decimal? price = 100m, bool isSold = false,
        Condition? condition = Condition.USED, decimal? shippingCost = 5m) =>
        new(
            ListingId: listingId,
            Title: $"Test item {listingId}",
            Price: price,
            Currency: "GBP",
            ShippingCost: shippingCost,
            Url: $"https://ebay.co.uk/itm/{listingId}",
            BuyingFormat: BuyingFormat.BUY_NOW,
            Condition: condition,
            Images: new List<string>(),
            EndDateUtc: null,
            IsSold: isSold);

    private ScrapeRun CreateAndSeedScrapeRun()
    {
        var scrapeRun = new ScrapeRun
        {
            Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
            TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        _dbContext.SaveChanges();
        return scrapeRun;
    }

    private ScrapeJobConfig CreateJobConfig() => new(1, "Test");

    /// <summary>
    /// Sets up the search parser to return the given summaries on active search page 1.
    /// Sold search returns empty (call 1). Active page 1 returns summaries (call 2). Active page 2 returns empty (call 3).
    /// </summary>
    private void SetupActiveSearchResults(params IEbayProductSummary[] summaries)
    {
        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                // Call 1: sold page 1 -> empty (stops sold search)
                // Call 2: active page 1 -> return summaries
                // Call 3+: active page 2+ -> empty (stops active search)
                return callCount == 2
                    ? summaries.ToList()
                    : Enumerable.Empty<IEbayProductSummary>();
            });
    }

    // ---------------------------------------------------------------
    // 1. CreateRun creates proper ScrapeRun entity
    // ---------------------------------------------------------------

    [Test]
    public async Task Should_create_run_with_queued_status_and_trigger_type()
    {
        var processor = CreateProcessor();
        var job = CreateJobConfig();

        var run = await processor.CreateRun(job, "Nightly");

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("Queued"));
            Assert.That(run.CurrentPhase, Is.EqualTo("Queued"));
            Assert.That(run.TriggerType, Is.EqualTo("Nightly"));
            Assert.That(run.JobId, Is.EqualTo(1));
            Assert.That(run.StartedUtc, Is.Not.EqualTo(default(DateTime)));
            Assert.That(run.InstanceId, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public async Task Should_persist_run_to_database_on_create()
    {
        var processor = CreateProcessor();
        var job = CreateJobConfig();

        var run = await processor.CreateRun(job, "Manual");

        var persisted = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.That(persisted, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(persisted!.Id, Is.EqualTo(run.Id));
            Assert.That(persisted.Status, Is.EqualTo("Queued"));
            Assert.That(persisted.TriggerType, Is.EqualTo("Manual"));
        });
    }

    // ---------------------------------------------------------------
    // 2. Descriptions fetched inline via GetPageHtmlAsync
    // ---------------------------------------------------------------

    [Test]
    public async Task Should_fetch_description_via_GetPageHtml_for_each_new_listing()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        var summaries = new[]
        {
            CreateSummary("LID001"),
            CreateSummary("LID002"),
            CreateSummary("LID003")
        };
        SetupActiveSearchResults(summaries);

        await CreateProcessor().Execute(run, job);

        // Verify GetPageHtmlAsync was called with each listing's description URL
        foreach (var summary in summaries)
        {
            _webscraperClientMock.Verify(
                w => w.GetPageHtmlAsync(
                    It.Is<string>(url => url.Contains(summary.ListingId!)),
                    It.IsAny<IEnumerable<object>?>(),
                    It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()),
                Times.AtLeastOnce,
                $"Description should be fetched for listing {summary.ListingId}");
        }
    }

    [Test]
    public async Task Should_parse_and_save_description_for_new_listings()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _listingParserMock
            .Setup(p => p.ParseDescription(It.IsAny<IDocument>()))
            .Returns("Parsed description content");

        SetupActiveSearchResults(CreateSummary("DESC1"), CreateSummary("DESC2"));

        await CreateProcessor().Execute(run, job);

        var listings = await _dbContext.Listings.ToListAsync();
        Assert.That(listings, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(listings.All(l => l.Description == "Parsed description content"), Is.True,
                "All listings should have parsed description");
            Assert.That(listings.All(l => l.DescriptionStatus == "complete"), Is.True,
                "All listings should have DescriptionStatus = complete");
        });
    }

    [Test]
    public async Task Should_mark_description_missing_when_parser_returns_null()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _listingParserMock
            .Setup(p => p.ParseDescription(It.IsAny<IDocument>()))
            .Returns((string?)null);

        SetupActiveSearchResults(CreateSummary("NODESC1"));

        await CreateProcessor().Execute(run, job);

        var listing = await _dbContext.Listings.FirstAsync(l => l.ListingId == "NODESC1");
        Assert.That(listing.DescriptionStatus, Is.EqualTo("missing"));
    }

    // ---------------------------------------------------------------
    // 3. Failed descriptions don't fail the run
    // ---------------------------------------------------------------

    [Test]
    public async Task Should_complete_run_when_some_descriptions_fail()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        SetupActiveSearchResults(
            CreateSummary("OK1"),
            CreateSummary("FAIL1"),
            CreateSummary("OK2"));

        // Fail description fetch for FAIL1, succeed for others
        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.Is<string>(url => url.Contains("FAIL1")),
                It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection timeout"));

        await CreateProcessor().Execute(run, job);

        var refreshedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(refreshedRun!.Status, Is.EqualTo("Completed"),
                "Run should complete even when some descriptions fail");
            Assert.That(refreshedRun.CompletedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Should_mark_failed_listings_with_missing_description_status()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        SetupActiveSearchResults(CreateSummary("OK1"), CreateSummary("FAIL1"));

        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.Is<string>(url => url.Contains("FAIL1")),
                It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Timeout"));

        await CreateProcessor().Execute(run, job);

        var failedListing = await _dbContext.Listings.FirstAsync(l => l.ListingId == "FAIL1");
        var okListing = await _dbContext.Listings.FirstAsync(l => l.ListingId == "OK1");

        Assert.Multiple(() =>
        {
            Assert.That(failedListing.DescriptionStatus, Is.EqualTo("missing"),
                "Failed listing should have DescriptionStatus = missing");
            Assert.That(okListing.DescriptionStatus, Is.EqualTo("complete"),
                "Successful listing should have DescriptionStatus = complete");
        });
    }

    [Test]
    public async Task Should_create_scrape_run_issue_when_description_fetch_fails()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        SetupActiveSearchResults(CreateSummary("FAIL1"));

        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.Is<string>(url => url.Contains("FAIL1")),
                It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        await CreateProcessor().Execute(run, job);

        var issues = await _dbContext.ScrapeRunIssues.ToListAsync();
        Assert.That(issues, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(issues[0].ScrapeRunId, Is.EqualTo(run.Id));
            Assert.That(issues[0].ListingId, Is.EqualTo("FAIL1"));
            Assert.That(issues[0].IssueType, Is.EqualTo("DescriptionFetchFailed"));
            Assert.That(issues[0].ErrorMessage, Is.EqualTo("Connection refused"));
            Assert.That(issues[0].CreatedUtc, Is.Not.EqualTo(default(DateTime)));
        });
    }

    [Test]
    public async Task Should_increment_listings_failed_counter_for_description_failures()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        SetupActiveSearchResults(
            CreateSummary("FAIL1"),
            CreateSummary("FAIL2"),
            CreateSummary("OK1"));

        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.Is<string>(url => url.Contains("FAIL1") || url.Contains("FAIL2")),
                It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Timeout"));

        await CreateProcessor().Execute(run, job);

        var refreshedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.That(refreshedRun!.ListingsFailed, Is.EqualTo(2),
            "Both failed descriptions should be counted");
    }

    // ---------------------------------------------------------------
    // 4. Progress updated during processing
    // ---------------------------------------------------------------

    [Test]
    public async Task Should_update_listings_processed_count()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        // Create 25 summaries to trigger the progress save (every 10)
        var summaries = Enumerable.Range(1, 25)
            .Select(i => CreateSummary($"PROG{i:D3}"))
            .ToArray();
        SetupActiveSearchResults(summaries);

        await CreateProcessor().Execute(run, job);

        var refreshedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.That(refreshedRun!.ListingsProcessed, Is.EqualTo(25),
            "All 25 listings should be counted as processed");
    }

    [Test]
    public async Task Should_update_progress_incrementally_as_descriptions_complete()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        // Create 15 summaries — enough to trigger progress saves at 10
        var summaries = Enumerable.Range(1, 15)
            .Select(i => CreateSummary($"INC{i:D3}"))
            .ToArray();
        SetupActiveSearchResults(summaries);

        // Make one listing slow — if progress only updates after ALL complete,
        // ListingsProcessed will jump from 0 to 15.
        // If it updates incrementally, we should see intermediate saves.
        var slowTcs = new TaskCompletionSource<string>();
        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.Is<string>(url => url.Contains("INC015")),
                It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Returns(slowTcs.Task);

        // Start Execute — it will block waiting for the slow listing
        var executeTask = CreateProcessor().Execute(run, job);

        // Wait for fast listings to be fetched
        await Task.Delay(500);

        // Record progress while slow fetch is pending
        var midProgress = run.ListingsProcessed;

        // Complete the slow fetch
        slowTcs.SetResult("<html>slow description</html>");
        await executeTask;

        // The key assertion: progress should have been > 0 before the slow fetch completed
        Assert.That(midProgress, Is.GreaterThan(0),
            "Progress should update incrementally as descriptions complete, not wait for all fetches");
    }

    [Test]
    public async Task Should_track_active_listings_added()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        SetupActiveSearchResults(
            CreateSummary("ACT1", isSold: false),
            CreateSummary("ACT2", isSold: false));

        await CreateProcessor().Execute(run, job);

        var refreshedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(refreshedRun!.ListingsAddedActive, Is.EqualTo(2));
            Assert.That(refreshedRun.ListingsAddedSold, Is.EqualTo(0));
        });
    }

    // ---------------------------------------------------------------
    // 5. Run always completes when no new listings to scrape
    // ---------------------------------------------------------------

    [Test]
    public async Task Should_complete_run_when_all_listings_are_terminal()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        // Pre-seed listings with terminal statuses
        _dbContext.Listings.Add(new Listing
        {
            ListingId = "SOLD1", ScrapeJobId = 1,
            Title = "Sold Item", ListingStatus = "Sold"
        });
        _dbContext.Listings.Add(new Listing
        {
            ListingId = "ENDED1", ScrapeJobId = 1,
            Title = "Ended Item", ListingStatus = "Ended"
        });
        _dbContext.Listings.Add(new Listing
        {
            ListingId = "OOS1", ScrapeJobId = 1,
            Title = "Out of Stock", ListingStatus = "OutOfStock"
        });
        await _dbContext.SaveChangesAsync();

        // Search returns all three existing terminal listings
        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 2
                    ? new IEbayProductSummary[]
                    {
                        CreateSummary("SOLD1", isSold: false),
                        CreateSummary("ENDED1", isSold: false),
                        CreateSummary("OOS1", isSold: false)
                    }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        var refreshedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(refreshedRun!.Status, Is.EqualTo("Completed"));
            Assert.That(refreshedRun.ListingsFilteredPreQueue, Is.EqualTo(3),
                "All 3 terminal listings should be filtered");
        });

        // No description URLs should have been fetched (only search URLs)
        _webscraperClientMock.Verify(
            w => w.GetPageHtmlAsync(
                It.Is<string>(url => url.Contains("eBayISAPI")),
                It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "No description fetches should occur for terminal listings");
    }

    [Test]
    public async Task Should_complete_run_with_zero_listings_processed_when_search_returns_empty()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        // Default: search returns empty results

        await CreateProcessor().Execute(run, job);

        var refreshedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(refreshedRun!.Status, Is.EqualTo("Completed"));
            Assert.That(refreshedRun.ListingsProcessed, Is.EqualTo(0));
            Assert.That(refreshedRun.TotalListingsFound, Is.EqualTo(0));
        });
    }

    // ---------------------------------------------------------------
    // 6. Execute catches exceptions and marks run Failed
    // ---------------------------------------------------------------

    [Test]
    public async Task Should_mark_run_failed_when_search_throws()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Service unavailable"));

        await CreateProcessor().Execute(run, job);

        var refreshedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(refreshedRun!.Status, Is.EqualTo("Failed"));
            Assert.That(refreshedRun.ErrorMessage, Is.EqualTo("Service unavailable"));
            Assert.That(refreshedRun.CompletedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Should_not_throw_when_execute_encounters_exception()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unexpected error"));

        // Should not throw
        Assert.DoesNotThrowAsync(async () => await CreateProcessor().Execute(run, job));
    }

    // ---------------------------------------------------------------
    // 7. Indexing integration with description fetching
    // ---------------------------------------------------------------

    [Test]
    public async Task Should_index_listing_with_embed_content_when_description_is_complete()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        SetupActiveSearchResults(CreateSummary("IDX1"));

        await CreateProcessor().Execute(run, job);

        _indexingServiceMock.Verify(
            i => i.Index(
                It.Is<Listing>(l => l.ListingId == "IDX1"),
                true, It.IsAny<CancellationToken>()),
            Times.Once,
            "New listing with complete description should be indexed with embedContent=true");
    }

    [Test]
    public async Task Should_not_index_listing_when_description_fetch_fails()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        SetupActiveSearchResults(CreateSummary("NOIDX1"));

        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.Is<string>(url => url.Contains("NOIDX1")),
                It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Failed"));

        await CreateProcessor().Execute(run, job);

        _indexingServiceMock.Verify(
            i => i.Index(
                It.Is<Listing>(l => l.ListingId == "NOIDX1"),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Failed description should not trigger indexing");
    }

    // ---------------------------------------------------------------
    // 8. Sold listing description fetch flow
    // ---------------------------------------------------------------

    [Test]
    public async Task Should_track_sold_listings_added_count()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        // Return sold summaries on sold search page 1
        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                // Call 1: sold page 1 -> return sold listings
                // Call 2: sold page 2 -> empty (stops)
                // Call 3+: active pages -> empty
                return callCount == 1
                    ? new IEbayProductSummary[]
                    {
                        CreateSummary("SOLD1", isSold: true),
                        CreateSummary("SOLD2", isSold: true)
                    }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        var refreshedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(refreshedRun!.ListingsAddedSold, Is.EqualTo(2));
            Assert.That(refreshedRun.ListingsAddedActive, Is.EqualTo(0));
        });
    }

    // ---------------------------------------------------------------
    // 9. Multiple description failures create individual issues
    // ---------------------------------------------------------------

    [Test]
    public async Task Should_create_separate_issues_for_each_failed_description()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        SetupActiveSearchResults(
            CreateSummary("FAIL_A"),
            CreateSummary("FAIL_B"),
            CreateSummary("OK1"));

        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.Is<string>(url => url.Contains("FAIL_A")),
                It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Error A"));

        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.Is<string>(url => url.Contains("FAIL_B")),
                It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Error B"));

        await CreateProcessor().Execute(run, job);

        var issues = await _dbContext.ScrapeRunIssues.OrderBy(i => i.ListingId).ToListAsync();
        Assert.That(issues, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(issues[0].ListingId, Is.EqualTo("FAIL_A"));
            Assert.That(issues[0].ErrorMessage, Is.EqualTo("Error A"));
            Assert.That(issues[1].ListingId, Is.EqualTo("FAIL_B"));
            Assert.That(issues[1].ErrorMessage, Is.EqualTo("Error B"));
        });

        // Run should still complete
        var refreshedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.That(refreshedRun!.Status, Is.EqualTo("Completed"));
    }

    // ---------------------------------------------------------------
    // 10. Diagnostic failure tracking
    // ---------------------------------------------------------------

    [Test]
    public async Task Should_record_http_status_and_phase_on_fetch_failure()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        var httpEx = new HttpRequestException("Internal Server Error", null, System.Net.HttpStatusCode.InternalServerError);
        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.Is<string>(url => url.Contains("eBayISAPI")),
                It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(httpEx);

        SetupActiveSearchResults(CreateSummary("DIAG1"));

        await CreateProcessor().Execute(run, job);

        var issue = await _dbContext.ScrapeRunIssues.FirstOrDefaultAsync(i => i.ScrapeRunId == run.Id);
        Assert.That(issue, Is.Not.Null, "Should record a ScrapeRunIssue");
        Assert.Multiple(() =>
        {
            Assert.That(issue!.IssueType, Is.EqualTo("DescriptionFetchFailed"));
            Assert.That(issue.Phase, Is.EqualTo("DescriptionFetch"));
            Assert.That(issue.HttpStatusCode, Is.EqualTo(500));
            Assert.That(issue.StackTrace, Is.Not.Null.And.Contains("HttpRequestException"));
        });
    }

    [Test]
    public async Task Should_record_phase_and_stack_trace_on_indexing_failure()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.Is<string>(url => url.Contains("eBayISAPI")),
                It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html><body>A real description</body></html>");

        _listingParserMock
            .Setup(p => p.ParseDescription(It.IsAny<IDocument>()))
            .Returns("A real description");

        _indexingServiceMock
            .Setup(i => i.Index(It.IsAny<Listing>(), true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("OpenAI rate limit exceeded"));

        SetupActiveSearchResults(CreateSummary("IDXFAIL1"));

        await CreateProcessor().Execute(run, job);

        var issue = await _dbContext.ScrapeRunIssues.FirstOrDefaultAsync(i => i.ScrapeRunId == run.Id);
        Assert.That(issue, Is.Not.Null, "Should record a ScrapeRunIssue");
        Assert.Multiple(() =>
        {
            Assert.That(issue!.IssueType, Is.EqualTo("IndexingFailed"));
            Assert.That(issue.Phase, Is.EqualTo("Indexing"));
            Assert.That(issue.StackTrace, Is.Not.Null.And.Contains("HttpRequestException"));
            Assert.That(issue.ErrorMessage, Does.Contain("OpenAI rate limit exceeded"));
        });
    }

    [Test]
    public async Task Should_record_parse_failure_with_phase_and_stack_trace()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.Is<string>(url => url.Contains("eBayISAPI")),
                It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html><body>content</body></html>");

        _listingParserMock
            .Setup(p => p.ParseDescription(It.IsAny<IDocument>()))
            .Throws(new InvalidOperationException("Unexpected DOM structure"));

        SetupActiveSearchResults(CreateSummary("PARSEFAIL1"));

        await CreateProcessor().Execute(run, job);

        var issue = await _dbContext.ScrapeRunIssues.FirstOrDefaultAsync(i => i.ScrapeRunId == run.Id);
        Assert.That(issue, Is.Not.Null, "Should record a ScrapeRunIssue");
        Assert.Multiple(() =>
        {
            Assert.That(issue!.IssueType, Is.EqualTo("ParseFailed"));
            Assert.That(issue.Phase, Is.EqualTo("Parse"));
            Assert.That(issue.StackTrace, Is.Not.Null.And.Contains("InvalidOperationException"));
        });
    }
}
