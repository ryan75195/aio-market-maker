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
    private Mock<IListingComparisonService> _comparisonMock = null!;
    private Mock<ILogger<ComparablesEtlService>> _loggerMock = null!;
    private ComparablesEtlService _service = null!;

    [SetUp]
    public void Setup()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _searchMock = new Mock<ISemanticSearchService>();
        _comparisonMock = new Mock<IListingComparisonService>();
        _loggerMock = new Mock<ILogger<ComparablesEtlService>>();

        // Default: FindSimilar returns empty results unless explicitly mocked
        _searchMock.Setup(s => s.FindSimilar(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<Pinecone.Metadata?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResult(new List<SemanticSearchHit>()));

        _service = new ComparablesEtlService(
            _searchMock.Object,
            _comparisonMock.Object,
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

    private void MockPineconeResult(string queryListingId, params (string listingId, double score)[] results)
    {
        var hits = results.Select(r =>
            new SemanticSearchHit(r.listingId, (float)r.score)).ToList();

        _searchMock.Setup(s => s.FindSimilar(
                queryListingId,
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<Pinecone.Metadata?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResult(hits));
    }

    [Test]
    public async Task Should_call_llm_for_uncached_pair_and_store_verdict()
    {
        var active = SeedListing(1, "iPhone 15 Pro", "Active", 800m);
        var sold = SeedListing(2, "iPhone 15 Pro 256GB", "Sold", 850m);

        MockPineconeResult("1", ("2", 0.92));

        _comparisonMock.Setup(c => c.Compare(
                It.Is<Listing>(l => l.Id == 1),
                It.Is<Listing>(l => l.Id == 2),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComparableVerdict(true, "Same product"));

        _searchMock.Setup(s => s.Exists(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _service.Run(dryRun: false);

        var verdict = _dbContext.ListingRelationships.SingleOrDefault();
        Assert.Multiple(() =>
        {
            Assert.That(verdict, Is.Not.Null);
            Assert.That(verdict!.ListingIdA, Is.EqualTo(1));
            Assert.That(verdict.ListingIdB, Is.EqualTo(2));
            Assert.That(verdict.IsComparable, Is.True);
            Assert.That(verdict.Explanation, Is.EqualTo("Same product"));
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

        MockPineconeResult("1", ("2", 0.92));

        _searchMock.Setup(s => s.Exists(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _service.Run(dryRun: false);

        _comparisonMock.Verify(
            c => c.Compare(It.IsAny<Listing>(), It.IsAny<Listing>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.That(result.CacheHits, Is.EqualTo(1));
    }

    [Test]
    public async Task Should_use_canonical_ordering_for_verdict_storage()
    {
        // Listing 5 finds listing 3 — should store as (3, 5) not (5, 3)
        var active = SeedListing(5, "Samsung Galaxy S24", "Active", 700m);
        var sold = SeedListing(3, "Galaxy S24 Ultra", "Sold", 750m);

        MockPineconeResult("5", ("3", 0.88));

        _comparisonMock.Setup(c => c.Compare(
                It.IsAny<Listing>(),
                It.IsAny<Listing>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComparableVerdict(true, "Same phone"));

        _searchMock.Setup(s => s.Exists(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _service.Run(dryRun: false);

        var verdict = _dbContext.ListingRelationships.Single();
        Assert.Multiple(() =>
        {
            Assert.That(verdict.ListingIdA, Is.EqualTo(3));
            Assert.That(verdict.ListingIdB, Is.EqualTo(5));
        });
    }

    [Test]
    public async Task Should_compute_predictions_from_comparable_sold_listings()
    {
        var active = SeedListing(1, "iPhone 15 Pro", "Active", 800m);
        var sold1 = SeedListing(2, "iPhone 15 Pro", "Sold", 900m);
        var sold2 = SeedListing(3, "iPhone 15 Pro", "Sold", 850m);

        // Add status history with sold dates for the sold listings
        _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
        {
            ListingId = 2,
            ListingStatus = "Sold",
            SoldDateUtc = DateTime.UtcNow.AddDays(-5),
            RecordedUtc = DateTime.UtcNow,
            Price = 900m
        });
        _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
        {
            ListingId = 3,
            ListingStatus = "Sold",
            SoldDateUtc = DateTime.UtcNow.AddDays(-10),
            RecordedUtc = DateTime.UtcNow,
            Price = 850m
        });
        _dbContext.SaveChanges();

        // Pre-populate verdicts (simulating step 4 already done)
        _dbContext.ListingRelationships.Add(new ListingRelationship
        {
            ListingIdA = 1, ListingIdB = 2,
            IsComparable = true, Explanation = "Same", SimilarityScore = 0.9
        });
        _dbContext.ListingRelationships.Add(new ListingRelationship
        {
            ListingIdA = 1, ListingIdB = 3,
            IsComparable = true, Explanation = "Same", SimilarityScore = 0.88
        });
        _dbContext.SaveChanges();

        // No new Pinecone results needed — just testing aggregation
        _searchMock.Setup(s => s.Exists(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        MockPineconeResult("1");

        await _service.Run(dryRun: false);

        var prediction = _dbContext.ListingPredictions.SingleOrDefault(p => p.ListingId == 1);
        Assert.Multiple(() =>
        {
            Assert.That(prediction, Is.Not.Null);
            Assert.That(prediction!.AverageSoldPrice, Is.EqualTo(875m));
            Assert.That(prediction.SimilarSoldCount, Is.EqualTo(2));
            Assert.That(prediction.PotentialProfit, Is.EqualTo(75m));
        });
    }

    [Test]
    public async Task Should_report_counts_without_making_llm_calls_in_dry_run()
    {
        var active = SeedListing(1, "iPhone 15 Pro", "Active", 800m);
        var sold = SeedListing(2, "iPhone 15 Pro 256GB", "Sold", 850m);

        MockPineconeResult("1", ("2", 0.92));

        _searchMock.Setup(s => s.Exists(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _service.Run(dryRun: true);

        _comparisonMock.Verify(
            c => c.Compare(It.IsAny<Listing>(), It.IsAny<Listing>(), It.IsAny<CancellationToken>()),
            Times.Never);

        Assert.Multiple(() =>
        {
            Assert.That(result.LlmCallsRequired, Is.EqualTo(1));
            Assert.That(result.LlmCallsMade, Is.EqualTo(0));
            Assert.That(result.CacheHits, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Should_skip_listings_not_indexed_in_pinecone()
    {
        var active = SeedListing(1, "iPhone 15 Pro", "Active", 800m);

        _searchMock.Setup(s => s.Exists("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _service.Run(dryRun: false);

        Assert.That(result.ListingsProcessed, Is.EqualTo(0));
        _searchMock.Verify(
            s => s.FindSimilar(It.IsAny<string>(), It.IsAny<IEnumerable<string>?>(),
                It.IsAny<Pinecone.Metadata?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
