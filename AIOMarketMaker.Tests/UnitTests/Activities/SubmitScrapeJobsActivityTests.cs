using Moq;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;
using ScraperWorker.Services;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class SubmitScrapeJobsActivityTests
{
    private Mock<IQueueService> _mockQueueService = null!;
    private Mock<IEbayUrlBuilder> _mockUrlBuilder = null!;
    private Mock<ILogger<SubmitScrapeJobsActivity>> _mockLogger = null!;
    private SubmitScrapeJobsActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _mockQueueService = new Mock<IQueueService>();
        _mockUrlBuilder = new Mock<IEbayUrlBuilder>();
        _mockLogger = new Mock<ILogger<SubmitScrapeJobsActivity>>();

        _mockUrlBuilder.Setup(x => x.BuildListingUrl(It.IsAny<string>()))
            .Returns<string>(id => $"https://www.ebay.co.uk/itm/{id}");
        _mockUrlBuilder.Setup(x => x.BuildDescriptionUrl(It.IsAny<string>()))
            .Returns<string>(id => $"https://vi.vipr.ebaydesc.com/ws/eBayISAPI.dll?item={id}");

        _activity = new SubmitScrapeJobsActivity(
            _mockQueueService.Object,
            _mockUrlBuilder.Object,
            _mockLogger.Object);
    }

    [Test]
    public async Task Should_enqueue_two_messages_per_listing()
    {
        // Arrange
        var input = new SubmitScrapeJobsInput(
            ScrapeRunId: 123,
            ListingIds: new List<string> { "111", "222" });

        var enqueuedMessages = new List<ScrapeQueueMessage>();
        _mockQueueService
            .Setup(x => x.EnqueueBatchAsync(It.IsAny<IEnumerable<ScrapeQueueMessage>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ScrapeQueueMessage>, CancellationToken>((msgs, _) => enqueuedMessages.AddRange(msgs))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.SubmittedCount, Is.EqualTo(2));
            Assert.That(result.FailedCount, Is.EqualTo(0));
            Assert.That(enqueuedMessages, Has.Count.EqualTo(4)); // 2 listings × 2 URLs each
        });
    }

    [Test]
    public async Task Should_set_correct_message_properties()
    {
        // Arrange
        var input = new SubmitScrapeJobsInput(
            ScrapeRunId: 456,
            ListingIds: new List<string> { "12345" });

        var enqueuedMessages = new List<ScrapeQueueMessage>();
        _mockQueueService
            .Setup(x => x.EnqueueBatchAsync(It.IsAny<IEnumerable<ScrapeQueueMessage>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ScrapeQueueMessage>, CancellationToken>((msgs, _) => enqueuedMessages.AddRange(msgs))
            .Returns(Task.CompletedTask);

        // Act
        await _activity.Run(input, null!);

        // Assert
        var listingMsg = enqueuedMessages.First(m => m.FileKey == "listing");
        var descMsg = enqueuedMessages.First(m => m.FileKey == "description");

        Assert.Multiple(() =>
        {
            // Listing message
            Assert.That(listingMsg.Url, Does.Contain("/itm/12345"));
            Assert.That(listingMsg.GroupId, Is.EqualTo("12345"));
            Assert.That(listingMsg.FileKey, Is.EqualTo("listing"));
            Assert.That(listingMsg.ScrapeRunId, Is.EqualTo(456));
            Assert.That(listingMsg.JobId, Is.Not.Null.And.Not.Empty);

            // Description message
            Assert.That(descMsg.Url, Does.Contain("item=12345"));
            Assert.That(descMsg.GroupId, Is.EqualTo("12345"));
            Assert.That(descMsg.FileKey, Is.EqualTo("description"));
            Assert.That(descMsg.ScrapeRunId, Is.EqualTo(456));
        });
    }

    [Test]
    public async Task Should_call_enqueue_batch_once()
    {
        // Arrange
        var input = new SubmitScrapeJobsInput(
            ScrapeRunId: 789,
            ListingIds: new List<string> { "a", "b", "c" });

        // Act
        await _activity.Run(input, null!);

        // Assert - batch write should be called exactly once
        _mockQueueService.Verify(
            x => x.EnqueueBatchAsync(It.IsAny<IEnumerable<ScrapeQueueMessage>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
