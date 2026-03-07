using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Services.Pipeline;
using AIOMarketMaker.Core.Services.Taxonomy;
using AIOMarketMaker.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class TaxonomyPostJobStage_UnitTests
{
    private EtlDbContext _db = null!;
    private Mock<ITaxonomyService> _taxonomyMock = null!;
    private Mock<ITaxonomyPersistenceService> _persistenceMock = null!;
    private TaxonomyPostJobStage _stage = null!;

    [SetUp]
    public void SetUp()
    {
        _db = InMemoryDbContextFactory.Create();
        _taxonomyMock = new Mock<ITaxonomyService>();
        _persistenceMock = new Mock<ITaxonomyPersistenceService>();

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddScoped<EtlDbContext>(_ => _db);
        serviceCollection.AddScoped<ITaxonomyService>(_ => _taxonomyMock.Object);
        serviceCollection.AddScoped<ITaxonomyPersistenceService>(_ => _persistenceMock.Object);

        var serviceProvider = serviceCollection.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        _stage = new TaxonomyPostJobStage(
            scopeFactory,
            Mock.Of<ILogger<TaxonomyPostJobStage>>());
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    [Test]
    public async Task Should_skip_when_fewer_than_10_listings()
    {
        var job = AddJob();
        AddListings(job.Id, 5);
        var context = new PostJobContext(1, job.Id, "PS5");

        await _stage.Execute(context);

        _taxonomyMock.Verify(
            x => x.Generate(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Should_call_generate_with_listing_titles()
    {
        var job = AddJob();
        AddListings(job.Id, 15);
        var context = new PostJobContext(1, job.Id, "PS5");

        _taxonomyMock
            .Setup(x => x.Generate(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyResult());

        _persistenceMock
            .Setup(x => x.Save(It.IsAny<int>(), It.IsAny<TaxonomyResult>(),
                It.IsAny<IEnumerable<int>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersistedTaxonomyRun(1, 0, 0, 0));

        await _stage.Execute(context);

        _taxonomyMock.Verify(
            x => x.Generate(
                It.Is<IEnumerable<string>>(titles => titles.Count() == 15),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Should_persist_result_with_listing_ids()
    {
        var job = AddJob();
        var listings = AddListings(job.Id, 12);
        var context = new PostJobContext(1, job.Id, "PS5");

        _taxonomyMock
            .Setup(x => x.Generate(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyResult());

        _persistenceMock
            .Setup(x => x.Save(It.IsAny<int>(), It.IsAny<TaxonomyResult>(),
                It.IsAny<IEnumerable<int>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersistedTaxonomyRun(1, 0, 0, 0));

        await _stage.Execute(context);

        _persistenceMock.Verify(
            x => x.Save(
                job.Id,
                It.IsAny<TaxonomyResult>(),
                It.Is<IEnumerable<int>>(ids => ids.Count() == 12),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Should_skip_listings_with_null_titles()
    {
        var job = AddJob();
        AddListings(job.Id, 12);
        // Add 3 listings with null titles
        for (var i = 0; i < 3; i++)
        {
            _db.Listings.Add(new Listing
            {
                ListingId = $"null-title-{i}",
                ScrapeJobId = job.Id,
                Title = null
            });
        }
        _db.SaveChanges();

        var context = new PostJobContext(1, job.Id, "PS5");

        _taxonomyMock
            .Setup(x => x.Generate(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyResult());

        _persistenceMock
            .Setup(x => x.Save(It.IsAny<int>(), It.IsAny<TaxonomyResult>(),
                It.IsAny<IEnumerable<int>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersistedTaxonomyRun(1, 0, 0, 0));

        await _stage.Execute(context);

        // Should only pass 12 titles (not the 3 with null titles)
        _taxonomyMock.Verify(
            x => x.Generate(
                It.Is<IEnumerable<string>>(titles => titles.Count() == 12),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // -- Helpers --

    private ScrapeJob AddJob(string searchTerm = "PS5")
    {
        var job = new ScrapeJob { SearchTerm = searchTerm };
        _db.ScrapeJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    private List<Listing> AddListings(int jobId, int count)
    {
        var listings = new List<Listing>();
        for (var i = 0; i < count; i++)
        {
            var listing = new Listing
            {
                ListingId = $"ebay-{jobId}-{i}",
                ScrapeJobId = jobId,
                Title = $"Test Item {i}"
            };
            _db.Listings.Add(listing);
            listings.Add(listing);
        }
        _db.SaveChanges();
        return listings;
    }

    private static TaxonomyResult EmptyResult() =>
        new(Enumerable.Empty<Axis>(), Enumerable.Empty<CellAssignment>(),
            Enumerable.Empty<CellStats>(), 0, 0);
}
