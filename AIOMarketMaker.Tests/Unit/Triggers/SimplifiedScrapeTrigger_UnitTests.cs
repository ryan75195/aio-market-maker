using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Functions.Worker;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Azure;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Triggers;
using AIOMarketMaker.Tests.Utils;
using AIOMarketMaker.Models.Ebay;
using AngleSharp.Dom;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace AIOMarketMaker.Tests.Unit.Triggers;

[TestFixture]
[Category("Unit")]
public class SimplifiedScrapeTrigger_UnitTests
{
    private Mock<ILogger<SimplifiedScrapeTrigger>> _loggerMock;
    private EtlDbContext _dbContext;
    private Mock<IWebscraperClient> _webscraperClientMock;
    private Mock<ISearchParser> _searchParserMock;
    private Mock<QueueServiceClient> _queueServiceMock;
    private Mock<QueueClient> _queueClientMock;

    [SetUp]
    public void SetUp()
    {
        _loggerMock = new Mock<ILogger<SimplifiedScrapeTrigger>>();
        _dbContext = InMemoryDbContextFactory.Create();
        _webscraperClientMock = new Mock<IWebscraperClient>();
        _searchParserMock = new Mock<ISearchParser>();
        _queueServiceMock = new Mock<QueueServiceClient>();
        _queueClientMock = new Mock<QueueClient>();

        _queueServiceMock
            .Setup(q => q.GetQueueClient("scrape-work"))
            .Returns(_queueClientMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext?.Dispose();
    }

    [Test]
    public void Should_construct_with_all_dependencies()
    {
        // Act
        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object,
            _dbContext,
            _webscraperClientMock.Object,
            _searchParserMock.Object,
            _queueServiceMock.Object);

        // Assert
        Assert.That(trigger, Is.Not.Null);
    }

    [Test]
    public async Task RunScrapeForJobAsync_should_create_scrape_run_and_enqueue_listings()
    {
        // Arrange
        var jobId = 1;
        var searchTerm = "test product";
        var triggerType = "Manual";

        // Create a scrape job in the database
        var scrapeJob = new ScrapeJob { Id = jobId, SearchTerm = searchTerm, IsEnabled = true };
        _dbContext.ScrapeJobs.Add(scrapeJob);
        await _dbContext.SaveChangesAsync();

        // Setup webscraper client to return HTML
        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<object>>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html><body>Mock search results</body></html>");

        // Setup search parser to return mock product summaries
        var mockProducts = new List<IEbayProductSummary>
        {
            new EbayProductSummary("itm001", "Product 1", 10.00m, "GBP", 1.00m, "url1", BuyingFormat.BUY_NOW, Condition.USED, null, null),
            new EbayProductSummary("itm002", "Product 2", 20.00m, "GBP", 2.00m, "url2", BuyingFormat.BUY_NOW, Condition.NEW, null, null),
            new EbayProductSummary("itm003", "Product 3", 30.00m, "GBP", 3.00m, "url3", BuyingFormat.AUCTION, Condition.USED, null, null)
        };
        _searchParserMock
            .Setup(s => s.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(mockProducts);

        // Setup queue client to accept messages
        _queueClientMock
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Response.FromValue(
                QueuesModelFactory.SendReceipt("messageId", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "popReceipt", DateTimeOffset.UtcNow.AddMinutes(1)),
                Mock.Of<Response>()));

        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object,
            _dbContext,
            _webscraperClientMock.Object,
            _searchParserMock.Object,
            _queueServiceMock.Object);

        // Act
        var result = await trigger.RunScrapeForJobAsync(jobId, searchTerm, triggerType);

        // Assert
        Assert.Multiple(() =>
        {
            // Should return the count of listings enqueued
            Assert.That(result, Is.EqualTo(3));

            // Verify ScrapeRun was created
            var scrapeRun = _dbContext.ScrapeRuns.FirstOrDefault();
            Assert.That(scrapeRun, Is.Not.Null);
            Assert.That(scrapeRun!.JobId, Is.EqualTo(jobId));
            Assert.That(scrapeRun.TriggerType, Is.EqualTo(triggerType));
            Assert.That(scrapeRun.Status, Is.EqualTo("Indexing"));
            Assert.That(scrapeRun.TotalListingsFound, Is.EqualTo(3));

            // Verify ScrapeRunListings were created
            var scrapeRunListings = _dbContext.ScrapeRunListings.ToList();
            Assert.That(scrapeRunListings.Count, Is.EqualTo(3));
            Assert.That(scrapeRunListings.Select(l => l.ListingId), Is.EquivalentTo(new[] { "itm001", "itm002", "itm003" }));
        });

        // Verify queue messages were sent for each listing (listing + description = 2 per listing = 6 total)
        _queueClientMock.Verify(q => q.SendMessageAsync(It.IsAny<string>()),
            Times.Exactly(6));
    }

    [Test]
    public async Task RunScrapeForJobAsync_should_skip_terminal_statuses_but_include_active_for_rescrape()
    {
        // Arrange
        var jobId = 1;
        var searchTerm = "test product";

        var scrapeJob = new ScrapeJob { Id = jobId, SearchTerm = searchTerm, IsEnabled = true };
        _dbContext.ScrapeJobs.Add(scrapeJob);

        // Create listings with different statuses
        var activeListing = new Listing { ListingId = "itm001", ScrapeJobId = jobId, ListingStatus = "Active" };
        var soldListing = new Listing { ListingId = "itm002", ScrapeJobId = jobId, ListingStatus = "Sold" };
        var endedListing = new Listing { ListingId = "itm003", ScrapeJobId = jobId, ListingStatus = "Ended" };
        var outOfStockListing = new Listing { ListingId = "itm004", ScrapeJobId = jobId, ListingStatus = "OutOfStock" };
        // itm005 doesn't exist in DB yet - it's a new listing
        var nullStatusListing = new Listing { ListingId = "itm006", ScrapeJobId = jobId, ListingStatus = null };

        _dbContext.Listings.AddRange(activeListing, soldListing, endedListing, outOfStockListing, nullStatusListing);
        await _dbContext.SaveChangesAsync();

        // Setup webscraper client to return HTML
        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<object>>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html><body>Mock</body></html>");

        // Parser returns all 6 listing IDs (simulating search results)
        var mockProducts = new List<IEbayProductSummary>
        {
            new EbayProductSummary("itm001", "Active Product", 10m, "GBP", 0m, "url1", BuyingFormat.BUY_NOW, Condition.USED, null, null),
            new EbayProductSummary("itm002", "Sold Product", 20m, "GBP", 0m, "url2", BuyingFormat.BUY_NOW, Condition.USED, null, null),
            new EbayProductSummary("itm003", "Ended Product", 30m, "GBP", 0m, "url3", BuyingFormat.BUY_NOW, Condition.USED, null, null),
            new EbayProductSummary("itm004", "OutOfStock Product", 40m, "GBP", 0m, "url4", BuyingFormat.BUY_NOW, Condition.USED, null, null),
            new EbayProductSummary("itm005", "New Product", 50m, "GBP", 0m, "url5", BuyingFormat.BUY_NOW, Condition.USED, null, null),
            new EbayProductSummary("itm006", "Null Status Product", 60m, "GBP", 0m, "url6", BuyingFormat.BUY_NOW, Condition.USED, null, null),
        };
        _searchParserMock.Setup(s => s.ParseSearchResults(It.IsAny<IDocument>())).Returns(mockProducts);

        _queueClientMock
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Response.FromValue(
                QueuesModelFactory.SendReceipt("id", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "pop", DateTimeOffset.UtcNow.AddMinutes(1)),
                Mock.Of<Response>()));

        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object, _dbContext, _webscraperClientMock.Object,
            _searchParserMock.Object, _queueServiceMock.Object);

        // Act
        var result = await trigger.RunScrapeForJobAsync(jobId, searchTerm, "Manual");

        // Assert
        // itm001 (Active) - INCLUDED (re-scrape for price updates)
        // itm002 (Sold) - EXCLUDED (terminal)
        // itm003 (Ended) - EXCLUDED (terminal)
        // itm004 (OutOfStock) - EXCLUDED (terminal)
        // itm005 (new/not in DB) - INCLUDED (new listing)
        // itm006 (null status) - INCLUDED (treated as new listing needing scrape)
        Assert.That(result, Is.EqualTo(3), "Should include Active, new, and null-status listings, exclude terminal statuses");

        var enqueuedListings = _dbContext.ScrapeRunListings.Select(l => l.ListingId).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(enqueuedListings, Does.Contain("itm001"), "Active listing should be re-scraped");
            Assert.That(enqueuedListings, Does.Contain("itm005"), "New listing should be scraped");
            Assert.That(enqueuedListings, Does.Contain("itm006"), "Null-status listing should be scraped");
            Assert.That(enqueuedListings, Does.Not.Contain("itm002"), "Sold listing should be skipped");
            Assert.That(enqueuedListings, Does.Not.Contain("itm003"), "Ended listing should be skipped");
            Assert.That(enqueuedListings, Does.Not.Contain("itm004"), "OutOfStock listing should be skipped");
        });
    }

    [Test]
    public async Task RunNightly_should_call_RunScrapeForAllEnabledJobsAsync()
    {
        // Arrange - Create two enabled jobs and one disabled job
        var job1 = new ScrapeJob { Id = 1, SearchTerm = "product 1", IsEnabled = true };
        var job2 = new ScrapeJob { Id = 2, SearchTerm = "product 2", IsEnabled = true };
        var job3 = new ScrapeJob { Id = 3, SearchTerm = "disabled product", IsEnabled = false };
        _dbContext.ScrapeJobs.AddRange(job1, job2, job3);
        await _dbContext.SaveChangesAsync();

        // Setup webscraper client to return HTML
        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<object>>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html><body>Mock search results</body></html>");

        // Setup search parser to return empty list (simplify test)
        _searchParserMock
            .Setup(s => s.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(new List<IEbayProductSummary>());

        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object,
            _dbContext,
            _webscraperClientMock.Object,
            _searchParserMock.Object,
            _queueServiceMock.Object);

        // Act
        await trigger.RunNightly(null!);

        // Assert - Two ScrapeRuns should be created (one for each enabled job)
        var scrapeRuns = await _dbContext.ScrapeRuns.ToListAsync();
        Assert.That(scrapeRuns.Count, Is.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(scrapeRuns.Select(r => r.JobId), Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(scrapeRuns.All(r => r.TriggerType == "Nightly"), Is.True);
        });
    }

    [Test]
    public async Task RunManual_should_return_OK_on_success()
    {
        // Arrange - Create an enabled job
        var job = new ScrapeJob { Id = 1, SearchTerm = "test product", IsEnabled = true };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        // Setup webscraper client to return HTML
        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<object>>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html><body>Mock search results</body></html>");

        // Setup search parser to return mock products
        var mockProducts = new List<IEbayProductSummary>
        {
            new EbayProductSummary("itm001", "Product 1", 10.00m, "GBP", 1.00m, "url1", BuyingFormat.BUY_NOW, Condition.USED, null, null)
        };
        _searchParserMock
            .Setup(s => s.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(mockProducts);

        // Setup queue client to accept messages
        _queueClientMock
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Response.FromValue(
                QueuesModelFactory.SendReceipt("messageId", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "popReceipt", DateTimeOffset.UtcNow.AddMinutes(1)),
                Mock.Of<Response>()));

        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object,
            _dbContext,
            _webscraperClientMock.Object,
            _searchParserMock.Object,
            _queueServiceMock.Object);

        // Create mock HTTP request with empty body (no specific jobId)
        var httpRequest = MockHttpRequestData.CreateEmpty();

        // Act
        var response = await trigger.RunManual(httpRequest);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task RunManual_should_run_specific_job_when_jobId_provided()
    {
        // Arrange - Create two enabled jobs
        var job1 = new ScrapeJob { Id = 1, SearchTerm = "product 1", IsEnabled = true };
        var job2 = new ScrapeJob { Id = 2, SearchTerm = "product 2", IsEnabled = true };
        _dbContext.ScrapeJobs.AddRange(job1, job2);
        await _dbContext.SaveChangesAsync();

        // Setup webscraper client to return HTML
        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<object>>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html><body>Mock search results</body></html>");

        // Setup search parser to return empty list
        _searchParserMock
            .Setup(s => s.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(new List<IEbayProductSummary>());

        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object,
            _dbContext,
            _webscraperClientMock.Object,
            _searchParserMock.Object,
            _queueServiceMock.Object);

        // Create mock HTTP request with specific jobId
        var httpRequest = MockHttpRequestData.Create(new { jobId = 2 });

        // Act
        var response = await trigger.RunManual(httpRequest);

        // Assert - Only one ScrapeRun should be created for job 2
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var scrapeRuns = await _dbContext.ScrapeRuns.ToListAsync();
        Assert.That(scrapeRuns.Count, Is.EqualTo(1));
        Assert.That(scrapeRuns[0].JobId, Is.EqualTo(2));
    }

    [Test]
    public async Task RunScrapeForJobAsync_should_search_multiple_pages_until_no_results()
    {
        // Arrange
        var jobId = 1;
        var scrapeJob = new ScrapeJob { Id = jobId, SearchTerm = "test", IsEnabled = true };
        _dbContext.ScrapeJobs.Add(scrapeJob);
        await _dbContext.SaveChangesAsync();

        // Track which URLs were called
        var calledUrls = new List<string>();
        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>>(),
                It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<object>, string, TimeSpan?, CancellationToken>((url, _, _, _, _) => calledUrls.Add(url))
            .ReturnsAsync("<html></html>");

        // Setup parser: Page 1 = 2 products, Page 2 = 1 product, Page 3 = 0 products
        var callCount = 0;
        _searchParserMock
            .Setup(s => s.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new List<IEbayProductSummary>
                    {
                        new EbayProductSummary("itm001", "P1", 10m, "GBP", 0m, "u1", BuyingFormat.BUY_NOW, Condition.USED, null, null),
                        new EbayProductSummary("itm002", "P2", 20m, "GBP", 0m, "u2", BuyingFormat.BUY_NOW, Condition.USED, null, null),
                    },
                    2 => new List<IEbayProductSummary>
                    {
                        new EbayProductSummary("itm003", "P3", 30m, "GBP", 0m, "u3", BuyingFormat.BUY_NOW, Condition.USED, null, null),
                    },
                    _ => new List<IEbayProductSummary>()
                };
            });

        _queueClientMock
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Response.FromValue(
                QueuesModelFactory.SendReceipt("id", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "pop", DateTimeOffset.UtcNow.AddMinutes(1)),
                Mock.Of<Response>()));

        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object, _dbContext, _webscraperClientMock.Object,
            _searchParserMock.Object, _queueServiceMock.Object);

        // Act
        var result = await trigger.RunScrapeForJobAsync(jobId, "test", "Manual");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(3), "Should find all 3 listings across pages");
            Assert.That(calledUrls.Count, Is.EqualTo(3), "Should call 3 pages (page 1, 2, 3)");
            Assert.That(calledUrls[0], Does.Contain("_pgn=1").Or.Not.Contain("_pgn"), "First call should be page 1");
            Assert.That(calledUrls[1], Does.Contain("_pgn=2"), "Second call should be page 2");
            Assert.That(calledUrls[2], Does.Contain("_pgn=3"), "Third call should be page 3");
        });
    }
}
