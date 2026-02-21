using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Tests.Common;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ListingPredictionService_UnitTests
{
    private EtlDbContext _db = null!;
    private ListingPredictionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _db = InMemoryDbContextFactory.Create();
        _service = new ListingPredictionService(_db);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    // -- Helpers --

    private ScrapeJob AddJob(string searchTerm = "PS5")
    {
        var job = new ScrapeJob { SearchTerm = searchTerm };
        _db.ScrapeJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    private Listing AddListing(int jobId, string listingId, decimal? price,
        string condition, string status)
    {
        var listing = new Listing
        {
            ListingId = listingId, Price = price, Condition = condition,
            ListingStatus = status, ScrapeJobId = jobId, Title = $"Item {listingId}"
        };
        _db.Listings.Add(listing);
        _db.SaveChanges();
        return listing;
    }

    private ListingRelationship AddRelationship(int idA, int idB,
        bool isComparable = true, double score = 0.9)
    {
        var rel = new ListingRelationship
        {
            ListingIdA = idA, ListingIdB = idB,
            IsComparable = isComparable, SimilarityScore = score,
            Explanation = "Test relationship"
        };
        _db.ListingRelationships.Add(rel);
        _db.SaveChanges();
        return rel;
    }

    // -- GetComparables tests --

    [Test]
    public async Task GetComparables_should_return_empty_when_no_relationships()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");

        var result = await _service.GetComparables(active.Id, new PredictionFilters());

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetComparables_should_return_sold_comps_bidirectionally()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");
        var soldA = AddListing(job.Id, "222", 380m, "New", "Sold");
        var soldB = AddListing(job.Id, "333", 400m, "New", "Sold");

        // A-side relationship
        AddRelationship(active.Id, soldA.Id);
        // B-side relationship
        AddRelationship(soldB.Id, active.Id);

        var result = (await _service.GetComparables(active.Id, new PredictionFilters())).ToList();

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(c => c.ListingId), Is.EquivalentTo(new[] { "222", "333" }));
    }

    [Test]
    public async Task GetComparables_should_exclude_non_comparable_relationships()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");
        var sold = AddListing(job.Id, "222", 380m, "New", "Sold");

        AddRelationship(active.Id, sold.Id, isComparable: false);

        var result = await _service.GetComparables(active.Id, new PredictionFilters());

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetComparables_should_filter_by_condition_when_enabled()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");
        var soldSame = AddListing(job.Id, "222", 380m, "New", "Sold");
        var soldDiff = AddListing(job.Id, "333", 320m, "Used", "Sold");

        AddRelationship(active.Id, soldSame.Id);
        AddRelationship(active.Id, soldDiff.Id);

        var result = (await _service.GetComparables(active.Id,
            new PredictionFilters(MatchCondition: true))).ToList();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].ListingId, Is.EqualTo("222"));
    }

    [Test]
    public async Task GetComparables_should_return_all_conditions_when_match_disabled()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");
        var soldSame = AddListing(job.Id, "222", 380m, "New", "Sold");
        var soldDiff = AddListing(job.Id, "333", 320m, "Used", "Sold");

        AddRelationship(active.Id, soldSame.Id);
        AddRelationship(active.Id, soldDiff.Id);

        var result = (await _service.GetComparables(active.Id,
            new PredictionFilters(MatchCondition: false))).ToList();

        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetComparables_should_filter_by_price_band()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 100m, "New", "Active");
        var soldInBand = AddListing(job.Id, "222", 150m, "New", "Sold");    // 100*2=200, 100/2=50 -> in band
        var soldOutBand = AddListing(job.Id, "333", 250m, "New", "Sold");   // > 200 -> out of band

        AddRelationship(active.Id, soldInBand.Id);
        AddRelationship(active.Id, soldOutBand.Id);

        var result = (await _service.GetComparables(active.Id,
            new PredictionFilters(PriceBand: 2.0m, MatchCondition: false))).ToList();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].ListingId, Is.EqualTo("222"));
    }

    [Test]
    public async Task GetComparables_should_skip_price_band_when_zero()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 100m, "New", "Active");
        var sold = AddListing(job.Id, "222", 500m, "New", "Sold");

        AddRelationship(active.Id, sold.Id);

        var result = (await _service.GetComparables(active.Id,
            new PredictionFilters(PriceBand: 0, MatchCondition: false))).ToList();

        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetComparables_should_only_return_sold_listings()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");
        var otherActive = AddListing(job.Id, "222", 360m, "New", "Active");
        var ended = AddListing(job.Id, "333", 370m, "New", "Ended");
        var sold = AddListing(job.Id, "444", 380m, "New", "Sold");

        AddRelationship(active.Id, otherActive.Id);
        AddRelationship(active.Id, ended.Id);
        AddRelationship(active.Id, sold.Id);

        var result = (await _service.GetComparables(active.Id,
            new PredictionFilters(MatchCondition: false))).ToList();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].ListingId, Is.EqualTo("444"));
    }

    // -- GetPrediction tests --

    [Test]
    public async Task GetPrediction_should_return_null_when_no_comps()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");

        var result = await _service.GetPrediction(active.Id, new PredictionFilters());

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetPrediction_should_compute_count_and_avg_from_sold_comps()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");
        var sold1 = AddListing(job.Id, "222", 380m, "New", "Sold");
        var sold2 = AddListing(job.Id, "333", 400m, "New", "Sold");

        AddRelationship(active.Id, sold1.Id);
        AddRelationship(active.Id, sold2.Id);

        var result = await _service.GetPrediction(active.Id,
            new PredictionFilters(MatchCondition: false));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.SimilarSoldCount, Is.EqualTo(2));
            Assert.That(result.AverageSoldPrice, Is.EqualTo(390m));
            Assert.That(result.PotentialProfit, Is.EqualTo(40m)); // 390 - 350
        });
    }

    [Test]
    public async Task GetPrediction_should_apply_fee_percent()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 100m, "New", "Active");
        var sold = AddListing(job.Id, "222", 200m, "New", "Sold");

        AddRelationship(active.Id, sold.Id);

        var result = await _service.GetPrediction(active.Id,
            new PredictionFilters(FeePercent: 10m, MatchCondition: false));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            // Profit = 200 * (1 - 10/100) - 100 = 180 - 100 = 80
            Assert.That(result!.PotentialProfit, Is.EqualTo(80m));
        });
    }

    [Test]
    public async Task GetPrediction_should_deduct_shipping_cost_with_fees()
    {
        var job = AddJob();
        var listing = new Listing
        {
            ListingId = "111", Price = 100m, ShippingCost = 10m,
            Condition = "New", ListingStatus = "Active", ScrapeJobId = job.Id,
            Title = "Item 111"
        };
        _db.Listings.Add(listing);
        _db.SaveChanges();

        var sold = AddListing(job.Id, "222", 200m, "New", "Sold");
        AddRelationship(listing.Id, sold.Id);

        var result = await _service.GetPrediction(listing.Id,
            new PredictionFilters(FeePercent: 10m, MatchCondition: false));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            // Profit = 200 * (1 - 10/100) - 100 - 10 = 180 - 110 = 70
            Assert.That(result!.PotentialProfit, Is.EqualTo(70m));
        });
    }

    [Test]
    public async Task GetPrediction_should_respect_price_band_filter()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 100m, "New", "Active");
        var soldInBand = AddListing(job.Id, "222", 150m, "New", "Sold");    // in 2x band
        var soldOutBand = AddListing(job.Id, "333", 250m, "New", "Sold");   // out of 2x band

        AddRelationship(active.Id, soldInBand.Id);
        AddRelationship(active.Id, soldOutBand.Id);

        var result = await _service.GetPrediction(active.Id,
            new PredictionFilters(PriceBand: 2.0m, MatchCondition: false));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.SimilarSoldCount, Is.EqualTo(1));
            Assert.That(result.AverageSoldPrice, Is.EqualTo(150m));
        });
    }

    [Test]
    public async Task GetPrediction_should_exclude_zero_price_comps()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 100m, "New", "Active");
        var sold1 = AddListing(job.Id, "222", 200m, "New", "Sold");
        var sold2 = AddListing(job.Id, "333", 0m, "New", "Sold");

        AddRelationship(active.Id, sold1.Id);
        AddRelationship(active.Id, sold2.Id);

        var result = await _service.GetPrediction(active.Id,
            new PredictionFilters(MatchCondition: false));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.SimilarSoldCount, Is.EqualTo(1));
            Assert.That(result.AverageSoldPrice, Is.EqualTo(200m));
        });
    }

    [Test]
    public async Task GetPrediction_should_be_consistent_with_GetComparables()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 100m, "New", "Active");
        var sold1 = AddListing(job.Id, "222", 150m, "New", "Sold");
        var sold2 = AddListing(job.Id, "333", 180m, "Used", "Sold");

        AddRelationship(active.Id, sold1.Id);
        AddRelationship(active.Id, sold2.Id);

        var filters = new PredictionFilters(PriceBand: 2.0m, MatchCondition: true);

        var comps = (await _service.GetComparables(active.Id, filters)).ToList();
        var prediction = await _service.GetPrediction(active.Id, filters);

        Assert.Multiple(() =>
        {
            // Only sold1 matches (New condition + in price band)
            Assert.That(comps, Has.Count.EqualTo(1));
            Assert.That(prediction, Is.Not.Null);
            Assert.That(prediction!.SimilarSoldCount, Is.EqualTo(comps.Count));

            var expectedAvg = comps.Where(c => c.Price > 0).Average(c => c.Price!.Value);
            Assert.That(prediction.AverageSoldPrice, Is.EqualTo(expectedAvg));
        });
    }
}
