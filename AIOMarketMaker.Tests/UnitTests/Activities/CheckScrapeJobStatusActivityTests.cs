using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Functions.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ScraperWorker.Services;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class CheckScrapeJobStatusActivityTests
{
    private Mock<IWebscraperClient> _mockWebScraper = null!;
    private CheckScrapeJobStatusActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _mockWebScraper = new Mock<IWebscraperClient>();
        _activity = new CheckScrapeJobStatusActivity(
            _mockWebScraper.Object,
            NullLogger<CheckScrapeJobStatusActivity>.Instance);
    }

    [Test]
    public async Task Should_return_not_found_when_status_is_null()
    {
        // Arrange
        _mockWebScraper
            .Setup(x => x.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobEntity?)null);

        // Act
        var result = await _activity.Run("job-123", null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.JobId, Is.EqualTo("job-123"));
            Assert.That(result.Status, Is.EqualTo("NotFound"));
            Assert.That(result.IsComplete, Is.False);
        });
    }

    [Test]
    public async Task Should_return_complete_true_when_status_is_success()
    {
        // Arrange
        var jobEntity = new JobEntity(
            jobId: "job-123",
            startedAt: DateTime.UtcNow,
            status: JobStatusType.Success,
            totalItems: 1,
            processed: 1,
            success: 1);

        _mockWebScraper
            .Setup(x => x.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobEntity);

        // Act
        var result = await _activity.Run("job-123", null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo("Success"));
            Assert.That(result.IsComplete, Is.True);
        });
    }

    [Test]
    public async Task Should_return_complete_true_when_status_is_failure()
    {
        // Arrange
        var jobEntity = new JobEntity(
            jobId: "job-123",
            startedAt: DateTime.UtcNow,
            status: JobStatusType.Failure,
            totalItems: 1,
            processed: 1,
            failure: 1);

        _mockWebScraper
            .Setup(x => x.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobEntity);

        // Act
        var result = await _activity.Run("job-123", null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo("Failure"));
            Assert.That(result.IsComplete, Is.True);
        });
    }

    [Test]
    public async Task Should_return_complete_false_when_status_is_pending()
    {
        // Arrange
        var jobEntity = new JobEntity(
            jobId: "job-123",
            startedAt: DateTime.UtcNow,
            status: JobStatusType.Pending,
            totalItems: 1);

        _mockWebScraper
            .Setup(x => x.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobEntity);

        // Act
        var result = await _activity.Run("job-123", null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo("Pending"));
            Assert.That(result.IsComplete, Is.False);
        });
    }
}
