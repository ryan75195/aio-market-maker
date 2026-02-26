using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ComparablesPostJobStageTests
{
    private Mock<IComparablesEtlService> _etlServiceMock = null!;
    private Mock<ILogger<ComparablesPostJobStage>> _loggerMock = null!;
    private ComparablesPostJobStage _stage = null!;

    [SetUp]
    public void SetUp()
    {
        _etlServiceMock = new Mock<IComparablesEtlService>();
        _loggerMock = new Mock<ILogger<ComparablesPostJobStage>>();

        _etlServiceMock
            .Setup(e => e.RunForJob(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComparablesEtlResult(10, 10, 5, 2, 3, 1));

        _stage = new ComparablesPostJobStage(_etlServiceMock.Object, _loggerMock.Object);
    }

    [Test]
    public void Name_should_be_finding_comparables()
    {
        Assert.That(_stage.Name, Is.EqualTo("Finding Comparables"));
    }

    [Test]
    public async Task Should_call_run_for_job_with_job_id_from_context()
    {
        var context = new PostJobContext(RunId: 1, JobId: 42, SearchTerm: "Test");

        await _stage.Execute(context);

        _etlServiceMock.Verify(e => e.RunForJob(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Should_pass_cancellation_token_to_etl_service()
    {
        var context = new PostJobContext(RunId: 1, JobId: 42, SearchTerm: "Test");
        using var cts = new CancellationTokenSource();

        await _stage.Execute(context, cts.Token);

        _etlServiceMock.Verify(e => e.RunForJob(42, cts.Token), Times.Once);
    }

    [Test]
    public void Should_propagate_exception_from_etl_service()
    {
        var context = new PostJobContext(RunId: 1, JobId: 42, SearchTerm: "Test");

        _etlServiceMock
            .Setup(e => e.RunForJob(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ETL failed"));

        Assert.ThrowsAsync<InvalidOperationException>(() => _stage.Execute(context));
    }
}
