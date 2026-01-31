using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Etl.Endpoints;
using AIOMarketMaker.Tests.Utils;

namespace AIOMarketMaker.Tests.Unit.Endpoints;

[TestFixture]
[Category("Unit")]
public class ProcessListingEndpoint_UnitTests
{
    private Mock<BlobServiceClient> _blobServiceMock;
    private EtlDbContext _dbContext;
    private Mock<IListingParser> _listingParserMock;
    private Mock<ILogger<ProcessListingEndpoint>> _loggerMock;

    [SetUp]
    public void SetUp()
    {
        _blobServiceMock = new Mock<BlobServiceClient>();
        _dbContext = InMemoryDbContextFactory.Create();
        _listingParserMock = new Mock<IListingParser>();
        _loggerMock = new Mock<ILogger<ProcessListingEndpoint>>();
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
        var endpoint = new ProcessListingEndpoint(
            _blobServiceMock.Object,
            _dbContext,
            _listingParserMock.Object,
            _loggerMock.Object);

        // Assert
        Assert.That(endpoint, Is.Not.Null);
    }

    [Test]
    public void ProcessListingRequest_should_store_all_properties()
    {
        // Arrange & Act
        var request = new ProcessListingRequest(
            ScrapeRunId: 123,
            ScrapeRunListingId: 456,
            ListingId: "itm789",
            ScrapeJobId: 1,
            BlobPath: "123/itm789/listing.html");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(request.ScrapeRunId, Is.EqualTo(123));
            Assert.That(request.ScrapeRunListingId, Is.EqualTo(456));
            Assert.That(request.ListingId, Is.EqualTo("itm789"));
            Assert.That(request.ScrapeJobId, Is.EqualTo(1));
            Assert.That(request.BlobPath, Is.EqualTo("123/itm789/listing.html"));
        });
    }

    [Test]
    public void ProcessListingResponse_should_store_success_with_status()
    {
        // Arrange & Act
        var response = new ProcessListingResponse(
            Success: true,
            Status: "added",
            ErrorMessage: null);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.Status, Is.EqualTo("added"));
            Assert.That(response.ErrorMessage, Is.Null);
        });
    }

    [Test]
    public void ProcessListingResponse_should_store_failure_with_error()
    {
        // Arrange & Act
        var response = new ProcessListingResponse(
            Success: false,
            Status: "failed",
            ErrorMessage: "Blob not found");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.Status, Is.EqualTo("failed"));
            Assert.That(response.ErrorMessage, Is.EqualTo("Blob not found"));
        });
    }
}
