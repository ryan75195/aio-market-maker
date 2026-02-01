using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Functions.Worker;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Azure;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Etl.Triggers;
using AIOMarketMaker.Tests.Utils;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text.Json;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Tests.Unit.Triggers;

/// <summary>
/// Unit tests for SimplifiedScrapeTrigger.
/// Tests the fire-and-forget queue pattern used by both RunManual and RunNightly.
/// Note: The actual scrape logic is tested in ScrapeJobQueueTrigger_UnitTests.
/// </summary>
[TestFixture]
[Category("Unit")]
public class SimplifiedScrapeTrigger_UnitTests
{
    private Mock<ILogger<SimplifiedScrapeTrigger>> _loggerMock;
    private EtlDbContext _dbContext;
    private Mock<QueueServiceClient> _queueServiceMock;
    private Mock<QueueClient> _jobQueueClientMock;

    [SetUp]
    public void SetUp()
    {
        _loggerMock = new Mock<ILogger<SimplifiedScrapeTrigger>>();
        _dbContext = InMemoryDbContextFactory.Create();
        _queueServiceMock = new Mock<QueueServiceClient>();
        _jobQueueClientMock = new Mock<QueueClient>();

        _queueServiceMock
            .Setup(q => q.GetQueueClient("scrape-jobs"))
            .Returns(_jobQueueClientMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext?.Dispose();
    }

    [Test]
    public void Should_construct_with_all_dependencies()
    {
        // Act
        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object,
            _dbContext,
            _queueServiceMock.Object);

        // Assert
        Assert.That(trigger, Is.Not.Null);
    }

    [Test]
    public async Task RunNightly_should_enqueue_job_for_each_enabled_job()
    {
        // Arrange - Create two enabled jobs and one disabled job
        var job1 = new ScrapeJob { Id = 1, SearchTerm = "product 1", IsEnabled = true };
        var job2 = new ScrapeJob { Id = 2, SearchTerm = "product 2", IsEnabled = true };
        var job3 = new ScrapeJob { Id = 3, SearchTerm = "disabled product", IsEnabled = false };
        _dbContext.ScrapeJobs.AddRange(job1, job2, job3);
        await _dbContext.SaveChangesAsync();

        var enqueuedMessages = new List<string>();
        _jobQueueClientMock
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .Callback<string>((msg) => enqueuedMessages.Add(msg))
            .ReturnsAsync(Response.FromValue(
                QueuesModelFactory.SendReceipt("messageId", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "popReceipt", DateTimeOffset.UtcNow.AddMinutes(1)),
                Mock.Of<Response>()));

        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object,
            _dbContext,
            _queueServiceMock.Object);

        // Act
        await trigger.RunNightly(null!);

        // Assert - Two job messages should be enqueued (one for each enabled job)
        Assert.That(enqueuedMessages.Count, Is.EqualTo(2), "Should enqueue one message per enabled job");

        // Verify ScrapeRuns were created with Queued status
        var scrapeRuns = await _dbContext.ScrapeRuns.ToListAsync();
        Assert.That(scrapeRuns.Count, Is.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(scrapeRuns.Select(r => r.JobId), Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(scrapeRuns.All(r => r.TriggerType == "Nightly"), Is.True);
            Assert.That(scrapeRuns.All(r => r.Status == "Queued"), Is.True);
            Assert.That(scrapeRuns.All(r => r.CurrentPhase == "Queued"), Is.True);
        });

        // Verify message contents
        var messages = enqueuedMessages.Select(m => JsonSerializer.Deserialize<ScrapeJobMessage>(m)!).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(messages.Select(m => m.JobId), Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(messages.All(m => m.TriggerType == "Nightly"), Is.True);
        });
    }

    [Test]
    public async Task RunNightly_should_not_enqueue_anything_when_no_enabled_jobs()
    {
        // Arrange - Create only disabled jobs
        var job = new ScrapeJob { Id = 1, SearchTerm = "disabled product", IsEnabled = false };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object,
            _dbContext,
            _queueServiceMock.Object);

        // Act
        await trigger.RunNightly(null!);

        // Assert
        _jobQueueClientMock.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
        var scrapeRuns = await _dbContext.ScrapeRuns.ToListAsync();
        Assert.That(scrapeRuns.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task RunManual_should_enqueue_job_messages_and_return_immediately()
    {
        // Arrange
        var job = new ScrapeJob { Id = 1, SearchTerm = "Test Product", IsEnabled = true };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var enqueuedMessages = new List<string>();
        _jobQueueClientMock
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .Callback<string>((msg) => enqueuedMessages.Add(msg))
            .ReturnsAsync(Mock.Of<Azure.Response<SendReceipt>>());

        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object,
            _dbContext,
            _queueServiceMock.Object);

        var request = MockHttpRequestData.CreateEmpty();

        // Act
        var response = await trigger.RunManual(request);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(enqueuedMessages, Has.Count.EqualTo(1), "Should enqueue one job message");

        // Verify ScrapeRun was created with Queued status
        var scrapeRun = await _dbContext.ScrapeRuns.FirstOrDefaultAsync();
        Assert.That(scrapeRun, Is.Not.Null);
        Assert.That(scrapeRun!.Status, Is.EqualTo("Queued"));
        Assert.That(scrapeRun.CurrentPhase, Is.EqualTo("Queued"));
        Assert.That(scrapeRun.TriggerType, Is.EqualTo("Manual"));

        // Verify message contains correct data
        var message = JsonSerializer.Deserialize<ScrapeJobMessage>(enqueuedMessages[0]);
        Assert.That(message!.ScrapeRunId, Is.EqualTo(scrapeRun.Id));
        Assert.That(message.JobId, Is.EqualTo(job.Id));
        Assert.That(message.SearchTerm, Is.EqualTo("Test Product"));
        Assert.That(message.TriggerType, Is.EqualTo("Manual"));
    }

    [Test]
    public async Task RunManual_should_return_OK_on_success()
    {
        // Arrange - Create an enabled job
        var job = new ScrapeJob { Id = 1, SearchTerm = "test product", IsEnabled = true };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        _jobQueueClientMock
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Response.FromValue(
                QueuesModelFactory.SendReceipt("messageId", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "popReceipt", DateTimeOffset.UtcNow.AddMinutes(1)),
                Mock.Of<Response>()));

        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object,
            _dbContext,
            _queueServiceMock.Object);

        // Create mock HTTP request with empty body (no specific jobId)
        var httpRequest = MockHttpRequestData.CreateEmpty();

        // Act
        var response = await trigger.RunManual(httpRequest);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task RunManual_should_run_specific_job_when_jobId_provided()
    {
        // Arrange - Create two enabled jobs
        var job1 = new ScrapeJob { Id = 1, SearchTerm = "product 1", IsEnabled = true };
        var job2 = new ScrapeJob { Id = 2, SearchTerm = "product 2", IsEnabled = true };
        _dbContext.ScrapeJobs.AddRange(job1, job2);
        await _dbContext.SaveChangesAsync();

        var enqueuedMessages = new List<string>();
        _jobQueueClientMock
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .Callback<string>((msg) => enqueuedMessages.Add(msg))
            .ReturnsAsync(Response.FromValue(
                QueuesModelFactory.SendReceipt("messageId", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "popReceipt", DateTimeOffset.UtcNow.AddMinutes(1)),
                Mock.Of<Response>()));

        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object,
            _dbContext,
            _queueServiceMock.Object);

        // Create mock HTTP request with specific jobId
        var httpRequest = MockHttpRequestData.Create(new { jobId = 2 });

        // Act
        var response = await trigger.RunManual(httpRequest);

        // Assert - Only one ScrapeRun should be created for job 2
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(enqueuedMessages.Count, Is.EqualTo(1), "Should only enqueue job 2");

        var scrapeRuns = await _dbContext.ScrapeRuns.ToListAsync();
        Assert.That(scrapeRuns.Count, Is.EqualTo(1));
        Assert.That(scrapeRuns[0].JobId, Is.EqualTo(2));

        var message = JsonSerializer.Deserialize<ScrapeJobMessage>(enqueuedMessages[0]);
        Assert.That(message!.JobId, Is.EqualTo(2));
        Assert.That(message.SearchTerm, Is.EqualTo("product 2"));
    }

    // Note: Test for "job not found returns 404" is skipped because HttpRequestData.CreateResponse(HttpStatusCode)
    // is an extension method that cannot be mocked with Moq. The behavior is tested via integration tests.

    [Test]
    public async Task RunManual_should_return_OK_with_empty_results_when_no_enabled_jobs()
    {
        // Arrange - Only disabled jobs
        var job = new ScrapeJob { Id = 1, SearchTerm = "disabled", IsEnabled = false };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object,
            _dbContext,
            _queueServiceMock.Object);

        var httpRequest = MockHttpRequestData.CreateEmpty();

        // Act
        var response = await trigger.RunManual(httpRequest);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _jobQueueClientMock.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task RunManual_should_enqueue_multiple_jobs_when_no_specific_job_requested()
    {
        // Arrange - Create multiple enabled jobs
        var job1 = new ScrapeJob { Id = 1, SearchTerm = "product 1", IsEnabled = true };
        var job2 = new ScrapeJob { Id = 2, SearchTerm = "product 2", IsEnabled = true };
        var job3 = new ScrapeJob { Id = 3, SearchTerm = "product 3", IsEnabled = true };
        _dbContext.ScrapeJobs.AddRange(job1, job2, job3);
        await _dbContext.SaveChangesAsync();

        var enqueuedMessages = new List<string>();
        _jobQueueClientMock
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .Callback<string>((msg) => enqueuedMessages.Add(msg))
            .ReturnsAsync(Response.FromValue(
                QueuesModelFactory.SendReceipt("messageId", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "popReceipt", DateTimeOffset.UtcNow.AddMinutes(1)),
                Mock.Of<Response>()));

        var trigger = new SimplifiedScrapeTrigger(
            _loggerMock.Object,
            _dbContext,
            _queueServiceMock.Object);

        var httpRequest = MockHttpRequestData.CreateEmpty();

        // Act
        var response = await trigger.RunManual(httpRequest);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(enqueuedMessages.Count, Is.EqualTo(3), "Should enqueue all enabled jobs");

        var messages = enqueuedMessages.Select(m => JsonSerializer.Deserialize<ScrapeJobMessage>(m)!).ToList();
        Assert.That(messages.Select(m => m.JobId), Is.EquivalentTo(new[] { 1, 2, 3 }));
    }
}
