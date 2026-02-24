using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AIOMarketMaker.Tests.Integration.Services;

[TestFixture]
[Category("Integration")]
[Explicit("Requires LocalDB with AIOMarketMaker database and migrations applied")]
public class ListingPredictionService_PricingIntegrationTests
{
    private const string ConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=AIOMarketMaker;Trusted_Connection=True;TrustServerCertificate=True;";

    private EtlDbContext _db = null!;
    private ListingPredictionService _service = null!;
    private PricingOptions _pricingOptions = null!;
    private int _testJobId;
    private readonly List<int> _createdListingIds = new();
    private readonly List<int> _createdRelationshipIds = new();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "INSERT INTO ScrapeJobs (SearchTerm, IsEnabled, CreatedUtc) OUTPUT INSERTED.Id VALUES ('__PricingTest__', 0, GETUTCDATE())",
            conn);
        _testJobId = (int)(await cmd.ExecuteScalarAsync())!;
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "DELETE FROM ScrapeJobs WHERE Id = @Id", conn);
        cmd.Parameters.AddWithValue("@Id", _testJobId);
        await cmd.ExecuteNonQueryAsync();
    }

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<EtlDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        _db = new EtlDbContext(options);
        _pricingOptions = new PricingOptions();
        _service = new ListingPredictionService(_db, Options.Create(_pricingOptions));
        _createdListingIds.Clear();
        _createdRelationshipIds.Clear();
    }

    [TearDown]
    public async Task TearDown()
    {
        // Delete in FK order: relationships first, then listings
        if (_createdRelationshipIds.Count > 0)
        {
            var relIds = string.Join(",", _createdRelationshipIds);
            await _db.Database.ExecuteSqlRawAsync($"DELETE FROM ListingRelationships WHERE Id IN ({relIds})");
        }
        if (_createdListingIds.Count > 0)
        {
            var listingIds = string.Join(",", _createdListingIds);
            await _db.Database.ExecuteSqlRawAsync($"DELETE FROM ListingRelationships WHERE ListingIdA IN ({listingIds}) OR ListingIdB IN ({listingIds})");
            await _db.Database.ExecuteSqlRawAsync($"DELETE FROM Listings WHERE Id IN ({listingIds})");
        }
        _db.Dispose();
    }

    // -- IQR Outlier Removal --

    [Test]
    public async Task Should_remove_price_outliers_via_iqr()
    {
        // Arrange: active listing at £30 (cheap enough for positive profit),
        // 5 sold comps: 50, 55, 60, 65, 200 (200 is outlier)
        var active = await CreateListing("Active", 30m);
        var sold1 = await CreateListing("Sold", 50m, soldDate: DateTime.UtcNow.AddDays(-5));
        var sold2 = await CreateListing("Sold", 55m, soldDate: DateTime.UtcNow.AddDays(-4));
        var sold3 = await CreateListing("Sold", 60m, soldDate: DateTime.UtcNow.AddDays(-3));
        var sold4 = await CreateListing("Sold", 65m, soldDate: DateTime.UtcNow.AddDays(-2));
        var sold5 = await CreateListing("Sold", 200m, soldDate: DateTime.UtcNow.AddDays(-1));

        await CreateRelationship(active, sold1, 0.90);
        await CreateRelationship(active, sold2, 0.90);
        await CreateRelationship(active, sold3, 0.90);
        await CreateRelationship(active, sold4, 0.90);
        await CreateRelationship(active, sold5, 0.90);

        // Act
        var filters = new PredictionFilters();
        var result = await _service.GetPrediction(active, filters);

        // Assert: outlier (200) removed, leaving 4 comps
        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.SimilarSoldCount, Is.EqualTo(4), "Should exclude the £200 outlier");
            Assert.That(result.OutliersRemoved, Is.EqualTo(1), "One outlier should be removed");
            // Weighted avg of 50,55,60,65 with equal confidence should be ~57.5
            Assert.That(result.AverageSoldPrice, Is.InRange(50m, 70m), "Price should exclude the £200 outlier");
        });
    }

    [Test]
    public async Task Should_keep_all_items_when_fewer_than_four_comps()
    {
        // Arrange: only 3 comps — IQR shouldn't apply
        var active = await CreateListing("Active", 60m);
        var sold1 = await CreateListing("Sold", 50m, soldDate: DateTime.UtcNow.AddDays(-3));
        var sold2 = await CreateListing("Sold", 55m, soldDate: DateTime.UtcNow.AddDays(-2));
        var sold3 = await CreateListing("Sold", 200m, soldDate: DateTime.UtcNow.AddDays(-1));

        await CreateRelationship(active, sold1, 0.90);
        await CreateRelationship(active, sold2, 0.90);
        await CreateRelationship(active, sold3, 0.90);

        var result = await _service.GetPrediction(active, new PredictionFilters());

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.SimilarSoldCount, Is.EqualTo(3), "All 3 should be kept (< 4 for IQR)");
            Assert.That(result.OutliersRemoved, Is.EqualTo(0));
        });
    }

    // -- Confidence Weighting --

    [Test]
    public async Task Should_weight_price_toward_high_confidence_comps()
    {
        // Arrange: two comps — one high confidence at £100, one low at £200
        var active = await CreateListing("Active", 80m);
        var soldHigh = await CreateListing("Sold", 100m, soldDate: DateTime.UtcNow.AddDays(-1));
        var soldLow = await CreateListing("Sold", 200m, soldDate: DateTime.UtcNow.AddDays(-1));

        await CreateRelationship(active, soldHigh, 0.99);
        await CreateRelationship(active, soldLow, 0.50);

        var result = await _service.GetPrediction(active, new PredictionFilters());

        Assert.That(result, Is.Not.Null);
        // With power=2: weight(0.99)=0.98, weight(0.50)=0.25
        // Weighted avg = (100*0.98 + 200*0.25) / (0.98+0.25) = 148/1.23 ≈ 120.3
        Assert.That(result!.AverageSoldPrice, Is.LessThan(150m),
            "Should weight toward £100 (high confidence), not simple avg of £150");
    }

    // -- Recency Weighting --

    [Test]
    public async Task Should_return_recency_weighted_price_favouring_recent_sales()
    {
        // Arrange: two comps with equal confidence — one recent at £100, one old at £200
        var active = await CreateListing("Active", 80m);
        var recent = await CreateListing("Sold", 100m, soldDate: DateTime.UtcNow.AddDays(-1));
        var old = await CreateListing("Sold", 200m, soldDate: DateTime.UtcNow.AddDays(-120));

        await CreateRelationship(active, recent, 0.90);
        await CreateRelationship(active, old, 0.90);

        // Act: use short half-life to exaggerate recency effect
        _pricingOptions.RecencyHalfLifeDays = 15.0;
        _service = new ListingPredictionService(_db, Options.Create(_pricingOptions));
        var result = await _service.GetPrediction(active, new PredictionFilters());

        // The recency-weighted price should favor £100 (recent)
        // but GetPrediction returns AverageSoldPrice which is confidence-weighted (not recency)
        // The CTE should use combined confidence + recency weighting
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.AverageSoldPrice, Is.LessThan(150m),
            "Should weight toward £100 (recent sale)");
    }

    // -- Confidence Score --

    [Test]
    public async Task Should_return_higher_confidence_with_more_consistent_prices()
    {
        // Arrange: set A — consistent prices (all £100)
        var activeA = await CreateListing("Active", 80m);
        for (var i = 0; i < 10; i++)
        {
            var sold = await CreateListing("Sold", 100m, soldDate: DateTime.UtcNow.AddDays(-i));
            await CreateRelationship(activeA, sold, 0.90);
        }

        // Set B — scattered prices (50 to 500)
        var activeB = await CreateListing("Active", 80m);
        var scatteredPrices = new[] { 50m, 100m, 150m, 200m, 250m, 300m, 350m, 400m, 450m, 500m };
        for (var i = 0; i < 10; i++)
        {
            var sold = await CreateListing("Sold", scatteredPrices[i], soldDate: DateTime.UtcNow.AddDays(-i));
            await CreateRelationship(activeB, sold, 0.90);
        }

        var resultA = await _service.GetPrediction(activeA, new PredictionFilters());
        var resultB = await _service.GetPrediction(activeB, new PredictionFilters());

        Assert.Multiple(() =>
        {
            Assert.That(resultA, Is.Not.Null);
            Assert.That(resultB, Is.Not.Null);
            Assert.That(resultA!.Confidence, Is.GreaterThan(resultB!.Confidence),
                "Consistent prices should yield higher confidence than scattered");
        });
    }

    // -- Median --

    [Test]
    public async Task Should_return_correct_median_price()
    {
        var active = await CreateListing("Active", 20m);
        var prices = new[] { 40m, 50m, 60m, 70m, 80m };
        foreach (var price in prices)
        {
            var sold = await CreateListing("Sold", price, soldDate: DateTime.UtcNow.AddDays(-1));
            await CreateRelationship(active, sold, 0.90);
        }

        var result = await _service.GetPrediction(active, new PredictionFilters());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.MedianSoldPrice, Is.EqualTo(60m).Within(1m),
            "Median of 40,50,60,70,80 should be 60");
    }

    // -- List view consistency --

    [Test]
    public async Task Should_return_same_price_in_list_and_detail_view()
    {
        var active = await CreateListing("Active", 30m);
        var prices = new[] { 50m, 55m, 60m, 65m, 200m }; // 200 is outlier
        foreach (var price in prices)
        {
            var sold = await CreateListing("Sold", price, soldDate: DateTime.UtcNow.AddDays(-1));
            await CreateRelationship(active, sold, 0.90);
        }

        var filters = new PredictionFilters(FeePercent: 13.25m);

        // Detail view
        var detail = await _service.GetPrediction(active, filters);

        // List view (single item)
        var list = await _service.GetPredictions(
            filters, null, "potentialProfit", "desc", 1, 50,
            new[] { active });

        var listItem = list.Items.FirstOrDefault(i => i.ListingId == active);

        Assert.Multiple(() =>
        {
            Assert.That(detail, Is.Not.Null);
            Assert.That(listItem, Is.Not.Null);
            Assert.That(detail!.AverageSoldPrice, Is.EqualTo(listItem!.AverageSoldPrice).Within(0.01m),
                "Detail and list view should return the same average sold price");
        });
    }

    // -- Helpers --

    private async Task<int> CreateListing(string status, decimal price,
        DateTime? soldDate = null, string? condition = null)
    {
        var listing = new Listing
        {
            ListingId = $"test-{Guid.NewGuid():N}",
            Title = $"Test Listing £{price}",
            Price = price,
            Currency = "GBP",
            ListingStatus = status,
            Condition = condition ?? "Used",
            ScrapeJobId = _testJobId,
            CreatedUtc = DateTime.UtcNow.AddDays(-30),
            EndDateUtc = soldDate
        };
        _db.Listings.Add(listing);
        await _db.SaveChangesAsync();
        _createdListingIds.Add(listing.Id);
        return listing.Id;
    }

    private async Task CreateRelationship(int activeId, int soldId, double classifierConfidence)
    {
        var (a, b) = activeId < soldId ? (activeId, soldId) : (soldId, activeId);
        var rel = new ListingRelationship
        {
            ListingIdA = a,
            ListingIdB = b,
            IsComparable = true,
            Explanation = $"Test: confidence={classifierConfidence:F3}",
            SimilarityScore = 0.85,
            ClassifierConfidence = classifierConfidence,
            CreatedUtc = DateTime.UtcNow
        };
        _db.ListingRelationships.Add(rel);
        await _db.SaveChangesAsync();
        _createdRelationshipIds.Add(rel.Id);
    }
}
