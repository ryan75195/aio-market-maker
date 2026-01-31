using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Etl.Endpoints;
using AIOMarketMaker.Tests.Utils;
using System.Text.Json;
using System.Net;

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

    [Test]
    public void ProcessListingRequest_should_deserialize_from_json()
    {
        // Arrange
        var json = @"{""scrapeRunId"":123,""scrapeRunListingId"":456,""listingId"":""itm789"",""scrapeJobId"":1,""blobPath"":""123/itm789/listing.html""}";

        // Act
        var request = JsonSerializer.Deserialize<ProcessListingRequest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Assert
        Assert.That(request, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(request!.ScrapeRunId, Is.EqualTo(123));
            Assert.That(request.ScrapeRunListingId, Is.EqualTo(456));
            Assert.That(request.ListingId, Is.EqualTo("itm789"));
            Assert.That(request.ScrapeJobId, Is.EqualTo(1));
            Assert.That(request.BlobPath, Is.EqualTo("123/itm789/listing.html"));
        });
    }

    [Test]
    public void Invalid_json_should_throw_JsonException()
    {
        // Arrange
        var invalidJson = "{invalid}";

        // Act & Assert
        Assert.Throws<JsonException>(() =>
        {
            JsonSerializer.Deserialize<ProcessListingRequest>(invalidJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        });
    }

    [Test]
    public void Empty_string_should_throw_JsonException()
    {
        // Arrange
        var emptyJson = "";

        // Act & Assert
        Assert.Throws<JsonException>(() =>
        {
            JsonSerializer.Deserialize<ProcessListingRequest>(emptyJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        });
    }

    [Test]
    public async Task Run_should_return_failed_when_blob_not_found()
    {
        // Arrange
        var scrapeRun = new ScrapeRun { Id = 1, Status = "Running" };
        var scrapeJob = new ScrapeJob { Id = 1, SearchTerm = "test" };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        _dbContext.ScrapeJobs.Add(scrapeJob);

        var scrapeRunListing = new ScrapeRunListing
        {
            ScrapeRunId = 1,
            ScrapeJobId = 1,
            ListingId = "itm123",
            Status = "Pending"
        };
        _dbContext.ScrapeRunListings.Add(scrapeRunListing);
        await _dbContext.SaveChangesAsync();

        // Setup blob client to return "does not exist"
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

        var endpoint = new ProcessListingEndpoint(
            _blobServiceMock.Object,
            _dbContext,
            _listingParserMock.Object,
            _loggerMock.Object);

        var request = new ProcessListingRequest(
            ScrapeRunId: 1,
            ScrapeRunListingId: 0,
            ListingId: "itm123",
            ScrapeJobId: 1,
            BlobPath: "1/itm123/listing.html");

        var httpRequest = MockHttpRequestData.Create(request);

        // Act
        var response = await endpoint.Run(httpRequest);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var responseBody = await MockHttpRequestData.ReadResponseAsync<ProcessListingResponse>(response);
        Assert.Multiple(() =>
        {
            Assert.That(responseBody.Success, Is.False);
            Assert.That(responseBody.Status, Is.EqualTo("failed"));
            Assert.That(responseBody.ErrorMessage, Is.EqualTo("Blob not found"));
        });
    }

    [Test]
    public async Task Run_should_return_skipped_when_already_processed()
    {
        // Arrange
        var scrapeRun = new ScrapeRun { Id = 1, Status = "Running" };
        var scrapeJob = new ScrapeJob { Id = 1, SearchTerm = "test" };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        _dbContext.ScrapeJobs.Add(scrapeJob);

        var scrapeRunListing = new ScrapeRunListing
        {
            ScrapeRunId = 1,
            ScrapeJobId = 1,
            ListingId = "itm123",
            Status = "Complete"
        };
        _dbContext.ScrapeRunListings.Add(scrapeRunListing);
        await _dbContext.SaveChangesAsync();

        var endpoint = new ProcessListingEndpoint(
            _blobServiceMock.Object,
            _dbContext,
            _listingParserMock.Object,
            _loggerMock.Object);

        var request = new ProcessListingRequest(
            ScrapeRunId: 1,
            ScrapeRunListingId: 0, // Not used with composite key
            ListingId: "itm123",
            ScrapeJobId: 1,
            BlobPath: "1/itm123/listing.html");

        var httpRequest = MockHttpRequestData.Create(request);

        // Act
        var response = await endpoint.Run(httpRequest);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var responseBody = await MockHttpRequestData.ReadResponseAsync<ProcessListingResponse>(response);
        Assert.Multiple(() =>
        {
            Assert.That(responseBody.Success, Is.True);
            Assert.That(responseBody.Status, Is.EqualTo("skipped"));
        });
    }
}
