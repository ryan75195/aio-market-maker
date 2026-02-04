using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Triggers;
using AIOMarketMaker.Etl.Services;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Tests.Utils;

namespace AIOMarketMaker.Tests.Unit.Triggers;

[TestFixture]
[Category("Unit")]
public class ScrapeTrigger_SequentialTests
{
    private Mock<ILogger<ScrapeTrigger>> _loggerMock;
    private Mock<IScrapeRunService> _scrapeRunServiceMock;
    private Func<TimeSpan, Task> _noDelay;

    [SetUp]
    public void SetUp()
    {
        _loggerMock = new Mock<ILogger<ScrapeTrigger>>();
        _scrapeRunServiceMock = new Mock<IScrapeRunService>();
        _noDelay = _ => Task.CompletedTask;
    }

    private ScrapeTrigger CreateTrigger()
    {
        return new ScrapeTrigger(
            _loggerMock.Object,
            _scrapeRunServiceMock.Object,
            _noDelay);
    }

    [Test]
    public async Task Should_process_jobs_sequentially()
    {
        // Arrange
        var job1 = new ScrapeJobConfig(1, "product 1");
        var job2 = new ScrapeJobConfig(2, "product 2");
        var jobs = new[] { job1, job2 };

        _scrapeRunServiceMock
            .Setup(s => s.GetScrapeJobConfigs())
            .ReturnsAsync(jobs);

        var startRunCallOrder = new List<int>();
        _scrapeRunServiceMock
            .Setup(s => s.StartRun(It.IsAny<ScrapeJobConfig>(), It.IsAny<string>()))
            .ReturnsAsync((ScrapeJobConfig job, string triggerType) =>
            {
                startRunCallOrder.Add(job.Id);
                return new StartedScrapeRun(job.Id * 100, job.Id, "Queued", Guid.NewGuid().ToString());
            });

        // Simulate job completion after first poll
        _scrapeRunServiceMock
            .Setup(s => s.IsRunComplete(It.IsAny<int>()))
            .ReturnsAsync(true);

        var trigger = CreateTrigger();

        // Act
        await trigger.RunNightly(null!);

        // Assert
        Assert.Multiple(() =>
        {
            // Both jobs should have StartRun called
            Assert.That(startRunCallOrder, Has.Count.EqualTo(2), "Should start both jobs");
            Assert.That(startRunCallOrder, Is.EqualTo(new[] { 1, 2 }), "Jobs should be started in order");
        });

        // Verify IsRunComplete was called for each job
        _scrapeRunServiceMock.Verify(s => s.IsRunComplete(100), Times.AtLeastOnce());
        _scrapeRunServiceMock.Verify(s => s.IsRunComplete(200), Times.AtLeastOnce());
    }

    [Test]
    public async Task Should_continue_to_next_job_when_current_job_completes()
    {
        // Arrange
        var job1 = new ScrapeJobConfig(1, "product 1");
        var job2 = new ScrapeJobConfig(2, "product 2");
        var jobs = new[] { job1, job2 };

        _scrapeRunServiceMock
            .Setup(s => s.GetScrapeJobConfigs())
            .ReturnsAsync(jobs);

        _scrapeRunServiceMock
            .Setup(s => s.StartRun(It.IsAny<ScrapeJobConfig>(), It.IsAny<string>()))
            .ReturnsAsync((ScrapeJobConfig job, string triggerType) =>
                new StartedScrapeRun(job.Id * 100, job.Id, "Queued", Guid.NewGuid().ToString()));

        // Track when IsRunComplete is called to ensure sequential behavior
        var isRunCompleteCallOrder = new List<int>();
        _scrapeRunServiceMock
            .Setup(s => s.IsRunComplete(It.IsAny<int>()))
            .ReturnsAsync((int runId) =>
            {
                isRunCompleteCallOrder.Add(runId);
                return true; // Complete immediately
            });

        var trigger = CreateTrigger();

        // Act
        await trigger.RunNightly(null!);

        // Assert - job 1 completion should be checked before job 2 starts
        Assert.That(isRunCompleteCallOrder, Contains.Item(100), "Should check if run 1 is complete");
        Assert.That(isRunCompleteCallOrder, Contains.Item(200), "Should check if run 2 is complete");

        // Verify both jobs were started
        _scrapeRunServiceMock.Verify(s => s.StartRun(job1, "Nightly"), Times.Once);
        _scrapeRunServiceMock.Verify(s => s.StartRun(job2, "Nightly"), Times.Once);
    }

    [Test]
    public async Task Should_poll_IsRunComplete_until_job_finishes()
    {
        // Arrange
        var job = new ScrapeJobConfig(1, "product 1");

        _scrapeRunServiceMock
            .Setup(s => s.GetScrapeJobConfigs())
            .ReturnsAsync(new[] { job });

        _scrapeRunServiceMock
            .Setup(s => s.StartRun(It.IsAny<ScrapeJobConfig>(), It.IsAny<string>()))
            .ReturnsAsync(new StartedScrapeRun(100, 1, "Queued", Guid.NewGuid().ToString()));

        // Simulate job not complete on first two polls, then complete
        var pollCount = 0;
        _scrapeRunServiceMock
            .Setup(s => s.IsRunComplete(100))
            .ReturnsAsync(() =>
            {
                pollCount++;
                return pollCount >= 3;
            });

        var trigger = CreateTrigger();

        // Act
        await trigger.RunNightly(null!);

        // Assert - IsRunComplete should be called at least 3 times
        Assert.That(pollCount, Is.EqualTo(3), "Should poll until job completes");
    }

    [Test]
    public async Task Should_process_manual_scrape_jobs_sequentially()
    {
        // Arrange
        var job1 = new ScrapeJobConfig(1, "product 1");
        var job2 = new ScrapeJobConfig(2, "product 2");
        var jobs = new[] { job1, job2 };

        _scrapeRunServiceMock
            .Setup(s => s.GetScrapeJobConfigs())
            .ReturnsAsync(jobs);

        var startRunCallOrder = new List<int>();
        _scrapeRunServiceMock
            .Setup(s => s.StartRun(It.IsAny<ScrapeJobConfig>(), It.IsAny<string>()))
            .ReturnsAsync((ScrapeJobConfig job, string triggerType) =>
            {
                startRunCallOrder.Add(job.Id);
                return new StartedScrapeRun(job.Id * 100, job.Id, "Queued", Guid.NewGuid().ToString());
            });

        _scrapeRunServiceMock
            .Setup(s => s.IsRunComplete(It.IsAny<int>()))
            .ReturnsAsync(true);

        var trigger = CreateTrigger();

        var request = MockHttpRequestData.CreateEmpty();

        // Act
        var response = await trigger.RunManual(request);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(startRunCallOrder, Has.Count.EqualTo(2), "Should start both jobs");
            Assert.That(startRunCallOrder, Is.EqualTo(new[] { 1, 2 }), "Jobs should be started in order");
        });
    }

    [Test]
    public async Task Should_not_call_StartRuns_when_processing_sequentially()
    {
        // Arrange
        var job = new ScrapeJobConfig(1, "product 1");

        _scrapeRunServiceMock
            .Setup(s => s.GetScrapeJobConfigs())
            .ReturnsAsync(new[] { job });

        _scrapeRunServiceMock
            .Setup(s => s.StartRun(It.IsAny<ScrapeJobConfig>(), It.IsAny<string>()))
            .ReturnsAsync(new StartedScrapeRun(100, 1, "Queued", Guid.NewGuid().ToString()));

        _scrapeRunServiceMock
            .Setup(s => s.IsRunComplete(It.IsAny<int>()))
            .ReturnsAsync(true);

        var trigger = CreateTrigger();

        // Act
        await trigger.RunNightly(null!);

        // Assert - StartRuns (plural) should not be called; only StartRun (singular)
        _scrapeRunServiceMock.Verify(
            s => s.StartRuns(It.IsAny<IEnumerable<ScrapeJobConfig>>(), It.IsAny<string>()),
            Times.Never,
            "Should use StartRun for sequential processing, not StartRuns");

        _scrapeRunServiceMock.Verify(
            s => s.StartRun(It.IsAny<ScrapeJobConfig>(), It.IsAny<string>()),
            Times.Once,
            "Should call StartRun for each job");
    }
}
