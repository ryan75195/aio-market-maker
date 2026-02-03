using Moq;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using ScraperWorker.Services;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class WebscraperClient_EnqueueTests
{
    private Mock<IQueueService> _queueServiceMock = null!;
    private Mock<IJobRepository> _jobRepositoryMock = null!;
    private Mock<ILogger<WebscraperClient>> _loggerMock = null!;

    [SetUp]
    public void Setup()
    {
        _queueServiceMock = new Mock<IQueueService>();
        _jobRepositoryMock = new Mock<IJobRepository>();
        _loggerMock = new Mock<ILogger<WebscraperClient>>();
    }

    private WebscraperClient CreateClient() => new(
        new HttpClient(),
        new ScraperApiConfig("http://localhost:7126", ""),
        _jobRepositoryMock.Object,
        _loggerMock.Object,
        _queueServiceMock.Object);

    [Test]
    public async Task Should_enqueue_listing_and_description_for_each_item()
    {
        var items = new[]
        {
            new ScrapeWorkItem("ABC123", "https://ebay.co.uk/itm/ABC123", "https://vi.vipr.ebaydesc.com/item=ABC123")
        };

        await CreateClient().EnqueueScrapeWork(items, scrapeRunId: 1, scrapeJobId: 10);

        _queueServiceMock.Verify(
            q => q.EnqueueBatchAsync(
                It.Is<IEnumerable<ScrapeQueueMessage>>(msgs =>
                    msgs.Count() == 2
                    && msgs.Any(m => m.FileKey == "listing" && m.GroupId == "ABC123" && m.ScrapeRunId == 1 && m.ScrapeJobId == 10)
                    && msgs.Any(m => m.FileKey == "description" && m.GroupId == "ABC123")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Should_share_job_guid_between_listing_and_description()
    {
        var items = new[]
        {
            new ScrapeWorkItem("ABC123", "https://ebay.co.uk/itm/ABC123", "https://vi.vipr.ebaydesc.com/item=ABC123")
        };

        IEnumerable<ScrapeQueueMessage>? capturedMessages = null;
        _queueServiceMock
            .Setup(q => q.EnqueueBatchAsync(It.IsAny<IEnumerable<ScrapeQueueMessage>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ScrapeQueueMessage>, CancellationToken>((msgs, _) => capturedMessages = msgs.ToList());

        await CreateClient().EnqueueScrapeWork(items, scrapeRunId: 1, scrapeJobId: 10);

        Assert.That(capturedMessages, Is.Not.Null);
        var messageList = capturedMessages!.ToList();
        Assert.That(messageList[0].JobId, Is.EqualTo(messageList[1].JobId),
            "Listing and description for same item should share a JobId");
    }

    [Test]
    public async Task Should_enqueue_multiple_items_in_single_batch()
    {
        var items = new[]
        {
            new ScrapeWorkItem("ABC123", "https://ebay.co.uk/itm/ABC123", "https://desc/ABC123"),
            new ScrapeWorkItem("DEF456", "https://ebay.co.uk/itm/DEF456", "https://desc/DEF456")
        };

        await CreateClient().EnqueueScrapeWork(items, scrapeRunId: 1, scrapeJobId: 10);

        _queueServiceMock.Verify(
            q => q.EnqueueBatchAsync(
                It.Is<IEnumerable<ScrapeQueueMessage>>(msgs => msgs.Count() == 4),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
