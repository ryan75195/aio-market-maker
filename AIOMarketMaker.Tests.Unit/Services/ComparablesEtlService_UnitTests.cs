using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ComparablesEtlService_UnitTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<EtlDbContext> _contextOptions = null!;
    private Mock<IDbContextFactory<EtlDbContext>> _dbFactoryMock = null!;
    private Mock<ISemanticSearchService> _searchMock = null!;
    private Mock<IVariantClassifierClient> _classifierMock = null!;
    private Mock<ILogger<ComparablesEtlService>> _loggerMock = null!;
    private ComparablesEtlService _service = null!;

    [SetUp]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<EtlDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = new EtlDbContext(_contextOptions))
        {
            ctx.Database.EnsureCreated();
        }

        _dbFactoryMock = new Mock<IDbContextFactory<EtlDbContext>>();
        _dbFactoryMock.Setup(f => f.CreateDbContext())
            .Returns(() => new EtlDbContext(_contextOptions));
        _dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new EtlDbContext(_contextOptions));

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
            _dbFactoryMock.Object,
            _loggerMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _connection.Dispose();
    }

    private Listing SeedListing(int id, string title, string status, decimal price = 100m, int jobId = 1)
    {
        using var dbContext = new EtlDbContext(_contextOptions);

        if (!dbContext.ScrapeJobs.Any(j => j.Id == jobId))
        {
            dbContext.ScrapeJobs.Add(new ScrapeJob
            {
                Id = jobId,
                SearchTerm = $"test-{jobId}"
            });
            dbContext.SaveChanges();
        }

        var listing = new Listing
        {
            Id = id,
            ListingId = id.ToString(),
            Title = title,
            ListingStatus = status,
            Price = price,
            Condition = "New",
            Description = $"Description for {title}",
            ScrapeJobId = jobId
        };
        dbContext.Listings.Add(listing);
        dbContext.SaveChanges();
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

        var result = await _service.RunForJob(jobId: 1);

        using var dbContext = new EtlDbContext(_contextOptions);
        var verdict = dbContext.ListingRelationships.SingleOrDefault();
        Assert.Multiple(() =>
        {
            Assert.That(verdict, Is.Not.Null);
            Assert.That(verdict!.ListingIdA, Is.EqualTo(1));
            Assert.That(verdict.ListingIdB, Is.EqualTo(2));
            Assert.That(verdict.IsComparable, Is.True);
            Assert.That(verdict.Explanation, Does.Contain("Model: confidence="));
            Assert.That(result.PairsClassified, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Should_skip_llm_call_when_verdict_already_cached()
    {
        var active = SeedListing(1, "iPhone 15 Pro", "Active", 800m);
        var sold = SeedListing(2, "iPhone 15 Pro 256GB", "Sold", 850m);

        using (var dbContext = new EtlDbContext(_contextOptions))
        {
            dbContext.ListingRelationships.Add(new ListingRelationship
            {
                ListingIdA = 1,
                ListingIdB = 2,
                IsComparable = true,
                Explanation = "Already evaluated",
                SimilarityScore = 0.92
            });
            dbContext.SaveChanges();
        }

        MockVectorSearchResult("1", ("2", 0.92));

        var result = await _service.RunForJob(jobId: 1);

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

        await _service.RunForJob(jobId: 1);

        using var dbContext = new EtlDbContext(_contextOptions);
        var verdict = dbContext.ListingRelationships.Single();
        Assert.Multiple(() =>
        {
            Assert.That(verdict.ListingIdA, Is.EqualTo(3));
            Assert.That(verdict.ListingIdB, Is.EqualTo(5));
        });
    }

    // Predictions are computed live by ListingPredictionService CTE queries.
    // No ETL step to test — prediction logic is verified by ListingPredictionService unit tests.

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

        var result = await _service.RunForJob(jobId: 1);

        // Should only classify 1 pair (active1 <-> sold1), not active1 <-> active2
        Assert.That(result.PairsClassified, Is.EqualTo(1));
        using var dbContext = new EtlDbContext(_contextOptions);
        var verdict = dbContext.ListingRelationships.Single();
        Assert.Multiple(() =>
        {
            Assert.That(verdict.ListingIdA, Is.EqualTo(1));
            Assert.That(verdict.ListingIdB, Is.EqualTo(3));
        });
    }

    // ---- RunForJob (scoped path) tests ----

    [Test]
    public async Task RunForJob_should_only_process_active_listings_from_specified_job()
    {
        // Job 1: active listing
        SeedListing(1, "iPhone 15 Pro", "Active", 800m, jobId: 1);
        // Job 2: active listing (should NOT be processed)
        SeedListing(2, "Samsung Galaxy", "Active", 700m, jobId: 2);
        // Shared sold listing
        SeedListing(3, "iPhone 15 Pro 256GB", "Sold", 850m, jobId: 1);

        MockVectorSearchResult("1", ("3", 0.92));
        // If job 2 listing were processed, it would also search
        MockVectorSearchResult("2", ("3", 0.88));

        _classifierMock.Setup(c => c.Classify(
                It.IsAny<IEnumerable<ClassifyPairRequest>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PairResult(true, 0.95f) });

        var result = await _service.RunForJob(jobId: 1);

        // Only listing 1 (job 1) should have been vector-searched
        Assert.Multiple(() =>
        {
            Assert.That(result.VectorQueries, Is.EqualTo(1));
            Assert.That(result.PairsClassified, Is.EqualTo(1));
        });

        // Vector search should only have been called for listing "1", not "2"
        _searchMock.Verify(s => s.FindSimilar("1", It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        _searchMock.Verify(s => s.FindSimilar("2", It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task RunForJob_should_find_comparables_in_sold_listings_from_other_jobs()
    {
        // Job 1: active listing
        SeedListing(1, "iPhone 15 Pro", "Active", 800m, jobId: 1);
        // Job 2: sold listing (should be found as comparable via vector search)
        SeedListing(2, "iPhone 15 Pro 256GB", "Sold", 850m, jobId: 2);

        MockVectorSearchResult("1", ("2", 0.92));

        _classifierMock.Setup(c => c.Classify(
                It.IsAny<IEnumerable<ClassifyPairRequest>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PairResult(true, 0.95f) });

        var result = await _service.RunForJob(jobId: 1);

        // Should classify the cross-job pair
        Assert.That(result.PairsClassified, Is.EqualTo(1));

        using var dbContext = new EtlDbContext(_contextOptions);
        var verdict = dbContext.ListingRelationships.Single();
        Assert.Multiple(() =>
        {
            Assert.That(verdict.ListingIdA, Is.EqualTo(1));
            Assert.That(verdict.ListingIdB, Is.EqualTo(2));
            Assert.That(verdict.IsComparable, Is.True);
        });
    }

    [Test]
    public async Task RunForJob_should_skip_cached_verdicts()
    {
        SeedListing(1, "iPhone 15 Pro", "Active", 800m, jobId: 1);
        SeedListing(2, "iPhone 15 Pro 256GB", "Sold", 850m, jobId: 1);

        using (var dbContext = new EtlDbContext(_contextOptions))
        {
            dbContext.ListingRelationships.Add(new ListingRelationship
            {
                ListingIdA = 1,
                ListingIdB = 2,
                IsComparable = true,
                Explanation = "Already evaluated",
                SimilarityScore = 0.92
            });
            dbContext.SaveChanges();
        }

        MockVectorSearchResult("1", ("2", 0.92));

        var result = await _service.RunForJob(jobId: 1);

        _classifierMock.Verify(
            c => c.Classify(It.IsAny<IEnumerable<ClassifyPairRequest>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.That(result.CacheHits, Is.EqualTo(1));
    }

    [Test]
    public async Task RunForJob_should_return_early_when_job_has_no_active_listings()
    {
        // Only sold listings for job 1
        SeedListing(1, "iPhone 15 Pro", "Sold", 800m, jobId: 1);

        var result = await _service.RunForJob(jobId: 1);

        Assert.Multiple(() =>
        {
            Assert.That(result.ListingsProcessed, Is.EqualTo(0));
            Assert.That(result.VectorQueries, Is.EqualTo(0));
        });

        _searchMock.Verify(
            s => s.FindSimilar(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task RunForJob_should_skip_active_neighbors_and_only_classify_sold()
    {
        SeedListing(1, "iPhone 15 Pro", "Active", 800m, jobId: 1);
        SeedListing(2, "iPhone 15 Pro Black", "Active", 810m, jobId: 1);
        SeedListing(3, "iPhone 15 Pro 256GB", "Sold", 850m, jobId: 1);

        // Vector search returns both active and sold as neighbors
        MockVectorSearchResult("1", ("2", 0.91), ("3", 0.89));

        _classifierMock.Setup(c => c.Classify(
                It.IsAny<IEnumerable<ClassifyPairRequest>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PairResult(true, 0.95f) });

        var result = await _service.RunForJob(jobId: 1);

        // Should only classify sold match, not active-to-active
        Assert.That(result.PairsClassified, Is.EqualTo(1));
        using var dbContext = new EtlDbContext(_contextOptions);
        var verdict = dbContext.ListingRelationships.Single();
        Assert.Multiple(() =>
        {
            Assert.That(verdict.ListingIdA, Is.EqualTo(1));
            Assert.That(verdict.ListingIdB, Is.EqualTo(3));
        });
    }
}
