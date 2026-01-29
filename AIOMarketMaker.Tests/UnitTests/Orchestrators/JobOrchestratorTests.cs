using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Orchestrators;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AIOMarketMaker.Tests.UnitTests.Orchestrators;

[TestFixture]
[Category("Unit")]
public class JobOrchestratorTests
{
    [Test]
    public async Task Should_update_scrape_run_as_failed_when_activity_throws()
    {
        // Arrange
        var mockContext = new Mock<TaskOrchestrationContext>();
        var input = new JobOrchestratorInput(1, "scrape-run-42");

        mockContext.Setup(c => c.GetInput<JobOrchestratorInput>())
            .Returns(input);

        mockContext.Setup(c => c.CreateReplaySafeLogger<JobOrchestrator>())
            .Returns(NullLogger.Instance);

        // Make the first activity call (GetJobDetailsActivity) throw
        mockContext.Setup(c => c.CallActivityAsync<JobDetails>(
                It.IsAny<TaskName>(),
                It.IsAny<object?>(),
                It.IsAny<TaskOptions?>()))
            .ThrowsAsync(new InvalidOperationException("Database connection failed"));

        var orchestrator = new JobOrchestrator();

        // Act
        var result = await orchestrator.RunOrchestrator(mockContext.Object);

        // Assert - orchestrator should return failure
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("Database connection failed"));
        });

        // Assert - UpdateScrapeRunActivity should have been called with failure info
        mockContext.Verify(c => c.CallActivityAsync(
            It.Is<TaskName>(t => t.Name == nameof(UpdateScrapeRunActivity)),
            It.Is<UpdateScrapeRunInput>(i =>
                i.InstanceId == "scrape-run-42" &&
                !i.Success &&
                i.ErrorMessage != null &&
                i.ErrorMessage.Contains("Database connection failed")),
            It.IsAny<TaskOptions?>()),
            Times.Once,
            "JobOrchestrator catch block should call UpdateScrapeRunActivity to mark the run as Failed");
    }
}
