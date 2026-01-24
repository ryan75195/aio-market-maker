using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Functions.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ScraperWorker.Services;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class SubmitScrapeJobActivityTests
{
    private Mock<IWebscraperClient> _mockWebScraper = null!;
    private SubmitScrapeJobActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _mockWebScraper = new Mock<IWebscraperClient>();
        _activity = new SubmitScrapeJobActivity(
            _mockWebScraper.Object,
            NullLogger<SubmitScrapeJobActivity>.Instance);
    }

    [Test]
    public async Task Should_return_job_id_from_scraper()
    {
        // Arrange
        var expectedJobId = "job-123-456";
        _mockWebScraper
            .Setup(x => x.NewJobAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartResponse(expectedJobId));

        // Act
        var result = await _activity.Run("https://ebay.com/itm/123", null!);

        // Assert
        Assert.That(result, Is.EqualTo(expectedJobId));
    }

    [Test]
    public async Task Should_pass_url_to_scraper()
    {
        // Arrange
        var url = "https://ebay.com/itm/123456";
        _mockWebScraper
            .Setup(x => x.NewJobAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartResponse("job-id"));

        // Act
        await _activity.Run(url, null!);

        // Assert
        _mockWebScraper.Verify(x => x.NewJobAsync(
            It.Is<IEnumerable<string>>(urls => urls.Contains(url)),
            It.IsAny<IEnumerable<object>?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void Should_propagate_exception_from_scraper()
    {
        // Arrange
        _mockWebScraper
            .Setup(x => x.NewJobAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<object>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Scraper unavailable"));

        // Act & Assert
        Assert.ThrowsAsync<HttpRequestException>(async () =>
            await _activity.Run("https://ebay.com/itm/123", null!));
    }
}
