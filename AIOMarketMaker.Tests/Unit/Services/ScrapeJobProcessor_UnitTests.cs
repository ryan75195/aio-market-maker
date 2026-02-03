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

[TestFixture]
[Category("Unit")]
public class ScrapeJobProcessor_UnitTests
{
    private EtlDbContext _dbContext = null!;
    private Mock<ILogger<ScrapeJobProcessor>> _loggerMock = null!;
    private Mock<IWebscraperClient> _webscraperClientMock = null!;
    private Mock<ISearchParser> _searchParserMock = null!;
    private Mock<IEbayUrlBuilder> _urlBuilderMock = null!;

    [SetUp]
    public void Setup()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "Test" });
        _dbContext.SaveChanges();

        _loggerMock = new Mock<ILogger<ScrapeJobProcessor>>();
        _webscraperClientMock = new Mock<IWebscraperClient>();
        _searchParserMock = new Mock<ISearchParser>();
        _urlBuilderMock = new Mock<IEbayUrlBuilder>();

        // Default: empty search results (stops pagination)
        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html></html>");

        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(Enumerable.Empty<IEbayProductSummary>());

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
        _searchParserMock.Object, _urlBuilderMock.Object);

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

    [Test]
    public async Task Should_complete_run_when_no_listings_found()
    {
        CreateAndSeedScrapeRun();

        var message = new ScrapeJobMessage(1, 1, "Test", "Manual");

        await CreateProcessor().Process(message);

        var run = await _dbContext.ScrapeRuns.FindAsync(1);
        Assert.Multiple(() =>
        {
            Assert.That(run!.Status, Is.EqualTo("Completed"));
            Assert.That(run.CurrentPhase, Is.EqualTo("Completed"));
            Assert.That(run.CompletedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Should_enqueue_via_webscraper_client_for_new_listings()
    {
        CreateAndSeedScrapeRun();

        var summary = CreateSummary("ABC123", isSold: false);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                // Call 1: sold page 1 (empty) -> stops sold search
                // Call 2: active page 1 (returns listing)
                // Call 3: active page 2 (empty) -> stops active search
                return callCount == 2
                    ? new[] { summary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        var message = new ScrapeJobMessage(1, 1, "Test", "Manual");

        await CreateProcessor().Process(message);

        _webscraperClientMock.Verify(
            w => w.EnqueueScrapeWork(
                It.Is<IEnumerable<ScrapeWorkItem>>(items =>
                    items.Count() == 1
                    && items.First().ListingId == "ABC123"),
                1, 1, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Should_skip_terminal_listings_and_update_active_from_summary()
    {
        CreateAndSeedScrapeRun();

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
                // Call 1: sold page 1 (empty) -> stops sold search
                // Call 2: active page 1 (returns both listings)
                // Call 3: active page 2 (empty) -> stops active search
                return callCount == 2
                    ? new[] { activeSummary, soldSummary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        var message = new ScrapeJobMessage(1, 1, "Test", "Manual");

        await CreateProcessor().Process(message);

        var run = await _dbContext.ScrapeRuns.FindAsync(1);
        Assert.Multiple(() =>
        {
            Assert.That(run!.TotalListingsFound, Is.EqualTo(2));
            Assert.That(run.ListingsFilteredPreQueue, Is.EqualTo(1),
                "Sold listing should be filtered as terminal");
        });

        // Active listing routes to summary update, NOT to EnqueueScrapeWork
        // (no new listings to scrape — only terminal + existing active)
        _webscraperClientMock.Verify(
            w => w.EnqueueScrapeWork(
                It.IsAny<IEnumerable<ScrapeWorkItem>>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Should_route_sold_heuristic_listings_to_scrape()
    {
        CreateAndSeedScrapeRun();

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
                // Call 1: sold page 1 (returns the sold listing)
                // Call 2: sold page 2 (empty) -> stops sold search
                // Call 3: active page 1 (empty) -> stops active search
                return callCount == 1
                    ? new[] { soldSummary }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        var message = new ScrapeJobMessage(1, 1, "Test", "Manual");

        await CreateProcessor().Process(message);

        _webscraperClientMock.Verify(
            w => w.EnqueueScrapeWork(
                It.Is<IEnumerable<ScrapeWorkItem>>(items =>
                    items.Count() == 1
                    && items.First().ListingId == "TRANS1"),
                1, 1, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Should_update_existing_active_listing_from_summary_without_scraping()
    {
        CreateAndSeedScrapeRun();

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

        var message = new ScrapeJobMessage(1, 1, "Test", "Manual");

        await CreateProcessor().Process(message);

        var listing = _dbContext.Listings.First(l => l.ListingId == "UPD1");
        Assert.Multiple(() =>
        {
            Assert.That(listing.Price, Is.EqualTo(85m), "Price should be updated from summary");
            Assert.That(listing.ShippingCost, Is.EqualTo(3m), "Shipping should be updated from summary");
            Assert.That(listing.UpdatedUtc, Is.Not.Null, "UpdatedUtc should be set");
        });

        _webscraperClientMock.Verify(
            w => w.EnqueueScrapeWork(
                It.IsAny<IEnumerable<ScrapeWorkItem>>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Should_skip_unchanged_existing_active_listing()
    {
        CreateAndSeedScrapeRun();

        _dbContext.Listings.Add(new Listing
        {
            ListingId = "SAME1", ScrapeJobId = 1,
            Title = "Active Item", ListingStatus = "Active",
            Price = 100m, Condition = "USED", ShippingCost = 5m
        });
        await _dbContext.SaveChangesAsync();

        // Summary with identical values
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

        var message = new ScrapeJobMessage(1, 1, "Test", "Manual");

        await CreateProcessor().Process(message);

        var listing = _dbContext.Listings.First(l => l.ListingId == "SAME1");
        Assert.That(listing.UpdatedUtc, Is.Null, "UpdatedUtc should remain null when nothing changed");

        _webscraperClientMock.Verify(
            w => w.EnqueueScrapeWork(
                It.IsAny<IEnumerable<ScrapeWorkItem>>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var run = await _dbContext.ScrapeRuns.FindAsync(1);
        Assert.That(run!.ListingsSkipped, Is.EqualTo(1), "Unchanged listing should be counted as skipped");
    }

    [Test]
    public async Task Should_create_status_history_when_summary_price_changes()
    {
        CreateAndSeedScrapeRun();

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

        var message = new ScrapeJobMessage(1, 1, "Test", "Manual");

        await CreateProcessor().Process(message);

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
        CreateAndSeedScrapeRun();

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

        var message = new ScrapeJobMessage(1, 1, "Test", "Manual");

        await CreateProcessor().Process(message);

        var history = _dbContext.ListingStatusHistory.ToList();
        Assert.That(history, Has.Count.EqualTo(0), "No status history should be created when nothing changed");
    }

    [Test]
    public async Task Should_prefer_sold_summary_when_listing_appears_in_both_searches()
    {
        CreateAndSeedScrapeRun();

        // Existing active listing that appears in both sold and active search results
        _dbContext.Listings.Add(new Listing
        {
            ListingId = "DUAL1", ScrapeJobId = 1,
            Title = "Appears In Both", ListingStatus = "Active",
            Price = 100m, Condition = "USED", ShippingCost = 5m
        });
        await _dbContext.SaveChangesAsync();

        var soldSummary = CreateSummary("DUAL1", price: 100m, isSold: true);
        var activeSummary = CreateSummary("DUAL1", price: 100m, isSold: false);

        var callCount = 0;
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                // Call 1: sold page 1 (returns listing as sold)
                // Call 2: sold page 2 (empty) -> stops sold search
                // Call 3: active page 1 (returns same listing as active)
                // Call 4: active page 2 (empty) -> stops active search
                return callCount switch
                {
                    1 => new[] { soldSummary },
                    3 => new[] { activeSummary },
                    _ => Enumerable.Empty<IEbayProductSummary>()
                };
            });

        var message = new ScrapeJobMessage(1, 1, "Test", "Manual");

        await CreateProcessor().Process(message);

        // Sold wins via TryAdd order (sold first in soldSummaries.Concat(activeSummaries))
        // Existing active listing appearing as sold should be routed to full scrape, not summary update
        _webscraperClientMock.Verify(
            w => w.EnqueueScrapeWork(
                It.Is<IEnumerable<ScrapeWorkItem>>(items =>
                    items.Count() == 1
                    && items.First().ListingId == "DUAL1"),
                1, 1, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Should_set_failed_status_and_rethrow_on_exception()
    {
        CreateAndSeedScrapeRun();

        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var message = new ScrapeJobMessage(1, 1, "Test", "Manual");

        Assert.ThrowsAsync<HttpRequestException>(() => CreateProcessor().Process(message));

        var run = await _dbContext.ScrapeRuns.FindAsync(1);
        Assert.Multiple(() =>
        {
            Assert.That(run!.Status, Is.EqualTo("Failed"));
            Assert.That(run.ErrorMessage, Is.EqualTo("Connection refused"));
            Assert.That(run.CompletedUtc, Is.Not.Null);
        });
    }
}
