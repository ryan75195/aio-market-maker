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
using AIOMarketMaker.Tests.Common;
using AngleSharp.Dom;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ScrapeJobProcessor_UnitTests
{
    private EtlDbContext _dbContext = null!;
    private Mock<ILogger<ScrapeJobProcessor>> _loggerMock = null!;
    private Mock<IWebscraperClient> _webscraperClientMock = null!;
    private Mock<ISearchParser> _searchParserMock = null!;
    private Mock<IListingParser> _listingParserMock = null!;
    private Mock<IEbayUrlBuilder> _urlBuilderMock = null!;
    private Mock<IListingIndexingService> _indexingServiceMock = null!;

    [SetUp]
    public void Setup()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "Test" });
        _dbContext.SaveChanges();

        _loggerMock = new Mock<ILogger<ScrapeJobProcessor>>();
        _webscraperClientMock = new Mock<IWebscraperClient>();
        _searchParserMock = new Mock<ISearchParser>();
        _listingParserMock = new Mock<IListingParser>();
        _urlBuilderMock = new Mock<IEbayUrlBuilder>();
        _indexingServiceMock = new Mock<IListingIndexingService>();

        _indexingServiceMock
            .Setup(i => i.Index(It.IsAny<Listing>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexingResult(IndexingAction.Skipped));

        // Default: empty search results (stops pagination)
        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html></html>");

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
        _urlBuilderMock.Object, _indexingServiceMock.Object,
        new DbWriteGate(100));

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

    [Test]
    public async Task Should_complete_run_when_no_listings_found()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        await CreateProcessor().Execute(run, job);

        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(1);
        Assert.Multiple(() =>
        {
            Assert.That(updatedRun!.Status, Is.EqualTo("Completed"));
            Assert.That(updatedRun.CurrentPhase, Is.EqualTo("Completed"));
            Assert.That(updatedRun.CompletedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Should_fetch_descriptions_inline_for_new_listings()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        var summary = CreateSummary("ABC123", isSold: false);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 2
                    ? new[] { summary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        // Verify description was fetched inline (not enqueued)
        _webscraperClientMock.Verify(
            w => w.GetPageHtmlAsync(
                It.Is<string>(url => url.Contains("ABC123")),
                It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Test]
    public async Task Should_skip_terminal_listings_and_update_active_from_summary()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _dbContext.Listings.Add(new Listing
        {
            ListingId = "ACTIVE1", ScrapeJobId = 1,
            Title = "Active Item", ListingStatus = "Active",
            Price = 100m, Condition = "USED", ShippingCost = 5m
        });
        _dbContext.Listings.Add(new Listing
        {
            ListingId = "SOLD1", ScrapeJobId = 1,
            Title = "Sold Item", ListingStatus = "Sold"
        });
        await _dbContext.SaveChangesAsync();

        var activeSummary = CreateSummary("ACTIVE1", price: 90m, isSold: false);
        var soldSummary = CreateSummary("SOLD1", isSold: false);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 2
                    ? new[] { activeSummary, soldSummary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(1);
        Assert.Multiple(() =>
        {
            Assert.That(updatedRun!.TotalListingsFound, Is.EqualTo(2));
            Assert.That(updatedRun.ListingsFilteredPreQueue, Is.EqualTo(1),
                "Sold listing should be filtered as terminal");
        });
    }

    [Test]
    public async Task Should_route_sold_heuristic_listings_to_scrape()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _dbContext.Listings.Add(new Listing
        {
            ListingId = "TRANS1", ScrapeJobId = 1,
            Title = "Was Active Now Sold", ListingStatus = "Active",
            Price = 100m, Condition = "USED", ShippingCost = 5m
        });
        await _dbContext.SaveChangesAsync();

        var soldSummary = CreateSummary("TRANS1", price: 100m, isSold: true);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new[] { soldSummary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        // Sold listing should have description fetched inline
        _webscraperClientMock.Verify(
            w => w.GetPageHtmlAsync(
                It.Is<string>(url => url.Contains("TRANS1")),
                It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Test]
    public async Task Should_update_existing_active_listing_from_summary_without_scraping()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _dbContext.Listings.Add(new Listing
        {
            ListingId = "UPD1", ScrapeJobId = 1,
            Title = "Active Item", ListingStatus = "Active",
            Price = 100m, Condition = "USED", ShippingCost = 5m
        });
        await _dbContext.SaveChangesAsync();

        var summary = CreateSummary("UPD1", price: 85m, isSold: false, shippingCost: 3m);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 2
                    ? new[] { summary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        var listing = _dbContext.Listings.First(l => l.ListingId == "UPD1");
        Assert.Multiple(() =>
        {
            Assert.That(listing.Price, Is.EqualTo(85m), "Price should be updated from summary");
            Assert.That(listing.ShippingCost, Is.EqualTo(3m), "Shipping should be updated from summary");
            Assert.That(listing.UpdatedUtc, Is.Not.Null, "UpdatedUtc should be set");
        });
    }

    [Test]
    public async Task Should_skip_unchanged_existing_active_listing()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _dbContext.Listings.Add(new Listing
        {
            ListingId = "SAME1", ScrapeJobId = 1,
            Title = "Active Item", ListingStatus = "Active",
            Price = 100m, Condition = "USED", ShippingCost = 5m
        });
        await _dbContext.SaveChangesAsync();

        var summary = CreateSummary("SAME1", price: 100m, isSold: false,
            condition: Condition.USED, shippingCost: 5m);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 2
                    ? new[] { summary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        var listing = _dbContext.Listings.First(l => l.ListingId == "SAME1");
        Assert.That(listing.UpdatedUtc, Is.Null, "UpdatedUtc should remain null when nothing changed");

        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(1);
        Assert.That(updatedRun!.ListingsSkipped, Is.EqualTo(1), "Unchanged listing should be counted as skipped");
    }

    [Test]
    public async Task Should_create_status_history_when_summary_price_changes()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _dbContext.Listings.Add(new Listing
        {
            ListingId = "HIST1", ScrapeJobId = 1,
            Title = "Active Item", ListingStatus = "Active",
            Price = 100m, Condition = "USED", ShippingCost = 5m
        });
        await _dbContext.SaveChangesAsync();

        var summary = CreateSummary("HIST1", price: 85m, isSold: false);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 2
                    ? new[] { summary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        var history = _dbContext.ListingStatusHistory.ToList();
        Assert.That(history, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(history[0].Source, Is.EqualTo("SummaryUpdate"));
            Assert.That(history[0].Price, Is.EqualTo(85m));
            Assert.That(history[0].ListingStatus, Is.EqualTo("Active"));
        });
    }

    [Test]
    public async Task Should_not_create_status_history_when_summary_unchanged()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _dbContext.Listings.Add(new Listing
        {
            ListingId = "NOHIST1", ScrapeJobId = 1,
            Title = "Active Item", ListingStatus = "Active",
            Price = 100m, Condition = "USED", ShippingCost = 5m
        });
        await _dbContext.SaveChangesAsync();

        var summary = CreateSummary("NOHIST1", price: 100m, isSold: false,
            condition: Condition.USED, shippingCost: 5m);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 2
                    ? new[] { summary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        var history = _dbContext.ListingStatusHistory.ToList();
        Assert.That(history, Has.Count.EqualTo(0), "No status history should be created when nothing changed");
    }

    [Test]
    public async Task Should_create_listing_from_summary_for_new_listings()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        var summary = CreateSummary("NEW1", price: 150m, isSold: false,
            condition: Condition.NEW, shippingCost: 3m);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 2
                    ? new[] { summary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.ListingId == "NEW1");
        Assert.That(listing, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(listing!.Title, Is.EqualTo("Test item NEW1"));
            Assert.That(listing.Price, Is.EqualTo(150m));
            Assert.That(listing.Currency, Is.EqualTo("GBP"));
            Assert.That(listing.ShippingCost, Is.EqualTo(3m));
            Assert.That(listing.ListingStatus, Is.EqualTo("Active"));
            Assert.That(listing.ScrapeJobId, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Should_create_initial_status_history_for_new_listing()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        var summary = CreateSummary("NEWHIST1", price: 100m, isSold: false);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 2
                    ? new[] { summary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        var history = _dbContext.ListingStatusHistory.ToList();
        Assert.That(history, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(history[0].Source, Is.EqualTo("InitialScrape"));
            Assert.That(history[0].ListingStatus, Is.EqualTo("Active"));
            Assert.That(history[0].Price, Is.EqualTo(100m));
        });
    }

    [Test]
    public async Task Should_create_status_history_when_active_listing_transitions_to_sold()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _dbContext.Listings.Add(new Listing
        {
            ListingId = "SOLD_TRANS1", ScrapeJobId = 1,
            Title = "Was Active", ListingStatus = "Active",
            Price = 100m, Condition = "USED", ShippingCost = 5m
        });
        await _dbContext.SaveChangesAsync();

        var soldSummary = CreateSummary("SOLD_TRANS1", price: 100m, isSold: true);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new[] { soldSummary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        var listing = await _dbContext.Listings.FirstAsync(l => l.ListingId == "SOLD_TRANS1");
        Assert.That(listing.ListingStatus, Is.EqualTo("Sold"));

        var history = _dbContext.ListingStatusHistory.ToList();
        Assert.That(history, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(history[0].Source, Is.EqualTo("StatusUpdate"));
            Assert.That(history[0].ListingStatus, Is.EqualTo("Sold"));
        });
    }

    [Test]
    public async Task Should_set_failed_status_on_exception()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        await CreateProcessor().Execute(run, job);

        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(1);
        Assert.Multiple(() =>
        {
            Assert.That(updatedRun!.Status, Is.EqualTo("Failed"));
            Assert.That(updatedRun.ErrorMessage, Is.EqualTo("Connection refused"));
            Assert.That(updatedRun.CompletedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Should_index_changed_listings()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _dbContext.Listings.Add(new Listing
        {
            ListingId = "IDX1", ScrapeJobId = 1,
            Title = "Indexed Item", ListingStatus = "Active",
            Price = 100m, Condition = "USED", ShippingCost = 5m
        });
        await _dbContext.SaveChangesAsync();

        var summary = CreateSummary("IDX1", price: 80m, isSold: false);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 2
                    ? new[] { summary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        _indexingServiceMock.Verify(i => i.Index(
            It.Is<Listing>(l => l.ListingId == "IDX1"),
            false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Should_not_index_unchanged_listings()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        _dbContext.Listings.Add(new Listing
        {
            ListingId = "NOIDX1", ScrapeJobId = 1,
            Title = "Unchanged Item", ListingStatus = "Active",
            Price = 100m, Condition = "USED", ShippingCost = 5m
        });
        await _dbContext.SaveChangesAsync();

        var summary = CreateSummary("NOIDX1", price: 100m, isSold: false, shippingCost: 5m);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 2
                    ? new[] { summary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        _indexingServiceMock.Verify(i => i.Index(
            It.IsAny<Listing>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Should_store_batch_id_when_provided_to_CreateRun()
    {
        var batchId = Guid.NewGuid();
        var job = CreateJobConfig();

        var run = await CreateProcessor().CreateRun(job, "Manual", batchId);

        var saved = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.That(saved!.BatchId, Is.EqualTo(batchId));
    }

    [Test]
    public async Task Should_allow_null_batch_id_in_CreateRun()
    {
        var job = CreateJobConfig();

        var run = await CreateProcessor().CreateRun(job, "Manual");

        var saved = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.That(saved!.BatchId, Is.Null);
    }

    [Test]
    public async Task Should_not_regress_sold_listing_back_to_active_on_rescrape()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        // Listing already Sold in DB
        _dbContext.Listings.Add(new Listing
        {
            ListingId = "REGRESS1", ScrapeJobId = 1,
            Title = "Previously Sold", ListingStatus = "Sold",
            Price = 100m, Condition = "USED"
        });
        await _dbContext.SaveChangesAsync();

        // Search returns this listing as active (eBay sometimes shows sold items in active results)
        var activeSummary = CreateSummary("REGRESS1", price: 95m, isSold: false);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 2
                    ? new[] { activeSummary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        var listing = await _dbContext.Listings.FirstAsync(l => l.ListingId == "REGRESS1");
        Assert.That(listing.ListingStatus, Is.EqualTo("Sold"),
            "Status should NOT regress from Sold back to Active");
    }

    [Test]
    public async Task Should_use_safe_status_transitions_when_saving_from_summaries()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        // Pre-existing listing already marked Sold
        _dbContext.Listings.Add(new Listing
        {
            ListingId = "CONCURRENT1", ScrapeJobId = 1,
            Title = "Concurrent Update", ListingStatus = "Sold",
            Price = 120m, Condition = "USED"
        });
        await _dbContext.SaveChangesAsync();

        // This listing appears in sold search results — classify routes to ToScrape
        var soldSummary = CreateSummary("CONCURRENT1", price: 110m, isSold: true);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new[] { soldSummary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        await CreateProcessor().Execute(run, job);

        var listing = await _dbContext.Listings.FirstAsync(l => l.ListingId == "CONCURRENT1");
        Assert.That(listing.ListingStatus, Is.EqualTo("Sold"),
            "Status should remain Sold — terminal listings are filtered in classify");
        Assert.That(listing.Price, Is.EqualTo(120m),
            "Price should not change — terminal listings are skipped entirely");
    }

    [TestCase(null, 100, Description = "Null LastRunUtc uses default")]
    [TestCase(-1, 6, Description = "1 day ago: ceil(~1) = 2, lookback = 3, max(5, 3*2) = 6")]
    [TestCase(-3, 10, Description = "3 days ago: ceil(~3) = 4, lookback = 5, max(5, 5*2) = 10")]
    [TestCase(-30, 64, Description = "30 days ago: ceil(~30) = 31, lookback = 32, max(5, 32*2) = 64")]
    [TestCase(-90, 100, Description = "90 days ago: min(184, 100) = 100")]
    public void Should_calculate_max_sold_pages_from_lookback(int? daysAgo, int expectedPages)
    {
        var lastRunUtc = daysAgo.HasValue ? DateTime.UtcNow.AddDays(daysAgo.Value) : (DateTime?)null;
        var result = ScrapeJobProcessor.CalculateMaxSoldPages(lastRunUtc);
        Assert.That(result, Is.EqualTo(expectedPages));
    }

    [Test]
    public async Task Should_update_job_LastRunUtc_on_successful_completion()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        await CreateProcessor().Execute(run, job);

        var updatedJob = await _dbContext.ScrapeJobs.FindAsync(1);
        Assert.That(updatedJob!.LastRunUtc, Is.Not.Null,
            "Job LastRunUtc should be updated after successful run");
        Assert.That(updatedJob.LastRunUtc, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromSeconds(5)));
    }
}
