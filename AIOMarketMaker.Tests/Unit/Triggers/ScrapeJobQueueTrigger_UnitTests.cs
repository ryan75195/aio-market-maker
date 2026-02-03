using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Services;
using AIOMarketMaker.Etl.Triggers;

namespace AIOMarketMaker.Tests.Unit.Triggers;

[TestFixture]
[Category("Unit")]
public class ScrapeJobQueueTrigger_UnitTests
{
    private Mock<ILogger<ScrapeJobQueueTrigger>> _loggerMock = null!;
    private Mock<IScrapeJobProcessor> _processorMock = null!;

    [SetUp]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<ScrapeJobQueueTrigger>>();
        _processorMock = new Mock<IScrapeJobProcessor>();
    }

    [Test]
    public async Task Should_delegate_to_processor_with_deserialized_message()
    {
        var message = new ScrapeJobMessage(100, 1, "Test", "Manual");
        var messageJson = JsonSerializer.Serialize(message);

        var trigger = new ScrapeJobQueueTrigger(_loggerMock.Object, _processorMock.Object);

        await trigger.ProcessJob(messageJson);

        _processorMock.Verify(
            p => p.Process(It.Is<ScrapeJobMessage>(m =>
                m.ScrapeRunId == 100 && m.JobId == 1
                && m.SearchTerm == "Test" && m.TriggerType == "Manual")),
            Times.Once);
    }

    [Test]
    public async Task Should_not_call_processor_when_message_is_invalid()
    {
        var trigger = new ScrapeJobQueueTrigger(_loggerMock.Object, _processorMock.Object);

        await trigger.ProcessJob("not valid json {{{");

        _processorMock.Verify(p => p.Process(It.IsAny<ScrapeJobMessage>()), Times.Never);
    }
}
