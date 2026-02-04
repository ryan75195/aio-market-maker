using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Pinecone;
using AIOMarketMaker.Tests.Utils;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ComparablesRefreshService_UnitTests
{
    private EtlDbContext _dbContext = null!;
    private Mock<ISemanticSearchService> _searchMock = null!;
    private Mock<ILogger<ComparablesRefreshService>> _loggerMock = null!;
    private ComparablesRefreshService _service = null!;

    [SetUp]
    public void Setup()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _searchMock = new Mock<ISemanticSearchService>();
        _loggerMock = new Mock<ILogger<ComparablesRefreshService>>();
        _service = new ComparablesRefreshService(_searchMock.Object, _dbContext, _loggerMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task Should_find_and_store_comparables_for_active_listing()
    {
        var activeListing = new Listing
        {
            ListingId = "ACTIVE1", ScrapeJobId = 1, Title = "PS5 Console",
            ListingStatus = "Active", Price = 350m
        };
        var soldListing = new Listing
        {
            ListingId = "SOLD1", ScrapeJobId = 1, Title = "PS5 Console Used",
            ListingStatus = "Sold", Price = 400m
        };
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "PS5" });
        _dbContext.Listings.AddRange(activeListing, soldListing);
        await _dbContext.SaveChangesAsync();

        _searchMock
            .Setup(s => s.FindSimilar(
                "ACTIVE1", null, It.IsAny<Metadata?>(), 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResult(new List<SemanticSearchHit>
            {
                new("SOLD1", 0.92f)
            }));

        var result = await _service.Refresh(new[] { activeListing });

        Assert.Multiple(() =>
        {
            Assert.That(result.ListingsProcessed, Is.EqualTo(1));
            Assert.That(result.ComparablesFound, Is.EqualTo(1));
        });

        var comparables = await _dbContext.ListingPricingComparables.ToListAsync();
        Assert.That(comparables, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(comparables[0].ListingId, Is.EqualTo(activeListing.Id));
            Assert.That(comparables[0].ComparableListingId, Is.EqualTo(soldListing.Id));
            Assert.That(comparables[0].SimilarityScore, Is.EqualTo(0.92).Within(0.01));
        });
    }

    [Test]
    public async Task Should_replace_old_comparables_on_refresh()
    {
        var activeListing = new Listing
        {
            ListingId = "ACTIVE1", ScrapeJobId = 1, Title = "PS5",
            ListingStatus = "Active", Price = 350m
        };
        var oldSold = new Listing
        {
            ListingId = "OLD_SOLD", ScrapeJobId = 1, Title = "PS5 Old",
            ListingStatus = "Sold", Price = 380m
        };
        var newSold = new Listing
        {
            ListingId = "NEW_SOLD", ScrapeJobId = 1, Title = "PS5 New",
            ListingStatus = "Sold", Price = 420m
        };
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "PS5" });
        _dbContext.Listings.AddRange(activeListing, oldSold, newSold);
        await _dbContext.SaveChangesAsync();

        // Seed an old comparable
        _dbContext.ListingPricingComparables.Add(new ListingPricingComparable
        {
            ListingId = activeListing.Id,
            ComparableListingId = oldSold.Id,
            SimilarityScore = 0.85
        });
        await _dbContext.SaveChangesAsync();

        _searchMock
            .Setup(s => s.FindSimilar(
                "ACTIVE1", null, It.IsAny<Metadata?>(), 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResult(new List<SemanticSearchHit>
            {
                new("NEW_SOLD", 0.95f)
            }));

        await _service.Refresh(new[] { activeListing });

        var comparables = await _dbContext.ListingPricingComparables.ToListAsync();
        Assert.That(comparables, Has.Count.EqualTo(1));
        Assert.That(comparables[0].ComparableListingId, Is.EqualTo(newSold.Id));
    }

    [Test]
    public async Task Should_skip_listings_with_no_similar_results()
    {
        var activeListing = new Listing
        {
            ListingId = "LONELY1", ScrapeJobId = 1, Title = "Rare Item",
            ListingStatus = "Active", Price = 999m
        };
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "rare" });
        _dbContext.Listings.Add(activeListing);
        await _dbContext.SaveChangesAsync();

        _searchMock
            .Setup(s => s.FindSimilar(
                "LONELY1", null, It.IsAny<Metadata?>(), 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResult(new List<SemanticSearchHit>()));

        var result = await _service.Refresh(new[] { activeListing });

        Assert.Multiple(() =>
        {
            Assert.That(result.ListingsProcessed, Is.EqualTo(1));
            Assert.That(result.ComparablesFound, Is.EqualTo(0));
        });

        var comparables = await _dbContext.ListingPricingComparables.ToListAsync();
        Assert.That(comparables, Has.Count.EqualTo(0));
    }

    [Test]
    public async Task Should_skip_pinecone_hits_not_found_in_database()
    {
        var activeListing = new Listing
        {
            ListingId = "ACTIVE1", ScrapeJobId = 1, Title = "PS5",
            ListingStatus = "Active", Price = 350m
        };
        _dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "PS5" });
        _dbContext.Listings.Add(activeListing);
        await _dbContext.SaveChangesAsync();

        // Pinecone returns a listing ID that doesn't exist in our DB
        _searchMock
            .Setup(s => s.FindSimilar(
                "ACTIVE1", null, It.IsAny<Metadata?>(), 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticSearchResult(new List<SemanticSearchHit>
            {
                new("GHOST_LISTING", 0.90f)
            }));

        var result = await _service.Refresh(new[] { activeListing });

        Assert.That(result.ComparablesFound, Is.EqualTo(0));
    }
}
