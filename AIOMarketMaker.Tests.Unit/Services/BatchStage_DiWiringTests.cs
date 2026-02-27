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
public class BatchStage_DiWiringTests
{
    [Test]
    public void Should_resolve_comparables_batch_stage_as_batch_stage()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IComparablesEtlService>(_ => Mock.Of<IComparablesEtlService>());
        services.AddSingleton<IServiceScopeFactory>(sp => sp.GetRequiredService<IServiceScopeFactory>());
        services.AddSingleton<IBatchStage, ComparablesBatchStage>();

        using var provider = services.BuildServiceProvider();
        var stages = provider.GetServices<IBatchStage>().ToList();

        Assert.That(stages, Has.Count.EqualTo(1));
        Assert.That(stages[0], Is.TypeOf<ComparablesBatchStage>());
    }

    [Test]
    public async Task Should_complete_processor_run_with_no_post_job_stages()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // No IPostJobStage registrations — this is now the production behavior

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

        var dbContext = InMemoryDbContextFactory.Create();
        services.AddSingleton(dbContext);

        using var provider = services.BuildServiceProvider();

        // Empty post-job stages — as in production
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

        await processor.Execute(run, new ScrapeJobConfig(1, "Test Item"));

        var refreshedRun = await dbContext.ScrapeRuns.FindAsync(run.Id);
        Assert.That(refreshedRun!.Status, Is.EqualTo("Completed"),
            "Processor should complete successfully with no post-job stages");

        dbContext.Dispose();
    }
}
