using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Endpoints;
using AIOMarketMaker.Etl.Services;
using AIOMarketMaker.Tests.Utils;
using System.Text.Json;

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

    private ProcessListingEndpoint CreateEndpoint()
    {
        var counterService = new EfCoreScrapeRunCounterService(
            _dbContext,
            new Mock<ILogger<EfCoreScrapeRunCounterService>>().Object,
            new NullComparablesRefreshService());
        var processorService = new ListingProcessorService(
            _blobServiceMock.Object,
            _dbContext,
            _listingParserMock.Object,
            counterService,
            new NullListingIndexingService(),
            new Mock<ILogger<ListingProcessorService>>().Object);
        return new ProcessListingEndpoint(processorService, _loggerMock.Object);
    }

    [Test]
    public void Should_construct_with_all_dependencies()
    {
        var endpoint = CreateEndpoint();
        Assert.That(endpoint, Is.Not.Null);
    }

    [Test]
    public void ProcessListingRequest_should_store_all_properties()
    {
        var request = new ProcessListingRequest(
            ScrapeRunId: 123,
            ScrapeRunListingId: 456,
            ListingId: "itm789",
            ScrapeJobId: 1,
            BlobPath: "123/itm789/description.html");

        Assert.Multiple(() =>
        {
            Assert.That(request.ScrapeRunId, Is.EqualTo(123));
            Assert.That(request.ScrapeRunListingId, Is.EqualTo(456));
            Assert.That(request.ListingId, Is.EqualTo("itm789"));
            Assert.That(request.ScrapeJobId, Is.EqualTo(1));
            Assert.That(request.BlobPath, Is.EqualTo("123/itm789/description.html"));
        });
    }

    [Test]
    public void ProcessListingResponse_should_store_success_with_status()
    {
        var response = new ProcessListingResponse(
            Success: true,
            Status: "complete",
            ErrorMessage: null);

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.Status, Is.EqualTo("complete"));
            Assert.That(response.ErrorMessage, Is.Null);
        });
    }

    [Test]
    public void ProcessListingResponse_should_store_failure_with_error()
    {
        var response = new ProcessListingResponse(
            Success: false,
            Status: "failed",
            ErrorMessage: "Listing not found");

        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.Status, Is.EqualTo("failed"));
            Assert.That(response.ErrorMessage, Is.EqualTo("Listing not found"));
        });
    }

    [Test]
    public void ProcessListingRequest_should_deserialize_from_json()
    {
        var json = @"{""scrapeRunId"":123,""scrapeRunListingId"":456,""listingId"":""itm789"",""scrapeJobId"":1,""blobPath"":""123/itm789/description.html""}";

        var request = JsonSerializer.Deserialize<ProcessListingRequest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.That(request, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(request!.ScrapeRunId, Is.EqualTo(123));
            Assert.That(request.ScrapeRunListingId, Is.EqualTo(456));
            Assert.That(request.ListingId, Is.EqualTo("itm789"));
            Assert.That(request.ScrapeJobId, Is.EqualTo(1));
            Assert.That(request.BlobPath, Is.EqualTo("123/itm789/description.html"));
        });
    }

    [Test]
    public void Invalid_json_should_throw_JsonException()
    {
        var invalidJson = "{invalid}";

        Assert.Throws<JsonException>(() =>
        {
            JsonSerializer.Deserialize<ProcessListingRequest>(invalidJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        });
    }

    [Test]
    public void Empty_string_should_throw_JsonException()
    {
        var emptyJson = "";

        Assert.Throws<JsonException>(() =>
        {
            JsonSerializer.Deserialize<ProcessListingRequest>(emptyJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        });
    }
}
