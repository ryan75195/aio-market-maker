using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Endpoints;
using AIOMarketMaker.Etl.Services;
using AIOMarketMaker.Tests.Utils;
using AIOMarketMaker.Models.Ebay;
using AngleSharp.Dom;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ListingProcessorService_UnitTests
{
    private EtlDbContext _dbContext = null!;
    private Mock<BlobServiceClient> _blobServiceMock = null!;
    private Mock<IListingParser> _listingParserMock = null!;
    private Mock<IScrapeRunCounterService> _counterServiceMock = null!;
    private Mock<IListingIndexingService> _indexingServiceMock = null!;
    private Mock<ILogger<ListingProcessorService>> _loggerMock = null!;

    [SetUp]
    public void Setup()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _blobServiceMock = new Mock<BlobServiceClient>();
        _listingParserMock = new Mock<IListingParser>();
        _counterServiceMock = new Mock<IScrapeRunCounterService>();
        _indexingServiceMock = new Mock<IListingIndexingService>();
        _indexingServiceMock
            .Setup(i => i.Index(It.IsAny<Listing>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexingResult(IndexingAction.Embedded));
        _loggerMock = new Mock<ILogger<ListingProcessorService>>();
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    private ListingProcessorService CreateService() =>
        new(_blobServiceMock.Object, _dbContext, _listingParserMock.Object,
            _counterServiceMock.Object, _indexingServiceMock.Object, _loggerMock.Object);

    [Test]
    public async Task Should_add_new_listing_and_create_initial_history()
    {
        _dbContext.ScrapeRuns.Add(new ScrapeRun { Id = 1, Status = "Running" });
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "test" });
        _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
        {
            ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "ABC123", Status = "Pending"
        });
        await _dbContext.SaveChangesAsync();

        SetupBlobWithContent("<html></html>");
        SetupParserWithListing("ABC123", "Test Product", 99.99m, EbayListingStatus.Active);

        var request = new ProcessListingRequest(1, 0, "ABC123", 1, "1/ABC123/listing.html");

        var result = await CreateService().Process(request);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.Status, Is.EqualTo("added"));
        });

        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.ListingId == "ABC123");
        Assert.That(listing, Is.Not.Null);
        Assert.That(listing!.Title, Is.EqualTo("Test Product"));

        var history = await _dbContext.ListingStatusHistory.ToListAsync();
        Assert.That(history, Has.Count.EqualTo(1));
        Assert.That(history[0].Source, Is.EqualTo("InitialScrape"));
    }

    private void SetupBlobWithContent(string htmlContent)
    {
        var mockBlobContainerClient = new Mock<BlobContainerClient>();
        var mockBlobClient = new Mock<BlobClient>();

        mockBlobClient
            .Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Azure.Response.FromValue(true, Mock.Of<Azure.Response>()));

        var blobContent = BinaryData.FromString(htmlContent);
        var blobDownloadResult = BlobsModelFactory.BlobDownloadResult(content: blobContent);

        mockBlobClient
            .Setup(b => b.DownloadContentAsync())
            .ReturnsAsync(Azure.Response.FromValue(blobDownloadResult, Mock.Of<Azure.Response>()));
        mockBlobClient
            .Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Azure.Response.FromValue(blobDownloadResult, Mock.Of<Azure.Response>()));

        mockBlobContainerClient
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Returns(mockBlobClient.Object);

        _blobServiceMock
            .Setup(s => s.GetBlobContainerClient("html"))
            .Returns(mockBlobContainerClient.Object);
    }

    [Test]
    public async Task Should_index_new_listing_after_upsert()
    {
        _dbContext.ScrapeRuns.Add(new ScrapeRun { Id = 1, Status = "Running" });
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "test" });
        _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
        {
            ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "NEW1", Status = "Pending"
        });
        await _dbContext.SaveChangesAsync();

        SetupBlobWithContent("<html></html>");
        SetupParserWithListing("NEW1", "New Product", 49.99m, EbayListingStatus.Active);

        var request = new ProcessListingRequest(1, 0, "NEW1", 1, "1/NEW1/listing.html");

        await CreateService().Process(request);

        _indexingServiceMock.Verify(i => i.Index(
            It.Is<Listing>(l => l.ListingId == "NEW1"),
            true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Should_index_updated_listing_with_is_new_false()
    {
        _dbContext.ScrapeRuns.Add(new ScrapeRun { Id = 1, Status = "Running" });
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "test" });
        _dbContext.Listings.Add(new Listing
        {
            ListingId = "UPD1", ScrapeJobId = 1, Title = "Existing Item",
            ListingStatus = "Active", Price = 100m
        });
        _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
        {
            ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "UPD1", Status = "Pending"
        });
        await _dbContext.SaveChangesAsync();

        SetupBlobWithContent("<html></html>");
        SetupParserWithListing("UPD1", "Updated Item", 85m, EbayListingStatus.Active);

        var request = new ProcessListingRequest(1, 0, "UPD1", 1, "1/UPD1/listing.html");

        await CreateService().Process(request);

        _indexingServiceMock.Verify(i => i.Index(
            It.Is<Listing>(l => l.ListingId == "UPD1"),
            false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Should_not_mark_complete_when_indexing_fails()
    {
        _dbContext.ScrapeRuns.Add(new ScrapeRun { Id = 1, Status = "Running" });
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "test" });
        _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
        {
            ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "FAIL1", Status = "Pending"
        });
        await _dbContext.SaveChangesAsync();

        SetupBlobWithContent("<html></html>");
        SetupParserWithListing("FAIL1", "Failing Item", 50m, EbayListingStatus.Active);

        _indexingServiceMock
            .Setup(i => i.Index(It.IsAny<Listing>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Pinecone timeout"));

        var request = new ProcessListingRequest(1, 0, "FAIL1", 1, "1/FAIL1/listing.html");

        Assert.ThrowsAsync<HttpRequestException>(() => CreateService().Process(request));

        var srl = await _dbContext.ScrapeRunListings.FirstAsync(s => s.ListingId == "FAIL1");
        Assert.That(srl.Status, Is.Not.EqualTo("Complete"),
            "ScrapeRunListing should NOT be marked Complete when indexing fails");

        // But the listing itself should still be saved (we don't lose parsed data)
        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.ListingId == "FAIL1");
        Assert.That(listing, Is.Not.Null, "Listing should be persisted even when indexing fails");
    }

    private void SetupParserWithListing(string id, string title, decimal price,
        EbayListingStatus status)
    {
        var parsed = new ExtractedEbayListing(
            id: id, title: title, price: price, currency: "GBP",
            shippingCost: 5m, Condition: Condition.NEW,
            images: new[] { "http://example.com/img.jpg" },
            listingStatus: status, purchaseFormat: PurchaseFormat.BuyItNow,
            ItemSpecifics: null, descriptionSource: null, SoldDateUtc: null,
            Location: "London", Url: $"http://www.ebay.co.uk/itm/{id}");

        _listingParserMock
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>()))
            .Returns(parsed);
    }
}
