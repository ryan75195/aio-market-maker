using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Functions.Activities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ScraperWorker.Services;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class GetScrapedHtmlActivityTests
{
    private Mock<IWebscraperClient> _mockWebScraper = null!;
    private Mock<IJobRepository> _mockJobRepository = null!;
    private Mock<ILogger<GetScrapedHtmlActivity>> _mockLogger = null!;
    private GetScrapedHtmlActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _mockWebScraper = new Mock<IWebscraperClient>();
        _mockJobRepository = new Mock<IJobRepository>();
        _mockLogger = new Mock<ILogger<GetScrapedHtmlActivity>>();
        _activity = new GetScrapedHtmlActivity(
            _mockWebScraper.Object,
            _mockJobRepository.Object,
            _mockLogger.Object);
    }

    [Test]
    public async Task Should_return_html_from_blob_storage()
    {
        // Arrange
        var jobId = "job-123";
        var url = "https://ebay.com/itm/123";
        var expectedHtml = "<html><body>Test content</body></html>";

        var jobItem = new JobItemEntity(
            jobId,
            JobStatusType.Success,
            url,
            DateTimeOffset.UtcNow,
            blobUri: "https://blob.storage/job-123/html",
            error: null);

        _mockWebScraper
            .Setup(x => x.GetResultsAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobItemEntity> { jobItem });

        _mockJobRepository
            .Setup(x => x.GetFileContentsAsync(jobId, url, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedHtml);

        // Act
        var result = await _activity.Run(new GetScrapedHtmlInput(jobId), null!);

        // Assert
        Assert.That(result, Is.EqualTo(expectedHtml));
    }

    [Test]
    public async Task Should_return_null_when_no_results()
    {
        // Arrange
        _mockWebScraper
            .Setup(x => x.GetResultsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobItemEntity>());

        // Act
        var result = await _activity.Run(new GetScrapedHtmlInput("job-123"), null!);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_return_null_when_result_has_empty_url()
    {
        // Arrange
        var jobId = "job-123";
        var jobItem = new JobItemEntity(
            jobId,
            JobStatusType.Success,
            url: "",
            DateTimeOffset.UtcNow,
            blobUri: null,
            error: null);

        _mockWebScraper
            .Setup(x => x.GetResultsAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobItemEntity> { jobItem });

        // Act
        var result = await _activity.Run(new GetScrapedHtmlInput(jobId), null!);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_return_null_when_html_is_empty()
    {
        // Arrange
        var jobId = "job-123";
        var url = "https://ebay.com/itm/123";

        var jobItem = new JobItemEntity(
            jobId,
            JobStatusType.Success,
            url,
            DateTimeOffset.UtcNow,
            blobUri: "https://blob.storage/job-123/html",
            error: null);

        _mockWebScraper
            .Setup(x => x.GetResultsAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobItemEntity> { jobItem });

        _mockJobRepository
            .Setup(x => x.GetFileContentsAsync(jobId, url, It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        // Act
        var result = await _activity.Run(new GetScrapedHtmlInput(jobId), null!);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_return_null_when_html_is_null()
    {
        // Arrange
        var jobId = "job-123";
        var url = "https://ebay.com/itm/123";

        var jobItem = new JobItemEntity(
            jobId,
            JobStatusType.Success,
            url,
            DateTimeOffset.UtcNow,
            blobUri: "https://blob.storage/job-123/html",
            error: null);

        _mockWebScraper
            .Setup(x => x.GetResultsAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobItemEntity> { jobItem });

        _mockJobRepository
            .Setup(x => x.GetFileContentsAsync(jobId, url, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string)null!);

        // Act
        var result = await _activity.Run(new GetScrapedHtmlInput(jobId), null!);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_not_log_bot_detection_warning_for_description_urls()
    {
        // Arrange - description URL with small HTML (normal for descriptions)
        var jobId = "job-123";
        var descriptionUrl = "https://itm.ebaydesc.com/itmdesc/123456789?t=1234567890";
        var smallHtml = new string('x', 5000); // 5KB - normal for description pages

        var jobItem = new JobItemEntity(
            jobId,
            JobStatusType.Success,
            descriptionUrl,
            DateTimeOffset.UtcNow,
            blobUri: "https://blob.storage/job-123/html",
            error: null);

        _mockWebScraper
            .Setup(x => x.GetResultsAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobItemEntity> { jobItem });

        _mockJobRepository
            .Setup(x => x.GetFileContentsAsync(jobId, descriptionUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(smallHtml);

        // Act
        var result = await _activity.Run(new GetScrapedHtmlInput(jobId), null!);

        // Assert - should return HTML but NOT log warning for description URLs
        Assert.That(result, Is.EqualTo(smallHtml));
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("bot detection")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never,
            "Should not log bot detection warning for description URLs");
    }

    [Test]
    public async Task Should_log_bot_detection_warning_for_small_listing_page_html()
    {
        // Arrange - listing page URL with suspiciously small HTML
        var jobId = "job-123";
        var listingUrl = "https://www.ebay.com/itm/123456789";
        var smallHtml = new string('x', 50000); // 50KB - suspiciously small for listing page

        var jobItem = new JobItemEntity(
            jobId,
            JobStatusType.Success,
            listingUrl,
            DateTimeOffset.UtcNow,
            blobUri: "https://blob.storage/job-123/html",
            error: null);

        _mockWebScraper
            .Setup(x => x.GetResultsAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobItemEntity> { jobItem });

        _mockJobRepository
            .Setup(x => x.GetFileContentsAsync(jobId, listingUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(smallHtml);

        // Act
        var result = await _activity.Run(new GetScrapedHtmlInput(jobId), null!);

        // Assert - should return HTML AND log warning for listing URLs
        Assert.That(result, Is.EqualTo(smallHtml));
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("bot detection")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "Should log bot detection warning for small listing page HTML");
    }

    [Test]
    public async Task Should_not_log_bot_detection_warning_for_large_listing_page_html()
    {
        // Arrange - listing page URL with normal size HTML
        var jobId = "job-123";
        var listingUrl = "https://www.ebay.com/itm/123456789";
        var normalHtml = new string('x', 500000); // 500KB - normal for listing page

        var jobItem = new JobItemEntity(
            jobId,
            JobStatusType.Success,
            listingUrl,
            DateTimeOffset.UtcNow,
            blobUri: "https://blob.storage/job-123/html",
            error: null);

        _mockWebScraper
            .Setup(x => x.GetResultsAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobItemEntity> { jobItem });

        _mockJobRepository
            .Setup(x => x.GetFileContentsAsync(jobId, listingUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(normalHtml);

        // Act
        var result = await _activity.Run(new GetScrapedHtmlInput(jobId), null!);

        // Assert - should return HTML and NOT log warning for normal size
        Assert.That(result, Is.EqualTo(normalHtml));
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("bot detection")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never,
            "Should not log bot detection warning for normal size HTML");
    }
}
