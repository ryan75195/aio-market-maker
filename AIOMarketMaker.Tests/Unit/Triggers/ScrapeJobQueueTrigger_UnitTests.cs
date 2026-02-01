using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Triggers;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Tests.Utils;
using Azure.Storage.Queues;

namespace AIOMarketMaker.Tests.Unit.Triggers;

[TestFixture]
[Category("Unit")]
public class ScrapeJobQueueTrigger_UnitTests
{
    private EtlDbContext _dbContext = null!;
    private Mock<ILogger<ScrapeJobQueueTrigger>> _loggerMock = null!;
    private Mock<IWebscraperClient> _webscraperClientMock = null!;
    private Mock<ISearchParser> _searchParserMock = null!;
    private Mock<QueueServiceClient> _queueServiceMock = null!;

    [SetUp]
    public void Setup()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _loggerMock = new Mock<ILogger<ScrapeJobQueueTrigger>>();
        _webscraperClientMock = new Mock<IWebscraperClient>();
        _searchParserMock = new Mock<ISearchParser>();
        _queueServiceMock = new Mock<QueueServiceClient>();

        // Setup queue client mock
        var queueClientMock = new Mock<QueueClient>();
        _queueServiceMock
            .Setup(q => q.GetQueueClient(It.IsAny<string>()))
            .Returns(queueClientMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task ProcessJob_should_update_status_to_Searching_when_started()
    {
        // Arrange
        var job = new ScrapeJob { Id = 1, SearchTerm = "Test", IsEnabled = true };
        _dbContext.ScrapeJobs.Add(job);

        var scrapeRun = new ScrapeRun
        {
            Id = 100,
            JobId = 1,
            Status = "Queued",
            CurrentPhase = "Queued",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        var message = new ScrapeJobMessage(100, 1, "Test", "Manual");
        var messageJson = JsonSerializer.Serialize(message);

        // This will fail because ScrapeJobQueueTrigger doesn't exist yet
        var trigger = new ScrapeJobQueueTrigger(
            _loggerMock.Object,
            _dbContext,
            _webscraperClientMock.Object,
            _searchParserMock.Object,
            _queueServiceMock.Object);

        // Act
        await trigger.ProcessJob(messageJson);

        // Assert
        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(100);
        Assert.That(updatedRun!.Status, Is.EqualTo("Searching").Or.EqualTo("Indexing"));
    }
}
