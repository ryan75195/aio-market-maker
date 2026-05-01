using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Services.Pipeline;
using AIOMarketMaker.Models.Ebay;
using AIOMarketMaker.Tests.Common;
using AngleSharp.Dom;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ScrapeJobProcessor_PostJobStageTests
{
    private EtlDbContext _dbContext = null!;
    private Mock<ILogger<ScrapeJobProcessor>> _loggerMock = null!;
    private Mock<IWebscraperClient> _webscraperClientMock = null!;
    private Mock<ISearchParser> _searchParserMock = null!;
    private Mock<IListingParser> _listingParserMock = null!;
    private Mock<IEbayUrlBuilder> _urlBuilderMock = null!;
    private Mock<IListingIndexingService> _indexingServiceMock = null!;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "Test", IsEnabled = true, CreatedUtc = DateTime.UtcNow });
        _dbContext.SaveChanges();

        _loggerMock = new Mock<ILogger<ScrapeJobProcessor>>();
        _webscraperClientMock = new Mock<IWebscraperClient>();
        _searchParserMock = new Mock<ISearchParser>();
        _listingParserMock = new Mock<IListingParser>();
        _urlBuilderMock = new Mock<IEbayUrlBuilder>();
        _indexingServiceMock = new Mock<IListingIndexingService>();

        _indexingServiceMock
            .Setup(i => i.Index(It.IsAny<Listing>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexingResult(IndexingAction.Embedded));
        _indexingServiceMock
            .Setup(i => i.IndexBatch(It.IsAny<IEnumerable<Listing>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Listing> listings, bool embed, CancellationToken _) =>
                listings.Select(_ => new IndexingResult(embed ? IndexingAction.Embedded : IndexingAction.Skipped)));

        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html></html>");

        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(Enumerable.Empty<IEbayProductSummary>());

        _listingParserMock
            .Setup(p => p.ParseDescription(It.IsAny<IDocument>()))
            .Returns("Test description");

        _urlBuilderMock
            .Setup(u => u.BuildDescriptionUrl(It.IsAny<string>()))
            .Returns<string>(id => $"https://vi.vipr.ebaydesc.com/ws/eBayISAPI.dll?item={id}");
        _urlBuilderMock
            .Setup(u => u.BuildSearchUrl(It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<int>(), It.IsAny<Condition>(), It.IsAny<BuyingFormat>()))
            .Returns("https://ebay.co.uk/sch/test");
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    private ScrapeJobProcessor CreateProcessor(params IPostJobStage[] stages) => new(
        _loggerMock.Object, _dbContext, _webscraperClientMock.Object,
        _searchParserMock.Object, _listingParserMock.Object,
        _urlBuilderMock.Object, _indexingServiceMock.Object,
        new DbWriteGate(100), stages,
        new ScrapingConfig());

    private ScrapeRun CreateAndSeedScrapeRun()
    {
        var scrapeRun = new ScrapeRun
        {
            Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
            TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        _dbContext.SaveChanges();
        return scrapeRun;
    }

    private ScrapeJobConfig CreateJobConfig() => new(1, "Test");

    // ---------------------------------------------------------------
    // 1. Post-job stages are executed after indexing, before completion
    // ---------------------------------------------------------------

    [Test]
    public async Task Should_execute_post_job_stage_after_indexing()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        var stageMock = new Mock<IPostJobStage>();
        stageMock.Setup(s => s.Name).Returns("Test Stage");

        var processor = CreateProcessor(stageMock.Object);
        await processor.Execute(run, job);

        stageMock.Verify(s => s.Execute(
            It.Is<PostJobContext>(c => c.RunId == run.Id && c.JobId == job.Id && c.SearchTerm == job.SearchTerm),
            It.IsAny<CancellationToken>()), Times.Once,
            "Post-job stage should be executed with correct context");
    }

    [Test]
    public async Task Should_execute_multiple_post_job_stages_in_order()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        var executionOrder = new List<string>();

        var stage1 = new Mock<IPostJobStage>();
        stage1.Setup(s => s.Name).Returns("Stage 1");
        stage1.Setup(s => s.Execute(It.IsAny<PostJobContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => executionOrder.Add("Stage 1"))
            .Returns(Task.CompletedTask);

        var stage2 = new Mock<IPostJobStage>();
        stage2.Setup(s => s.Name).Returns("Stage 2");
        stage2.Setup(s => s.Execute(It.IsAny<PostJobContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => executionOrder.Add("Stage 2"))
            .Returns(Task.CompletedTask);

        var processor = CreateProcessor(stage1.Object, stage2.Object);
        await processor.Execute(run, job);

        Assert.That(executionOrder, Is.EqualTo(new[] { "Stage 1", "Stage 2" }),
            "Post-job stages should execute in registration order");
    }

    [Test]
    public async Task Should_set_current_phase_to_stage_name_during_execution()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        string? phaseObserved = null;
        var stageMock = new Mock<IPostJobStage>();
        stageMock.Setup(s => s.Name).Returns("Finding Comparables");
        stageMock.Setup(s => s.Execute(It.IsAny<PostJobContext>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                // Read the phase from the DB entity — the processor sets it before calling Execute
                phaseObserved = _dbContext.ScrapeRuns.Find(run.Id)!.CurrentPhase;
            })
            .Returns(Task.CompletedTask);

        var processor = CreateProcessor(stageMock.Object);
        await processor.Execute(run, job);

        Assert.That(phaseObserved, Is.EqualTo("Finding Comparables"),
            "CurrentPhase should be set to the stage name before executing it");
    }

    [Test]
    public async Task Should_still_mark_completed_after_post_job_stages_run()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        var stageMock = new Mock<IPostJobStage>();
        stageMock.Setup(s => s.Name).Returns("Test Stage");

        var processor = CreateProcessor(stageMock.Object);
        await processor.Execute(run, job);

        var refreshedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(refreshedRun!.Status, Is.EqualTo("Completed"));
            Assert.That(refreshedRun.CurrentPhase, Is.EqualTo("Completed"));
            Assert.That(refreshedRun.CompletedUtc, Is.Not.Null);
        });
    }

    // ---------------------------------------------------------------
    // 2. Post-job stage failure is recorded but doesn't fail the run
    // ---------------------------------------------------------------

    [Test]
    public async Task Should_complete_run_when_post_job_stage_throws()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        var stageMock = new Mock<IPostJobStage>();
        stageMock.Setup(s => s.Name).Returns("Failing Stage");
        stageMock.Setup(s => s.Execute(It.IsAny<PostJobContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Stage crashed"));

        var processor = CreateProcessor(stageMock.Object);
        await processor.Execute(run, job);

        var refreshedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(refreshedRun!.Status, Is.EqualTo("Completed"),
                "Run should still complete even if a post-job stage fails");
            Assert.That(refreshedRun.CompletedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Should_record_issue_when_post_job_stage_throws()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        var stageMock = new Mock<IPostJobStage>();
        stageMock.Setup(s => s.Name).Returns("Failing Stage");
        stageMock.Setup(s => s.Execute(It.IsAny<PostJobContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Stage crashed"));

        var processor = CreateProcessor(stageMock.Object);
        await processor.Execute(run, job);

        var issues = await _dbContext.ScrapeRunIssues.ToListAsync();
        Assert.That(issues, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(issues[0].IssueType, Is.EqualTo("PostJobStageFailed"));
            Assert.That(issues[0].Phase, Is.EqualTo("Failing Stage"));
            Assert.That(issues[0].ErrorMessage, Is.EqualTo("Stage crashed"));
        });
    }

    [Test]
    public async Task Should_continue_to_next_stage_when_previous_stage_fails()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        var failingStage = new Mock<IPostJobStage>();
        failingStage.Setup(s => s.Name).Returns("Failing");
        failingStage.Setup(s => s.Execute(It.IsAny<PostJobContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));

        var passingStage = new Mock<IPostJobStage>();
        passingStage.Setup(s => s.Name).Returns("Passing");

        var processor = CreateProcessor(failingStage.Object, passingStage.Object);
        await processor.Execute(run, job);

        passingStage.Verify(s => s.Execute(
            It.Is<PostJobContext>(c => c.RunId == run.Id),
            It.IsAny<CancellationToken>()), Times.Once,
            "Second stage should still run even if first stage fails");
    }

    // ---------------------------------------------------------------
    // 3. No post-job stages — existing behavior unchanged
    // ---------------------------------------------------------------

    [Test]
    public async Task Should_complete_normally_with_no_post_job_stages()
    {
        var run = CreateAndSeedScrapeRun();
        var job = CreateJobConfig();

        var processor = CreateProcessor(); // No stages
        await processor.Execute(run, job);

        var refreshedRun = await _dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(refreshedRun!.Status, Is.EqualTo("Completed"));
            Assert.That(refreshedRun.CurrentPhase, Is.EqualTo("Completed"));
        });
    }
}
