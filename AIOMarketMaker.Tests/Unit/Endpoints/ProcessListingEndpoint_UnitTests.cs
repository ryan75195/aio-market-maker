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
using AIOMarketMaker.Models.Ebay;
using AngleSharp.Dom;
using System.Text.Json;
using System.Net;
using Microsoft.EntityFrameworkCore;

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

    [Test]
    public async Task Run_should_return_failed_when_html_contains_error_page()
    {
        // Arrange - Error page HTML (parser won't be able to extract title)
        var errorPageHtml = "<html><body>Please verify you are human</body></html>";

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

        SetupBlobWithContent(errorPageHtml);

        // Setup parser to return null title (simulating error page that can't be parsed)
        var parsedListing = new ExtractedEbayListing(
            id: "itm123",
            title: null,  // Parser couldn't extract title from error page
            price: null,
            currency: null,
            shippingCost: null,
            Condition: null,
            images: null,
            listingStatus: null,
            purchaseFormat: null,
            ItemSpecifics: null,
            descriptionSource: null,
            SoldDateUtc: null,
            Location: null,
            Url: null
        );

        _listingParserMock
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(parsedListing);

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
            Assert.That(responseBody.ErrorMessage, Does.Contain("parse"));
        });
    }

    [Test]
    public async Task Run_should_return_added_when_new_listing_processed()
    {
        // Arrange - Valid HTML content (size doesn't matter, parser extracts title)
        var validHtml = "<html><body>Valid listing page</body></html>";

        var scrapeRun = new ScrapeRun { Id = 1, Status = "Running", ListingsProcessed = 0 };
        var scrapeJob = new ScrapeJob { Id = 1, SearchTerm = "test" };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        _dbContext.ScrapeJobs.Add(scrapeJob);

        var scrapeRunListing = new ScrapeRunListing
        {
            ScrapeRunId = 1,
            ScrapeJobId = 1,
            ListingId = "123456789",
            Status = "Pending"
        };
        _dbContext.ScrapeRunListings.Add(scrapeRunListing);
        await _dbContext.SaveChangesAsync();

        SetupBlobWithContent(validHtml);

        // Setup parser to return a valid listing
        var parsedListing = new ExtractedEbayListing(
            id: "123456789",
            title: "Test Product",
            price: 99.99m,
            currency: "GBP",
            shippingCost: 5.00m,
            Condition: Condition.NEW,
            images: new[] { "http://example.com/image1.jpg" },
            listingStatus: EbayListingStatus.Active,
            purchaseFormat: PurchaseFormat.BuyItNow,
            ItemSpecifics: "Brand: Test",
            descriptionSource: null,
            SoldDateUtc: null,
            Location: "London, UK",
            Url: "http://www.ebay.co.uk/itm/123456789"
        );

        _listingParserMock
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(parsedListing);

        var endpoint = new ProcessListingEndpoint(
            _blobServiceMock.Object,
            _dbContext,
            _listingParserMock.Object,
            _loggerMock.Object);

        var request = new ProcessListingRequest(
            ScrapeRunId: 1,
            ScrapeRunListingId: 0,
            ListingId: "123456789",
            ScrapeJobId: 1,
            BlobPath: "1/123456789/listing.html");

        var httpRequest = MockHttpRequestData.Create(request);

        // Act
        var response = await endpoint.Run(httpRequest);

        // Assert - response indicates success
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var responseBody = await MockHttpRequestData.ReadResponseAsync<ProcessListingResponse>(response);
        Assert.Multiple(() =>
        {
            Assert.That(responseBody.Success, Is.True);
            Assert.That(responseBody.Status, Is.EqualTo("added"));
        });

        // Assert - listing was created in database
        var createdListing = await _dbContext.Listings
            .FirstOrDefaultAsync(l => l.ListingId == "123456789");
        Assert.That(createdListing, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(createdListing!.Title, Is.EqualTo("Test Product"));
            Assert.That(createdListing.Price, Is.EqualTo(99.99m));
            Assert.That(createdListing.ScrapeJobId, Is.EqualTo(1));
        });

        // Assert - ScrapeRunListing status was updated
        var updatedSrl = await _dbContext.ScrapeRunListings
            .FirstOrDefaultAsync(srl => srl.ScrapeRunId == 1 && srl.ListingId == "123456789");
        Assert.That(updatedSrl!.Status, Is.EqualTo("Complete"));

        // Assert - ScrapeRun.ListingsProcessed was incremented
        // Use AsNoTracking to get fresh data from DB (raw SQL bypasses change tracker)
        var updatedRun = await _dbContext.ScrapeRuns.AsNoTracking().FirstOrDefaultAsync(sr => sr.Id == 1);
        Assert.That(updatedRun!.ListingsProcessed, Is.EqualTo(1));
    }

    [Test]
    public async Task Run_should_return_updated_when_listing_exists()
    {
        // Arrange - Valid HTML content (size doesn't matter, parser extracts title)
        var validHtml = "<html><body>Valid listing page</body></html>";

        var scrapeRun = new ScrapeRun { Id = 1, Status = "Running", ListingsProcessed = 0 };
        var scrapeJob = new ScrapeJob { Id = 1, SearchTerm = "test" };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        _dbContext.ScrapeJobs.Add(scrapeJob);

        // Create an existing listing that will be updated
        var existingListing = new Listing
        {
            ListingId = "123456789",
            ScrapeJobId = 1,
            Title = "Old Title",
            Price = 50.00m,
            Currency = "GBP",
            CreatedUtc = DateTime.UtcNow.AddDays(-1)
        };
        _dbContext.Listings.Add(existingListing);

        var scrapeRunListing = new ScrapeRunListing
        {
            ScrapeRunId = 1,
            ScrapeJobId = 1,
            ListingId = "123456789",
            Status = "Pending"
        };
        _dbContext.ScrapeRunListings.Add(scrapeRunListing);
        await _dbContext.SaveChangesAsync();

        SetupBlobWithContent(validHtml);

        // Setup parser to return an updated listing
        var parsedListing = new ExtractedEbayListing(
            id: "123456789",
            title: "New Title",  // Changed
            price: 99.99m,       // Changed
            currency: "GBP",
            shippingCost: 5.00m,
            Condition: Condition.NEW,
            images: new[] { "http://example.com/image1.jpg" },
            listingStatus: EbayListingStatus.Active,
            purchaseFormat: PurchaseFormat.BuyItNow,
            ItemSpecifics: "Brand: Test",
            descriptionSource: null,
            SoldDateUtc: null,
            Location: "London, UK",
            Url: "http://www.ebay.co.uk/itm/123456789"
        );

        _listingParserMock
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(parsedListing);

        var endpoint = new ProcessListingEndpoint(
            _blobServiceMock.Object,
            _dbContext,
            _listingParserMock.Object,
            _loggerMock.Object);

        var request = new ProcessListingRequest(
            ScrapeRunId: 1,
            ScrapeRunListingId: 0,
            ListingId: "123456789",
            ScrapeJobId: 1,
            BlobPath: "1/123456789/listing.html");

        var httpRequest = MockHttpRequestData.Create(request);

        // Act
        var response = await endpoint.Run(httpRequest);

        // Assert - response indicates updated
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var responseBody = await MockHttpRequestData.ReadResponseAsync<ProcessListingResponse>(response);
        Assert.Multiple(() =>
        {
            Assert.That(responseBody.Success, Is.True);
            Assert.That(responseBody.Status, Is.EqualTo("updated"));
        });

        // Assert - listing was updated in database
        var updatedListing = await _dbContext.Listings
            .FirstOrDefaultAsync(l => l.ListingId == "123456789");
        Assert.That(updatedListing, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(updatedListing!.Title, Is.EqualTo("New Title"));
            Assert.That(updatedListing.Price, Is.EqualTo(99.99m));
        });
    }

    [Test]
    public async Task Run_should_return_failed_and_update_status_when_parser_returns_null_title()
    {
        // Arrange - Valid HTML but parser can't extract title (error page content)
        var htmlContent = "<html><body>Please verify you are human</body></html>";

        var scrapeRun = new ScrapeRun { Id = 1, Status = "Running", ListingsProcessed = 0 };
        var scrapeJob = new ScrapeJob { Id = 1, SearchTerm = "test" };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        _dbContext.ScrapeJobs.Add(scrapeJob);

        var scrapeRunListing = new ScrapeRunListing
        {
            ScrapeRunId = 1,
            ScrapeJobId = 1,
            ListingId = "123456789",
            Status = "Pending"
        };
        _dbContext.ScrapeRunListings.Add(scrapeRunListing);
        await _dbContext.SaveChangesAsync();

        SetupBlobWithContent(htmlContent);

        // Setup parser to return listing with null title (couldn't parse)
        var parsedListing = new ExtractedEbayListing(
            id: "123456789",
            title: null,  // Parser couldn't extract title
            price: null,
            currency: null,
            shippingCost: null,
            Condition: null,
            images: null,
            listingStatus: null,
            purchaseFormat: null,
            ItemSpecifics: null,
            descriptionSource: null,
            SoldDateUtc: null,
            Location: null,
            Url: null
        );

        _listingParserMock
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(parsedListing);

        var endpoint = new ProcessListingEndpoint(
            _blobServiceMock.Object,
            _dbContext,
            _listingParserMock.Object,
            _loggerMock.Object);

        var request = new ProcessListingRequest(
            ScrapeRunId: 1,
            ScrapeRunListingId: 0,
            ListingId: "123456789",
            ScrapeJobId: 1,
            BlobPath: "1/123456789/listing.html");

        var httpRequest = MockHttpRequestData.Create(request);

        // Act
        var response = await endpoint.Run(httpRequest);

        // Assert - response indicates failure
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var responseBody = await MockHttpRequestData.ReadResponseAsync<ProcessListingResponse>(response);
        Assert.Multiple(() =>
        {
            Assert.That(responseBody.Success, Is.False);
            Assert.That(responseBody.Status, Is.EqualTo("failed"));
            Assert.That(responseBody.ErrorMessage, Does.Contain("parse"));
        });

        // Assert - ScrapeRunListing status was updated to Failed (not left as Pending!)
        var updatedSrl = await _dbContext.ScrapeRunListings
            .FirstOrDefaultAsync(srl => srl.ScrapeRunId == 1 && srl.ListingId == "123456789");
        Assert.That(updatedSrl!.Status, Is.EqualTo("Failed"));

        // Assert - no listing was created
        var listing = await _dbContext.Listings
            .FirstOrDefaultAsync(l => l.ListingId == "123456789");
        Assert.That(listing, Is.Null);
    }

    [Test]
    public async Task Run_should_succeed_with_small_html_when_parser_extracts_valid_title()
    {
        // Arrange - Small but valid HTML (e.g., 14KB) - should NOT be rejected by size alone
        var smallValidHtml = "<html><body>Valid eBay listing page</body></html>"; // ~50 bytes

        var scrapeRun = new ScrapeRun { Id = 1, Status = "Running", ListingsProcessed = 0 };
        var scrapeJob = new ScrapeJob { Id = 1, SearchTerm = "test" };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        _dbContext.ScrapeJobs.Add(scrapeJob);

        var scrapeRunListing = new ScrapeRunListing
        {
            ScrapeRunId = 1,
            ScrapeJobId = 1,
            ListingId = "123456789",
            Status = "Pending"
        };
        _dbContext.ScrapeRunListings.Add(scrapeRunListing);
        await _dbContext.SaveChangesAsync();

        SetupBlobWithContent(smallValidHtml);

        // Setup parser to return a valid listing WITH a title
        var parsedListing = new ExtractedEbayListing(
            id: "123456789",
            title: "PlayStation 5 Console",  // Valid title extracted!
            price: 399.99m,
            currency: "GBP",
            shippingCost: 0m,
            Condition: Condition.NEW,
            images: new[] { "http://example.com/image1.jpg" },
            listingStatus: EbayListingStatus.Active,
            purchaseFormat: PurchaseFormat.BuyItNow,
            ItemSpecifics: null,
            descriptionSource: null,
            SoldDateUtc: null,
            Location: "London, UK",
            Url: "http://www.ebay.co.uk/itm/123456789"
        );

        _listingParserMock
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(parsedListing);

        var endpoint = new ProcessListingEndpoint(
            _blobServiceMock.Object,
            _dbContext,
            _listingParserMock.Object,
            _loggerMock.Object);

        var request = new ProcessListingRequest(
            ScrapeRunId: 1,
            ScrapeRunListingId: 0,
            ListingId: "123456789",
            ScrapeJobId: 1,
            BlobPath: "1/123456789/listing.html");

        var httpRequest = MockHttpRequestData.Create(request);

        // Act
        var response = await endpoint.Run(httpRequest);

        // Assert - should succeed despite small HTML size
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var responseBody = await MockHttpRequestData.ReadResponseAsync<ProcessListingResponse>(response);
        Assert.Multiple(() =>
        {
            Assert.That(responseBody.Success, Is.True);
            Assert.That(responseBody.Status, Is.EqualTo("added"));
        });

        // Assert - listing was created
        var createdListing = await _dbContext.Listings
            .FirstOrDefaultAsync(l => l.ListingId == "123456789");
        Assert.That(createdListing, Is.Not.Null);
        Assert.That(createdListing!.Title, Is.EqualTo("PlayStation 5 Console"));
    }

    [Test]
    public async Task Run_should_skip_update_when_status_transition_is_invalid()
    {
        // Arrange - Existing Sold listing
        var scrapeRun = new ScrapeRun { Id = 1, Status = "Running", ListingsProcessed = 0 };
        var scrapeJob = new ScrapeJob { Id = 1, SearchTerm = "test" };
        var existingListing = new Listing
        {
            ListingId = "123",
            ScrapeJobId = 1,
            Title = "Original Title",
            Price = 100m,
            ListingStatus = "Sold"  // Terminal status!
        };

        _dbContext.ScrapeRuns.Add(scrapeRun);
        _dbContext.ScrapeJobs.Add(scrapeJob);
        _dbContext.Listings.Add(existingListing);

        var scrapeRunListing = new ScrapeRunListing { ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "123", Status = "Pending" };
        _dbContext.ScrapeRunListings.Add(scrapeRunListing);
        await _dbContext.SaveChangesAsync();

        SetupBlobWithContent("<html></html>");

        // Parser returns Active status (invalid: Sold->Active)
        var parsedListing = new ExtractedEbayListing(
            id: "123",
            title: "New Title",
            price: 90m,
            currency: "GBP",
            shippingCost: null,
            Condition: Condition.USED,
            images: null,
            listingStatus: EbayListingStatus.Active,  // Trying to go Sold->Active!
            purchaseFormat: null,
            ItemSpecifics: null,
            descriptionSource: null,
            SoldDateUtc: null,
            Location: null,
            Url: null
        );

        _listingParserMock
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(parsedListing);

        var endpoint = new ProcessListingEndpoint(
            _blobServiceMock.Object,
            _dbContext,
            _listingParserMock.Object,
            _loggerMock.Object);

        var request = new ProcessListingRequest(1, 0, "123", 1, "path");
        var httpRequest = MockHttpRequestData.Create(request);

        // Act
        var response = await endpoint.Run(httpRequest);
        var responseBody = await MockHttpRequestData.ReadResponseAsync<ProcessListingResponse>(response);

        // Assert - Should skip the update
        Assert.Multiple(() =>
        {
            Assert.That(responseBody.Status, Is.EqualTo("skipped"));
            Assert.That(responseBody.ErrorMessage, Does.Contain("invalid").IgnoreCase.Or.Contain("transition").IgnoreCase);
        });

        // Verify listing was NOT updated
        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.ListingId == "123");
        Assert.Multiple(() =>
        {
            Assert.That(listing!.Title, Is.EqualTo("Original Title"), "Title should not change");
            Assert.That(listing.ListingStatus, Is.EqualTo("Sold"), "Status should remain Sold");
        });
    }

    [Test]
    public async Task Run_should_create_status_history_when_status_changes()
    {
        // Arrange - Existing Active listing
        var scrapeRun = new ScrapeRun { Id = 1, Status = "Running", ListingsProcessed = 0 };
        var scrapeJob = new ScrapeJob { Id = 1, SearchTerm = "test" };
        var existingListing = new Listing
        {
            Id = 1,
            ListingId = "123",
            ScrapeJobId = 1,
            Title = "Product",
            Price = 100m,
            ListingStatus = "Active"
        };

        _dbContext.ScrapeRuns.Add(scrapeRun);
        _dbContext.ScrapeJobs.Add(scrapeJob);
        _dbContext.Listings.Add(existingListing);

        var scrapeRunListing = new ScrapeRunListing { ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "123", Status = "Pending" };
        _dbContext.ScrapeRunListings.Add(scrapeRunListing);
        await _dbContext.SaveChangesAsync();

        SetupBlobWithContent("<html></html>");

        // Parser returns Sold status (valid transition: Active→Sold)
        var parsedListing = new ExtractedEbayListing(
            id: "123",
            title: "Product",
            price: 95m,  // Sold price
            currency: "GBP",
            shippingCost: null,
            Condition: Condition.USED,
            images: null,
            listingStatus: EbayListingStatus.Sold,
            purchaseFormat: null,
            ItemSpecifics: null,
            descriptionSource: null,
            SoldDateUtc: DateTime.UtcNow,
            Location: null,
            Url: null
        );

        _listingParserMock
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(parsedListing);

        var endpoint = new ProcessListingEndpoint(
            _blobServiceMock.Object,
            _dbContext,
            _listingParserMock.Object,
            _loggerMock.Object);

        var request = new ProcessListingRequest(1, 0, "123", 1, "path");
        var httpRequest = MockHttpRequestData.Create(request);

        // Act
        await endpoint.Run(httpRequest);

        // Assert - ListingStatusHistory should be created
        var history = await _dbContext.ListingStatusHistory
            .Where(h => h.ListingId == 1)
            .ToListAsync();

        Assert.That(history.Count, Is.EqualTo(1), "Should create one history record");
        Assert.Multiple(() =>
        {
            Assert.That(history[0].ListingStatus, Is.EqualTo("Sold"));
            Assert.That(history[0].Price, Is.EqualTo(95m));
            Assert.That(history[0].Source, Is.EqualTo("StatusUpdate"));
        });
    }

    [Test]
    public async Task Run_should_create_status_history_on_initial_scrape()
    {
        // Arrange - No existing listing
        var scrapeRun = new ScrapeRun { Id = 1, Status = "Running", ListingsProcessed = 0 };
        var scrapeJob = new ScrapeJob { Id = 1, SearchTerm = "test" };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        _dbContext.ScrapeJobs.Add(scrapeJob);

        var scrapeRunListing = new ScrapeRunListing { ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "456", Status = "Pending" };
        _dbContext.ScrapeRunListings.Add(scrapeRunListing);
        await _dbContext.SaveChangesAsync();

        SetupBlobWithContent("<html></html>");

        // Parser returns a new listing
        var parsedListing = new ExtractedEbayListing(
            id: "456",
            title: "New Product",
            price: 150m,
            currency: "GBP",
            shippingCost: null,
            Condition: Condition.NEW,
            images: null,
            listingStatus: EbayListingStatus.Active,
            purchaseFormat: null,
            ItemSpecifics: null,
            descriptionSource: null,
            SoldDateUtc: null,
            Location: null,
            Url: null
        );

        _listingParserMock
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(parsedListing);

        var endpoint = new ProcessListingEndpoint(
            _blobServiceMock.Object,
            _dbContext,
            _listingParserMock.Object,
            _loggerMock.Object);

        var request = new ProcessListingRequest(1, 0, "456", 1, "path");
        var httpRequest = MockHttpRequestData.Create(request);

        // Act
        await endpoint.Run(httpRequest);

        // Assert - ListingStatusHistory should be created with InitialScrape source
        var history = await _dbContext.ListingStatusHistory.ToListAsync();

        Assert.That(history.Count, Is.EqualTo(1), "Should create one history record");
        Assert.Multiple(() =>
        {
            Assert.That(history[0].ListingStatus, Is.EqualTo("Active"));
            Assert.That(history[0].Price, Is.EqualTo(150m));
            Assert.That(history[0].Source, Is.EqualTo("InitialScrape"));
        });
    }

    [Test]
    public async Task Run_should_create_status_history_when_price_changes()
    {
        // Arrange - Existing Active listing with different price
        var scrapeRun = new ScrapeRun { Id = 1, Status = "Running", ListingsProcessed = 0 };
        var scrapeJob = new ScrapeJob { Id = 1, SearchTerm = "test" };
        var existingListing = new Listing
        {
            Id = 1,
            ListingId = "789",
            ScrapeJobId = 1,
            Title = "Product",
            Price = 200m,  // Original price
            ListingStatus = "Active"
        };

        _dbContext.ScrapeRuns.Add(scrapeRun);
        _dbContext.ScrapeJobs.Add(scrapeJob);
        _dbContext.Listings.Add(existingListing);

        var scrapeRunListing = new ScrapeRunListing { ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "789", Status = "Pending" };
        _dbContext.ScrapeRunListings.Add(scrapeRunListing);
        await _dbContext.SaveChangesAsync();

        SetupBlobWithContent("<html></html>");

        // Parser returns same status but different price
        var parsedListing = new ExtractedEbayListing(
            id: "789",
            title: "Product",
            price: 180m,  // Price dropped!
            currency: "GBP",
            shippingCost: null,
            Condition: Condition.USED,
            images: null,
            listingStatus: EbayListingStatus.Active,  // Same status
            purchaseFormat: null,
            ItemSpecifics: null,
            descriptionSource: null,
            SoldDateUtc: null,
            Location: null,
            Url: null
        );

        _listingParserMock
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(parsedListing);

        var endpoint = new ProcessListingEndpoint(
            _blobServiceMock.Object,
            _dbContext,
            _listingParserMock.Object,
            _loggerMock.Object);

        var request = new ProcessListingRequest(1, 0, "789", 1, "path");
        var httpRequest = MockHttpRequestData.Create(request);

        // Act
        await endpoint.Run(httpRequest);

        // Assert - ListingStatusHistory should be created with PriceUpdate source
        var history = await _dbContext.ListingStatusHistory
            .Where(h => h.ListingId == 1)
            .ToListAsync();

        Assert.That(history.Count, Is.EqualTo(1), "Should create one history record");
        Assert.Multiple(() =>
        {
            Assert.That(history[0].ListingStatus, Is.EqualTo("Active"));
            Assert.That(history[0].Price, Is.EqualTo(180m));
            Assert.That(history[0].Source, Is.EqualTo("PriceUpdate"));
        });
    }

[Test]
    public async Task Run_should_prioritize_status_update_when_both_status_and_price_change()
    {
        // Arrange - Existing Active listing @ $100
        var scrapeRun = new ScrapeRun { Id = 1, Status = "Running", ListingsProcessed = 0 };
        var scrapeJob = new ScrapeJob { Id = 1, SearchTerm = "test" };
        var existingListing = new Listing
        {
            Id = 1,
            ListingId = "999",
            ScrapeJobId = 1,
            Title = "Product",
            Price = 100m,  // Original price
            ListingStatus = "Active"
        };

        _dbContext.ScrapeRuns.Add(scrapeRun);
        _dbContext.ScrapeJobs.Add(scrapeJob);
        _dbContext.Listings.Add(existingListing);

        var scrapeRunListing = new ScrapeRunListing { ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "999", Status = "Pending" };
        _dbContext.ScrapeRunListings.Add(scrapeRunListing);
        await _dbContext.SaveChangesAsync();

        SetupBlobWithContent("<html></html>");

        // Parser returns Sold status AND different price (both changed)
        var parsedListing = new ExtractedEbayListing(
            id: "999",
            title: "Product",
            price: 95m,  // Price also changed
            currency: "GBP",
            shippingCost: null,
            Condition: Condition.USED,
            images: null,
            listingStatus: EbayListingStatus.Sold,  // Status changed: Active -> Sold
            purchaseFormat: null,
            ItemSpecifics: null,
            descriptionSource: null,
            SoldDateUtc: DateTime.UtcNow,
            Location: null,
            Url: null
        );

        _listingParserMock
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(parsedListing);

        var endpoint = new ProcessListingEndpoint(
            _blobServiceMock.Object,
            _dbContext,
            _listingParserMock.Object,
            _loggerMock.Object);

        var request = new ProcessListingRequest(1, 0, "999", 1, "path");
        var httpRequest = MockHttpRequestData.Create(request);

        // Act
        await endpoint.Run(httpRequest);

        // Assert - ListingStatusHistory should use "StatusUpdate" (status takes precedence over price)
        var history = await _dbContext.ListingStatusHistory
            .Where(h => h.ListingId == 1)
            .ToListAsync();

        Assert.That(history.Count, Is.EqualTo(1), "Should create one history record");
        Assert.Multiple(() =>
        {
            Assert.That(history[0].ListingStatus, Is.EqualTo("Sold"));
            Assert.That(history[0].Price, Is.EqualTo(95m));
            Assert.That(history[0].Source, Is.EqualTo("StatusUpdate"), "Status change should take precedence over price change");
        });
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

        // Setup all overloads of DownloadContentAsync
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
}
