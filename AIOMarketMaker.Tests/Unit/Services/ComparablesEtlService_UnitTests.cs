using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ComparablesEtlService_UnitTests
{
    private EtlDbContext _dbContext = null!;
    private Mock<ISemanticSearchService> _searchMock = null!;
    private Mock<IVariantClassifierClient> _classifierMock = null!;
    private Mock<ILogger<ComparablesEtlService>> _loggerMock = null!;
    private ComparablesEtlService _service = null!;

    [SetUp]
    public void Setup()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _searchMock = new Mock<ISemanticSearchService>();
        _classifierMock = new Mock<IVariantClassifierClient>();
        _loggerMock = new Mock<ILogger<ComparablesEtlService>>();

        // Default: FindSimilar returns empty results unless explicitly mocked
        _searchMock.Setup(s => s.FindSimilar(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResult(new List<SemanticSearchHit>()));

        _service = new ComparablesEtlService(
            _searchMock.Object,
            _classifierMock.Object,
            _dbContext,
            _loggerMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    private Listing SeedListing(int id, string title, string status, decimal price = 100m)
    {
        var job = _dbContext.ScrapeJobs.FirstOrDefault() ?? _dbContext.ScrapeJobs.Add(new ScrapeJob
        {
            Id = 1,
            SearchTerm = "test"
        }).Entity;
        _dbContext.SaveChanges();

        var listing = new Listing
        {
            Id = id,
            ListingId = id.ToString(),
            Title = title,
            ListingStatus = status,
            Price = price,
            Condition = "New",
            Description = $"Description for {title}",
            ScrapeJobId = job.Id
        };
        _dbContext.Listings.Add(listing);
        _dbContext.SaveChanges();
        return listing;
    }

    private void MockVectorSearchResult(string queryListingId, params (string listingId, double score)[] results)
    {
        var hits = results.Select(r =>
            new SemanticSearchHit(r.listingId, (float)r.score)).ToList();

        _searchMock.Setup(s => s.FindSimilar(
                queryListingId,
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResult(hits));
    }

    [Test]
    public async Task Should_call_llm_for_uncached_pair_and_store_verdict()
    {
        var active = SeedListing(1, "iPhone 15 Pro", "Active", 800m);
        var sold = SeedListing(2, "iPhone 15 Pro 256GB", "Sold", 850m);

        MockVectorSearchResult("1", ("2", 0.92));

        _classifierMock.Setup(c => c.Classify(
                It.IsAny<IEnumerable<ClassifyPairRequest>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PairResult(true, 0.95f) });

        var result = await _service.Run(dryRun: false);

        var verdict = _dbContext.ListingRelationships.SingleOrDefault();
        Assert.Multiple(() =>
        {
            Assert.That(verdict, Is.Not.Null);
            Assert.That(verdict!.ListingIdA, Is.EqualTo(1));
            Assert.That(verdict.ListingIdB, Is.EqualTo(2));
            Assert.That(verdict.IsComparable, Is.True);
            Assert.That(verdict.Explanation, Does.Contain("Model: confidence="));
            Assert.That(result.LlmCallsMade, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Should_skip_llm_call_when_verdict_already_cached()
    {
        var active = SeedListing(1, "iPhone 15 Pro", "Active", 800m);
        var sold = SeedListing(2, "iPhone 15 Pro 256GB", "Sold", 850m);

        _dbContext.ListingRelationships.Add(new ListingRelationship
        {
            ListingIdA = 1,
            ListingIdB = 2,
            IsComparable = true,
            Explanation = "Already evaluated",
            SimilarityScore = 0.92
        });
        _dbContext.SaveChanges();

        MockVectorSearchResult("1", ("2", 0.92));

        var result = await _service.Run(dryRun: false);

        _classifierMock.Verify(
            c => c.Classify(It.IsAny<IEnumerable<ClassifyPairRequest>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.That(result.CacheHits, Is.EqualTo(1));
    }

    [Test]
    public async Task Should_use_canonical_ordering_for_verdict_storage()
    {
        // Listing 5 finds listing 3 — should store as (3, 5) not (5, 3)
        var active = SeedListing(5, "Samsung Galaxy S24", "Active", 700m);
        var sold = SeedListing(3, "Galaxy S24 Ultra", "Sold", 750m);

        MockVectorSearchResult("5", ("3", 0.88));

        _classifierMock.Setup(c => c.Classify(
                It.IsAny<IEnumerable<ClassifyPairRequest>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PairResult(true, 0.92f) });

        await _service.Run(dryRun: false);

        var verdict = _dbContext.ListingRelationships.Single();
        Assert.Multiple(() =>
        {
            Assert.That(verdict.ListingIdA, Is.EqualTo(3));
            Assert.That(verdict.ListingIdB, Is.EqualTo(5));
        });
    }

    // Predictions are now computed live by vw_ListingPredictions SQL view.
    // No ETL step to test — view logic is verified by integration tests against real DB.

    [Test]
    public async Task Should_report_counts_without_making_llm_calls_in_dry_run()
    {
        var active = SeedListing(1, "iPhone 15 Pro", "Active", 800m);
        var sold = SeedListing(2, "iPhone 15 Pro 256GB", "Sold", 850m);

        MockVectorSearchResult("1", ("2", 0.92));

        var result = await _service.Run(dryRun: true);

        _classifierMock.Verify(
            c => c.Classify(It.IsAny<IEnumerable<ClassifyPairRequest>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        Assert.Multiple(() =>
        {
            Assert.That(result.LlmCallsRequired, Is.EqualTo(1));
            Assert.That(result.LlmCallsMade, Is.EqualTo(0));
            Assert.That(result.CacheHits, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Should_skip_active_neighbors_and_only_classify_sold()
    {
        var active1 = SeedListing(1, "iPhone 15 Pro", "Active", 800m);
        var active2 = SeedListing(2, "iPhone 15 Pro Black", "Active", 810m);
        var sold1 = SeedListing(3, "iPhone 15 Pro 256GB", "Sold", 850m);

        // Vector search returns both active2 and sold1 as neighbors of active1
        MockVectorSearchResult("1", ("2", 0.91), ("3", 0.89));

        _classifierMock.Setup(c => c.Classify(
                It.IsAny<IEnumerable<ClassifyPairRequest>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PairResult(true, 0.95f) });

        var result = await _service.Run(dryRun: false);

        // Should only classify 1 pair (active1 <-> sold1), not active1 <-> active2
        Assert.That(result.LlmCallsMade, Is.EqualTo(1));
        var verdict = _dbContext.ListingRelationships.Single();
        Assert.Multiple(() =>
        {
            Assert.That(verdict.ListingIdA, Is.EqualTo(1));
            Assert.That(verdict.ListingIdB, Is.EqualTo(3));
        });
    }
}
