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
}
