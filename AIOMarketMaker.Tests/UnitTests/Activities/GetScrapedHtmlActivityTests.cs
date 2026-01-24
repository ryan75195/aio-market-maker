using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Functions.Activities;
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
    private GetScrapedHtmlActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _mockWebScraper = new Mock<IWebscraperClient>();
        _mockJobRepository = new Mock<IJobRepository>();
        _activity = new GetScrapedHtmlActivity(
            _mockWebScraper.Object,
            _mockJobRepository.Object,
            NullLogger<GetScrapedHtmlActivity>.Instance);
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
}
