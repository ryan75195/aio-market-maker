using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ScraperWorker.Services;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class EnqueueScrapeRetryActivityTests
{
    private Mock<IWebscraperClient> _webScraperMock = null!;
    private Mock<IEbayUrlBuilder> _urlBuilderMock = null!;
    private EnqueueScrapeRetryActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _webScraperMock = new Mock<IWebscraperClient>();
        _urlBuilderMock = new Mock<IEbayUrlBuilder>();
        _activity = new EnqueueScrapeRetryActivity(
            _webScraperMock.Object,
            _urlBuilderMock.Object,
            NullLogger<EnqueueScrapeRetryActivity>.Instance);
    }

    [Test]
    public async Task Should_enqueue_listing_url_when_filekey_is_listing()
    {
        // Arrange
        var listingId = "123456789";
        var expectedUrl = $"https://www.ebay.co.uk/itm/{listingId}";
        _urlBuilderMock.Setup(x => x.BuildListingUrl(listingId)).Returns(expectedUrl);
        _webScraperMock
            .Setup(x => x.NewJobAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IEnumerable<object>>(),
                It.IsAny<string>(),
                listingId,
                "listing",
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartResponse("job-1"));

        var input = new EnqueueScrapeRetryInput(listingId, "listing");

        // Act
        await _activity.Run(input);

        // Assert
        _urlBuilderMock.Verify(x => x.BuildListingUrl(listingId), Times.Once);
        _webScraperMock.Verify(x => x.NewJobAsync(
            It.Is<IEnumerable<string>>(urls => urls.Single() == expectedUrl),
            It.IsAny<IEnumerable<object>>(),
            It.IsAny<string>(),
            listingId,
            "listing",
            It.IsAny<int?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Should_enqueue_description_url_when_filekey_is_description()
    {
        // Arrange
        var listingId = "987654321";
        var expectedUrl = $"https://itm.ebaydesc.com/itmdesc/{listingId}?t=0";
        _urlBuilderMock.Setup(x => x.BuildDescriptionUrl(listingId)).Returns(expectedUrl);
        _webScraperMock
            .Setup(x => x.NewJobAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IEnumerable<object>>(),
                It.IsAny<string>(),
                listingId,
                "description",
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartResponse("job-2"));

        var input = new EnqueueScrapeRetryInput(listingId, "description");

        // Act
        await _activity.Run(input);

        // Assert
        _urlBuilderMock.Verify(x => x.BuildDescriptionUrl(listingId), Times.Once);
        _webScraperMock.Verify(x => x.NewJobAsync(
            It.Is<IEnumerable<string>>(urls => urls.Single() == expectedUrl),
            It.IsAny<IEnumerable<object>>(),
            It.IsAny<string>(),
            listingId,
            "description",
            It.IsAny<int?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void Should_throw_when_listingid_is_null()
    {
        // Arrange
        var input = new EnqueueScrapeRetryInput(null!, "listing");

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(async () => await _activity.Run(input));
        Assert.That(ex!.Message, Does.Contain("ListingId and FileKey are required"));
    }

    [Test]
    public void Should_throw_when_listingid_is_empty()
    {
        // Arrange
        var input = new EnqueueScrapeRetryInput("", "listing");

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(async () => await _activity.Run(input));
        Assert.That(ex!.Message, Does.Contain("ListingId and FileKey are required"));
    }

    [Test]
    public void Should_throw_when_filekey_is_null()
    {
        // Arrange
        var input = new EnqueueScrapeRetryInput("123456", null!);

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(async () => await _activity.Run(input));
        Assert.That(ex!.Message, Does.Contain("ListingId and FileKey are required"));
    }

    [Test]
    public void Should_throw_when_filekey_is_empty()
    {
        // Arrange
        var input = new EnqueueScrapeRetryInput("123456", "");

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(async () => await _activity.Run(input));
        Assert.That(ex!.Message, Does.Contain("ListingId and FileKey are required"));
    }

    [Test]
    public void Should_rethrow_when_webscraper_fails()
    {
        // Arrange
        var listingId = "123456789";
        _urlBuilderMock.Setup(x => x.BuildListingUrl(listingId)).Returns("https://example.com");
        _webScraperMock
            .Setup(x => x.NewJobAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IEnumerable<object>>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var input = new EnqueueScrapeRetryInput(listingId, "listing");

        // Act & Assert
        var ex = Assert.ThrowsAsync<HttpRequestException>(async () => await _activity.Run(input));
        Assert.That(ex!.Message, Is.EqualTo("Connection refused"));
    }
}
