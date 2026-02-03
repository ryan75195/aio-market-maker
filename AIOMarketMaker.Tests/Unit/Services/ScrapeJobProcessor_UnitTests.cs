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

    [Test]
    public async Task Should_complete_run_when_no_listings_found()
    {
        var scrapeRun = new ScrapeRun
        {
            Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
            TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

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
        var scrapeRun = new ScrapeRun
        {
            Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
            TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        var callCount = 0;
        var mockSummary = new Mock<IEbayProductSummary>();
        mockSummary.Setup(s => s.ListingId).Returns("ABC123");
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                // Call 1: sold page 1 (empty) -> stops sold search
                // Call 2: active page 1 (returns listing)
                // Call 3: active page 2 (empty) -> stops active search
                return callCount == 2
                    ? new[] { mockSummary.Object }
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
    public async Task Should_skip_terminal_listings_but_rescrape_active()
    {
        var scrapeRun = new ScrapeRun
        {
            Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
            TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);

        _dbContext.Listings.Add(new Listing
        {
            ListingId = "ACTIVE1", ScrapeJobId = 1,
            Title = "Active Item", ListingStatus = "Active"
        });
        _dbContext.Listings.Add(new Listing
        {
            ListingId = "SOLD1", ScrapeJobId = 1,
            Title = "Sold Item", ListingStatus = "Sold"
        });
        await _dbContext.SaveChangesAsync();

        var callCount = 0;
        var activeSummary = new Mock<IEbayProductSummary>();
        activeSummary.Setup(s => s.ListingId).Returns("ACTIVE1");
        var soldSummary = new Mock<IEbayProductSummary>();
        soldSummary.Setup(s => s.ListingId).Returns("SOLD1");

        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 2
                    ? new[] { activeSummary.Object, soldSummary.Object }
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

        _webscraperClientMock.Verify(
            w => w.EnqueueScrapeWork(
                It.Is<IEnumerable<ScrapeWorkItem>>(items =>
                    items.Count() == 1
                    && items.First().ListingId == "ACTIVE1"),
                1, 1, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Should_set_failed_status_and_rethrow_on_exception()
    {
        var scrapeRun = new ScrapeRun
        {
            Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
            TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

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
