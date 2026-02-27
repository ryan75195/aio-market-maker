using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Models.Ebay;
using AIOMarketMaker.Tests.Common;
using AngleSharp.Dom;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class PostJobStage_DiWiringTests
{
    [Test]
    public void Should_resolve_comparables_post_job_stage_as_post_job_stage()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IComparablesEtlService>(_ => Mock.Of<IComparablesEtlService>());
        services.AddScoped<IPostJobStage, ComparablesPostJobStage>();

        using var provider = services.BuildServiceProvider();
        var stages = provider.GetServices<IPostJobStage>().ToList();

        Assert.That(stages, Has.Count.EqualTo(1));
        Assert.That(stages[0], Is.TypeOf<ComparablesPostJobStage>());
    }

    [Test]
    public async Task Should_execute_comparables_stage_through_processor_di_wiring()
    {
        var etlServiceMock = new Mock<IComparablesEtlService>();
        etlServiceMock
            .Setup(e => e.RunForJob(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComparablesEtlResult(0, 0, 0, 0, 0, 0));

        var services = new ServiceCollection();
        services.AddLogging();

        // Register the real stage with a mocked ETL service
        services.AddScoped<IComparablesEtlService>(_ => etlServiceMock.Object);
        services.AddScoped<IPostJobStage, ComparablesPostJobStage>();

        // Register the processor's other dependencies as mocks
        var webscraperMock = new Mock<IWebscraperClient>();
        webscraperMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html></html>");

        var searchParserMock = new Mock<ISearchParser>();
        searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(Enumerable.Empty<IEbayProductSummary>());

        services.AddSingleton(webscraperMock.Object);
        services.AddSingleton(Mock.Of<IListingParser>());
        services.AddSingleton(searchParserMock.Object);
        services.AddSingleton(Mock.Of<IEbayUrlBuilder>());
        services.AddSingleton(Mock.Of<IListingIndexingService>());
        services.AddSingleton(new DbWriteGate(100));

        // In-memory DB
        var dbContext = InMemoryDbContextFactory.Create();
        services.AddSingleton(dbContext);

        using var provider = services.BuildServiceProvider();

        // Create the processor through DI — verifies IEnumerable<IPostJobStage> is injected
        var stages = provider.GetServices<IPostJobStage>();
        var processor = new ScrapeJobProcessor(
            provider.GetRequiredService<ILogger<ScrapeJobProcessor>>(),
            dbContext,
            provider.GetRequiredService<IWebscraperClient>(),
            provider.GetRequiredService<ISearchParser>(),
            provider.GetRequiredService<IListingParser>(),
            provider.GetRequiredService<IEbayUrlBuilder>(),
            provider.GetRequiredService<IListingIndexingService>(),
            provider.GetRequiredService<DbWriteGate>(),
            stages,
            new ScrapingConfig());

        // Set up DB state
        dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "Test Item", IsEnabled = true, CreatedUtc = DateTime.UtcNow });
        await dbContext.SaveChangesAsync();

        var run = new ScrapeRun
        {
            JobId = 1, Status = "Queued", CurrentPhase = "Queued",
            TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        dbContext.ScrapeRuns.Add(run);
        await dbContext.SaveChangesAsync();

        // Execute the full pipeline
        await processor.Execute(run, new ScrapeJobConfig(1, "Test Item"));

        // Verify the real ComparablesPostJobStage called RunForJob with the correct job ID
        etlServiceMock.Verify(e => e.RunForJob(1, It.IsAny<CancellationToken>()), Times.Once,
            "ComparablesPostJobStage should call RunForJob via DI wiring during scrape execution");

        // Verify run still completed
        var refreshedRun = await dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.That(refreshedRun!.Status, Is.EqualTo("Completed"));

        dbContext.Dispose();
    }
}
