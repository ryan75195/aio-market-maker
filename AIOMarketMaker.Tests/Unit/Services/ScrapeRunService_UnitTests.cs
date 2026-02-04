using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Services;
using AIOMarketMaker.Tests.Utils;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ScrapeRunService_UnitTests
{
    private EtlDbContext _dbContext = null!;
    private Mock<QueueServiceClient> _queueServiceClientMock = null!;
    private Mock<QueueClient> _queueClientMock = null!;
    private Mock<ILogger<ScrapeRunService>> _loggerMock = null!;

    [SetUp]
    public void Setup()
    {
        _dbContext = InMemoryDbContextFactory.Create();

        _queueClientMock = new Mock<QueueClient>();
        _queueServiceClientMock = new Mock<QueueServiceClient>();
        _queueServiceClientMock
            .Setup(q => q.GetQueueClient(It.IsAny<string>()))
            .Returns(_queueClientMock.Object);

        _loggerMock = new Mock<ILogger<ScrapeRunService>>();
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    private ScrapeRunService CreateService() => new(
        _dbContext,
        _queueServiceClientMock.Object,
        _loggerMock.Object);

    [Test]
    public async Task Should_return_true_when_run_is_completed()
    {
        // Arrange
        var run = new ScrapeRun
        {
            Id = 1,
            JobId = 1,
            Status = "Completed",
            CurrentPhase = "Completed",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var service = CreateService();

        // Act
        var result = await service.IsRunComplete(1);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task Should_return_true_when_run_is_failed()
    {
        // Arrange
        var run = new ScrapeRun
        {
            Id = 1,
            JobId = 1,
            Status = "Failed",
            CurrentPhase = "Searching Active",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString(),
            ErrorMessage = "Test error"
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var service = CreateService();

        // Act
        var result = await service.IsRunComplete(1);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task Should_return_false_when_run_is_still_running()
    {
        // Arrange
        var run = new ScrapeRun
        {
            Id = 1,
            JobId = 1,
            Status = "Running",
            CurrentPhase = "Searching Active",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var service = CreateService();

        // Act
        var result = await service.IsRunComplete(1);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task Should_return_false_when_run_is_queued()
    {
        // Arrange
        var run = new ScrapeRun
        {
            Id = 1,
            JobId = 1,
            Status = "Queued",
            CurrentPhase = "Queued",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        var service = CreateService();

        // Act
        var result = await service.IsRunComplete(1);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task Should_return_true_when_run_not_found()
    {
        // Arrange - no runs in database
        var service = CreateService();

        // Act
        var result = await service.IsRunComplete(999);

        // Assert - treat missing runs as complete (nothing to wait for)
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task Should_create_scrape_run_when_starting()
    {
        // Arrange
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "Test Item" });
        await _dbContext.SaveChangesAsync();

        var job = new ScrapeJobConfig(1, "Test Item");
        var service = CreateService();

        // Act
        var result = await service.StartRun(job, "Manual");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result.JobId, Is.EqualTo(1));
            Assert.That(result.Status, Is.EqualTo("Queued"));
            Assert.That(result.InstanceId, Is.Not.Null.And.Not.Empty);
        });

        var savedRun = await _dbContext.ScrapeRuns.FindAsync(result.RunId);
        Assert.That(savedRun, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(savedRun!.JobId, Is.EqualTo(1));
            Assert.That(savedRun.Status, Is.EqualTo("Queued"));
            Assert.That(savedRun.TriggerType, Is.EqualTo("Manual"));
        });
    }

    [Test]
    public async Task Should_send_message_to_queue_when_starting()
    {
        // Arrange
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "Test Item" });
        await _dbContext.SaveChangesAsync();

        var job = new ScrapeJobConfig(1, "Test Item");
        var service = CreateService();

        // Act
        await service.StartRun(job, "Nightly");

        // Assert
        _queueClientMock.Verify(
            q => q.SendMessageAsync(It.Is<string>(m =>
                m.Contains("\"ScrapeRunId\":") &&
                m.Contains("\"JobId\":1") &&
                m.Contains("\"SearchTerm\":\"Test Item\"") &&
                m.Contains("\"TriggerType\":\"Nightly\""))),
            Times.Once);
    }
}
