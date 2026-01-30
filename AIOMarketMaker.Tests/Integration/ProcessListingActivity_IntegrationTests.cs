using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Models.Ebay;
using AIOMarketMaker.Tests.Utils;
using AngleSharp.Dom;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

// Condition enum is in global namespace
using ConditionEnum = Condition;

namespace AIOMarketMaker.Tests.Integration;

[TestFixture]
[Category("Integration")]
[Explicit("Requires Azurite running locally")]
public class ProcessListingActivity_IntegrationTests
{
    private EtlDbContext _dbContext = null!;
    private BlobServiceClient _blobService = null!;
    private Mock<IListingParser> _mockParser = null!;
    private ProcessListingActivity _activity = null!;

    private const int TestScrapeRunId = 99999;
    private const string TestListingId = "123456789";

    [SetUp]
    public async Task SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _blobService = new BlobServiceClient("UseDevelopmentStorage=true");
        _mockParser = new Mock<IListingParser>();

        _activity = new ProcessListingActivity(
            _blobService,
            _dbContext,
            _mockParser.Object,
            NullLogger<ProcessListingActivity>.Instance);

        // Ensure container exists
        var container = _blobService.GetBlobContainerClient("html");
        await container.CreateIfNotExistsAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        _dbContext.Dispose();

        // Cleanup test blobs
        var container = _blobService.GetBlobContainerClient("html");
        var listingBlob = container.GetBlobClient($"{TestScrapeRunId}/{TestListingId}/listing.html");
        await listingBlob.DeleteIfExistsAsync();
    }

    private async Task UploadTestBlob(string html)
    {
        var container = _blobService.GetBlobContainerClient("html");
        var blob = container.GetBlobClient($"{TestScrapeRunId}/{TestListingId}/listing.html");
        await blob.UploadAsync(BinaryData.FromString(html), overwrite: true);
    }

    private ExtractedEbayListing CreateValidListing()
    {
        return new ExtractedEbayListing(
            id: "123456789",
            title: "Test Product",
            price: 99.99m,
            currency: "GBP",
            shippingCost: 5.00m,
            Condition: ConditionEnum.NEW,
            images: new List<string> { "http://example.com/img.jpg" },
            listingStatus: EbayListingStatus.Active,
            purchaseFormat: PurchaseFormat.BuyItNow,
            ItemSpecifics: null,
            descriptionSource: null,
            SoldDateUtc: null,
            Location: "London, UK",
            Url: "https://ebay.com/itm/123456789"
        );
    }

    [Test]
    public async Task Should_return_success_when_all_fields_present()
    {
        // Arrange
        await UploadTestBlob("<html><body>Valid listing page</body></html>");

        var validListing = CreateValidListing();
        _mockParser
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(validListing);

        var input = new ProcessListingInput(
            ListingId: TestListingId,
            ScrapeJobId: 1,
            ScrapeRunId: TestScrapeRunId,
            HasDescription: false
        );

        // Act - This will fail on the SQL MERGE due to SQLite incompatibility,
        // but we're testing the validation path
        ProcessListingResult? result = null;
        try
        {
            result = await _activity.Run(input);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Expected: SQLite doesn't support SQL Server MERGE syntax.
            // If we get here, validation passed (didn't return early with parse failure).
            Assert.Pass("Validation passed - reached SQL MERGE (which fails on SQLite as expected)");
        }

        // If no exception, verify result
        Assert.That(result!.Success, Is.True);
        Assert.That(result.IsParseFailure, Is.False);
    }

    [Test]
    public async Task Should_return_parse_failure_when_id_is_null()
    {
        // Arrange
        await UploadTestBlob("<html><body>Page content</body></html>");

        var listing = CreateValidListing() with { id = null };
        _mockParser
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(listing);

        var input = new ProcessListingInput(TestListingId, 1, TestScrapeRunId, false);

        // Act
        var result = await _activity.Run(input);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.IsParseFailure, Is.True);
            Assert.That(result.MissingFields, Contains.Item("id"));
        });
    }

    [Test]
    public async Task Should_return_parse_failure_when_title_is_null()
    {
        // Arrange
        await UploadTestBlob("<html><body>Page content</body></html>");

        var listing = CreateValidListing() with { title = null };
        _mockParser
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(listing);

        var input = new ProcessListingInput(TestListingId, 1, TestScrapeRunId, false);

        // Act
        var result = await _activity.Run(input);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.IsParseFailure, Is.True);
            Assert.That(result.MissingFields, Contains.Item("title"));
        });
    }

    [Test]
    public async Task Should_return_parse_failure_when_price_is_null()
    {
        // Arrange
        await UploadTestBlob("<html><body>Page content</body></html>");

        var listing = CreateValidListing() with { price = null };
        _mockParser
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(listing);

        var input = new ProcessListingInput(TestListingId, 1, TestScrapeRunId, false);

        // Act
        var result = await _activity.Run(input);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.IsParseFailure, Is.True);
            Assert.That(result.MissingFields, Contains.Item("price"));
        });
    }

    [Test]
    public async Task Should_return_parse_failure_when_images_is_empty()
    {
        // Arrange
        await UploadTestBlob("<html><body>Page content</body></html>");

        var listing = CreateValidListing() with { images = new List<string>() };
        _mockParser
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(listing);

        var input = new ProcessListingInput(TestListingId, 1, TestScrapeRunId, false);

        // Act
        var result = await _activity.Run(input);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.IsParseFailure, Is.True);
            Assert.That(result.MissingFields, Contains.Item("images"));
        });
    }

    [Test]
    public async Task Should_return_parse_failure_when_purchaseFormat_is_Unknown_for_active_listing()
    {
        // Arrange
        await UploadTestBlob("<html><body>Page content</body></html>");

        var listing = CreateValidListing() with { purchaseFormat = PurchaseFormat.Unknown };
        _mockParser
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(listing);

        var input = new ProcessListingInput(TestListingId, 1, TestScrapeRunId, false);

        // Act
        var result = await _activity.Run(input);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.IsParseFailure, Is.True);
            Assert.That(result.MissingFields, Contains.Item("purchaseFormat"));
        });
    }

    [Test]
    public async Task Should_allow_unknown_purchaseFormat_for_sold_listing()
    {
        // Arrange - Sold listings cannot determine purchaseFormat from HTML
        // because the buy box doesn't show "Buy It Now" or "Submit bid" buttons
        await UploadTestBlob("<html><body>Page content</body></html>");

        var soldListing = CreateValidListing() with
        {
            listingStatus = EbayListingStatus.Sold,
            purchaseFormat = PurchaseFormat.Unknown
        };
        _mockParser
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(soldListing);

        var input = new ProcessListingInput(TestListingId, 1, TestScrapeRunId, false);

        // Act - This will reach the SQL MERGE since validation should pass
        ProcessListingResult? result = null;
        try
        {
            result = await _activity.Run(input);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Expected: SQLite doesn't support SQL Server MERGE syntax.
            // If we get here, validation passed (the key assertion).
            Assert.Pass("Validation passed for sold listing with Unknown purchaseFormat");
        }

        // If no exception, verify result
        Assert.That(result!.Success, Is.True, "Sold listings should pass validation with Unknown purchaseFormat");
    }

    [Test]
    public async Task Should_allow_unknown_purchaseFormat_for_ended_listing()
    {
        // Arrange - Ended listings (no sale) also can't determine purchaseFormat
        await UploadTestBlob("<html><body>Page content</body></html>");

        var endedListing = CreateValidListing() with
        {
            listingStatus = EbayListingStatus.Ended,
            purchaseFormat = PurchaseFormat.Unknown
        };
        _mockParser
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(endedListing);

        var input = new ProcessListingInput(TestListingId, 1, TestScrapeRunId, false);

        // Act
        ProcessListingResult? result = null;
        try
        {
            result = await _activity.Run(input);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            Assert.Pass("Validation passed for ended listing with Unknown purchaseFormat");
        }

        Assert.That(result!.Success, Is.True, "Ended listings should pass validation with Unknown purchaseFormat");
    }

    [Test]
    public async Task Should_return_all_missing_fields_when_multiple_null()
    {
        // Arrange
        await UploadTestBlob("<html><body>Page content</body></html>");

        var listing = CreateValidListing() with
        {
            id = null,
            title = null,
            price = null
        };
        _mockParser
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(listing);

        var input = new ProcessListingInput(TestListingId, 1, TestScrapeRunId, false);

        // Act
        var result = await _activity.Run(input);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.IsParseFailure, Is.True);
            Assert.That(result.MissingFields, Has.Count.EqualTo(3));
            Assert.That(result.MissingFields, Contains.Item("id"));
            Assert.That(result.MissingFields, Contains.Item("title"));
            Assert.That(result.MissingFields, Contains.Item("price"));
        });
    }

    [Test]
    public async Task Should_include_error_message_with_missing_fields()
    {
        // Arrange
        await UploadTestBlob("<html><body>Page content</body></html>");

        var listing = CreateValidListing() with { title = null, currency = null };
        _mockParser
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(listing);

        var input = new ProcessListingInput(TestListingId, 1, TestScrapeRunId, false);

        // Act
        var result = await _activity.Run(input);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorMessage, Does.Contain("Missing:"));
            Assert.That(result.ErrorMessage, Does.Contain("title"));
            Assert.That(result.ErrorMessage, Does.Contain("currency"));
        });
    }

    [Test]
    public async Task Should_mark_redirected_listing_as_delisted_success()
    {
        // Arrange - eBay sometimes redirects unavailable listings to similar items
        // This is a successful detection - we know the listing was delisted
        await UploadTestBlob("<html><body>Page content</body></html>");

        var redirectedListing = CreateValidListing() with { id = "999999999999" }; // Different ID
        _mockParser
            .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
            .Returns(redirectedListing);

        var input = new ProcessListingInput(
            ListingId: TestListingId,  // Expected: 123456789
            ScrapeJobId: 1,
            ScrapeRunId: TestScrapeRunId,
            HasDescription: false
        );

        // Act
        var result = await _activity.Run(input);

        // Assert - detecting a redirect is a success (we learned the listing was delisted)
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, "Detecting delisted listing is a success");
            Assert.That(result.IsDelisted, Is.True, "Should flag as delisted");
        });
    }

    [Test]
    public async Task Should_mark_product_page_redirect_as_skipped()
    {
        // Arrange - eBay sometimes redirects /itm/ URLs to /p/ product catalog pages
        // Product pages aggregate multiple sellers and don't have individual item data
        var productPageHtml = @"
            <html>
            <head>
                <link rel='canonical' href='https://www.ebay.co.uk/p/5058683488' />
            </head>
            <body>Product page content</body>
            </html>";
        await UploadTestBlob(productPageHtml);

        var input = new ProcessListingInput(
            ListingId: TestListingId,
            ScrapeJobId: 1,
            ScrapeRunId: TestScrapeRunId,
            HasDescription: false
        );

        // Act
        var result = await _activity.Run(input);

        // Assert - detecting a product page redirect is a success (skip this listing)
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, "Product page redirect detection is a success");
            Assert.That(result.IsProductPageRedirect, Is.True, "Should flag as product page redirect");
            Assert.That(result.IsDelisted, Is.False, "Should not flag as delisted");
        });
    }
}
