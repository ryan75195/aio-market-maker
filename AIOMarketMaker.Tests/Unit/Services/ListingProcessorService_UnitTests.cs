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
            .Setup(s => s.Index(It.IsAny<Listing>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
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

    private async Task SeedListingAndRun(string listingId = "ABC123")
    {
        _dbContext.ScrapeRuns.Add(new ScrapeRun { Id = 1, Status = "Running" });
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "test" });
        _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
        {
            ScrapeRunId = 1, ScrapeJobId = 1, ListingId = listingId, Status = "Pending"
        });
        _dbContext.Listings.Add(new Listing
        {
            ListingId = listingId, ScrapeJobId = 1, Title = "Test Product",
            Price = 99.99m, ListingStatus = "Active", DescriptionStatus = "pending"
        });
        await _dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task Should_set_description_when_blob_exists()
    {
        await SeedListingAndRun();
        SetupBlobWithContent("<html><div class=\"x-item-description-child\">Great product</div></html>");
        _listingParserMock
            .Setup(p => p.ParseDescription(It.IsAny<IDocument>()))
            .Returns("Great product");

        var request = new ProcessListingRequest(1, 0, "ABC123", 1, "1/ABC123/description.html");

        var result = await CreateService().Process(request);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.Status, Is.EqualTo("complete"));
        });

        var listing = await _dbContext.Listings.FirstAsync(l => l.ListingId == "ABC123");
        Assert.Multiple(() =>
        {
            Assert.That(listing.Description, Is.EqualTo("Great product"));
            Assert.That(listing.DescriptionStatus, Is.EqualTo("complete"));
        });
    }

    [Test]
    public async Task Should_set_description_status_missing_when_blob_not_found()
    {
        await SeedListingAndRun();
        SetupBlobNotFound();

        var request = new ProcessListingRequest(1, 0, "ABC123", 1, "1/ABC123/description.html");

        var result = await CreateService().Process(request);

        Assert.That(result.Success, Is.True);

        var listing = await _dbContext.Listings.FirstAsync(l => l.ListingId == "ABC123");
        Assert.That(listing.DescriptionStatus, Is.EqualTo("missing"));
    }

    [Test]
    public async Task Should_set_description_status_missing_when_parse_returns_null()
    {
        await SeedListingAndRun();
        SetupBlobWithContent("<html></html>");
        _listingParserMock
            .Setup(p => p.ParseDescription(It.IsAny<IDocument>()))
            .Returns((string?)null);

        var request = new ProcessListingRequest(1, 0, "ABC123", 1, "1/ABC123/description.html");

        var result = await CreateService().Process(request);

        Assert.That(result.Success, Is.True);

        var listing = await _dbContext.Listings.FirstAsync(l => l.ListingId == "ABC123");
        Assert.That(listing.DescriptionStatus, Is.EqualTo("missing"));
    }

    [Test]
    public async Task Should_skip_already_processed_listing()
    {
        _dbContext.ScrapeRuns.Add(new ScrapeRun { Id = 1, Status = "Running" });
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "test" });
        _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
        {
            ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "ABC123", Status = "Complete"
        });
        await _dbContext.SaveChangesAsync();

        var request = new ProcessListingRequest(1, 0, "ABC123", 1, "1/ABC123/description.html");

        var result = await CreateService().Process(request);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.Status, Is.EqualTo("skipped"));
        });
    }

    [Test]
    public async Task Should_fail_when_listing_not_found()
    {
        _dbContext.ScrapeRuns.Add(new ScrapeRun { Id = 1, Status = "Running" });
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "test" });
        _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
        {
            ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "MISSING1", Status = "Pending"
        });
        await _dbContext.SaveChangesAsync();

        var request = new ProcessListingRequest(1, 0, "MISSING1", 1, "1/MISSING1/description.html");

        var result = await CreateService().Process(request);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Status, Is.EqualTo("failed"));
            Assert.That(result.ErrorMessage, Does.Contain("not found"));
        });
    }

    [Test]
    public async Task Should_set_description_status_failed_when_parse_throws()
    {
        await SeedListingAndRun();
        SetupBlobWithContent("<html>bad content</html>");
        _listingParserMock
            .Setup(p => p.ParseDescription(It.IsAny<IDocument>()))
            .Throws(new InvalidOperationException("Parse error"));

        var request = new ProcessListingRequest(1, 0, "ABC123", 1, "1/ABC123/description.html");

        var result = await CreateService().Process(request);

        Assert.That(result.Success, Is.True);

        var listing = await _dbContext.Listings.FirstAsync(l => l.ListingId == "ABC123");
        Assert.That(listing.DescriptionStatus, Is.EqualTo("failed"));
    }

    [Test]
    public async Task Should_mark_scrape_run_listing_complete()
    {
        await SeedListingAndRun();
        SetupBlobWithContent("<html></html>");
        _listingParserMock
            .Setup(p => p.ParseDescription(It.IsAny<IDocument>()))
            .Returns("desc");

        var request = new ProcessListingRequest(1, 0, "ABC123", 1, "1/ABC123/description.html");

        await CreateService().Process(request);

        var srl = await _dbContext.ScrapeRunListings.FirstAsync(s => s.ListingId == "ABC123");
        Assert.Multiple(() =>
        {
            Assert.That(srl.Status, Is.EqualTo("Complete"));
            Assert.That(srl.CompletedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Should_index_listing_when_description_complete()
    {
        await SeedListingAndRun();
        SetupBlobWithContent("<html><div>Great product</div></html>");
        _listingParserMock
            .Setup(p => p.ParseDescription(It.IsAny<IDocument>()))
            .Returns("Great product");

        var request = new ProcessListingRequest(1, 0, "ABC123", 1, "1/ABC123/description.html");

        await CreateService().Process(request);

        _indexingServiceMock.Verify(
            s => s.Index(
                It.Is<Listing>(l => l.ListingId == "ABC123" && l.DescriptionStatus == "complete"),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Should_not_index_when_description_missing()
    {
        await SeedListingAndRun();
        SetupBlobNotFound();

        var request = new ProcessListingRequest(1, 0, "ABC123", 1, "1/ABC123/description.html");

        await CreateService().Process(request);

        _indexingServiceMock.Verify(
            s => s.Index(It.IsAny<Listing>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Should_not_index_when_description_parse_fails()
    {
        await SeedListingAndRun();
        SetupBlobWithContent("<html>bad content</html>");
        _listingParserMock
            .Setup(p => p.ParseDescription(It.IsAny<IDocument>()))
            .Throws(new InvalidOperationException("Parse error"));

        var request = new ProcessListingRequest(1, 0, "ABC123", 1, "1/ABC123/description.html");

        await CreateService().Process(request);

        _indexingServiceMock.Verify(
            s => s.Index(It.IsAny<Listing>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
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

    private void SetupBlobNotFound()
    {
        var mockBlobContainerClient = new Mock<BlobContainerClient>();
        var mockBlobClient = new Mock<BlobClient>();

        mockBlobClient
            .Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Azure.Response.FromValue(false, Mock.Of<Azure.Response>()));

        mockBlobContainerClient
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Returns(mockBlobClient.Object);

        _blobServiceMock
            .Setup(s => s.GetBlobContainerClient("html"))
            .Returns(mockBlobContainerClient.Object);
    }
}
