using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Tests.Common;
using Microsoft.Extensions.Options;
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
        _service = new ListingPredictionService(_db, Options.Create(new PricingOptions()));
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

    private ListingPrediction AddPrediction(int listingId, decimal avgSoldPrice,
        decimal potentialProfit, int similarSoldCount = 5, int? estimatedDaysToSell = 7,
        double confidence = 0.85, int outliersRemoved = 1, decimal? medianSoldPrice = null)
    {
        var prediction = new ListingPrediction
        {
            ListingId = listingId,
            SimilarSoldCount = similarSoldCount,
            AverageSoldPrice = avgSoldPrice,
            PotentialProfit = potentialProfit,
            EstimatedDaysToSell = estimatedDaysToSell,
            Confidence = confidence,
            OutliersRemoved = outliersRemoved,
            MedianSoldPrice = medianSoldPrice ?? avgSoldPrice - 5m,
            ComputedUtc = DateTime.UtcNow
        };
        _db.ListingPredictions.Add(prediction);
        _db.SaveChanges();
        return prediction;
    }

    // -- GetPrediction tests --

    [Test]
    public async Task Should_return_prediction_from_table_when_exists()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");

        AddPrediction(active.Id,
            avgSoldPrice: 400m, potentialProfit: 50m, similarSoldCount: 5,
            estimatedDaysToSell: 7, confidence: 0.85, outliersRemoved: 1,
            medianSoldPrice: 395m);

        var result = await _service.GetPrediction(active.Id, new PredictionFilters());

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.ListingId, Is.EqualTo(active.Id));
            Assert.That(result.SimilarSoldCount, Is.EqualTo(5));
            Assert.That(result.AverageSoldPrice, Is.EqualTo(400m));
            Assert.That(result.PotentialProfit, Is.EqualTo(50m));
            Assert.That(result.EstimatedDaysToSell, Is.EqualTo(7));
            Assert.That(result.Confidence, Is.EqualTo(0.85));
            Assert.That(result.OutliersRemoved, Is.EqualTo(1));
            Assert.That(result.MedianSoldPrice, Is.EqualTo(395m));
        });
    }

    [Test]
    public async Task Should_return_null_when_no_prediction_exists()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");

        var result = await _service.GetPrediction(active.Id, new PredictionFilters());

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_return_null_when_listing_does_not_exist()
    {
        var result = await _service.GetPrediction(9999, new PredictionFilters());

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_map_null_estimated_days_to_sell()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");

        AddPrediction(active.Id,
            avgSoldPrice: 400m, potentialProfit: 50m,
            estimatedDaysToSell: null);

        var result = await _service.GetPrediction(active.Id, new PredictionFilters());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.EstimatedDaysToSell, Is.Null);
    }

    [Test]
    public async Task Should_map_null_median_sold_price()
    {
        var job = AddJob();
        var active = AddListing(job.Id, "111", 350m, "New", "Active");

        var prediction = new ListingPrediction
        {
            ListingId = active.Id,
            SimilarSoldCount = 3,
            AverageSoldPrice = 400m,
            PotentialProfit = 50m,
            EstimatedDaysToSell = 7,
            Confidence = 0.80,
            OutliersRemoved = 0,
            MedianSoldPrice = null,
            ComputedUtc = DateTime.UtcNow
        };
        _db.ListingPredictions.Add(prediction);
        _db.SaveChanges();

        var result = await _service.GetPrediction(active.Id, new PredictionFilters());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.MedianSoldPrice, Is.Null);
    }

    [Test]
    public async Task Should_return_correct_prediction_when_multiple_listings_have_predictions()
    {
        var job = AddJob();
        var listing1 = AddListing(job.Id, "111", 350m, "New", "Active");
        var listing2 = AddListing(job.Id, "222", 200m, "Used", "Active");

        AddPrediction(listing1.Id, avgSoldPrice: 400m, potentialProfit: 50m);
        AddPrediction(listing2.Id, avgSoldPrice: 250m, potentialProfit: 30m);

        var result = await _service.GetPrediction(listing2.Id, new PredictionFilters());

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.ListingId, Is.EqualTo(listing2.Id));
            Assert.That(result.AverageSoldPrice, Is.EqualTo(250m));
            Assert.That(result.PotentialProfit, Is.EqualTo(30m));
        });
    }

    // -- GetPredictions tests --
    // These tests use raw SQL with SQL Server-specific syntax (OFFSET/FETCH, ISNULL,
    // [Condition] bracket escaping, TOP). SQLite in-memory does not support these,
    // so they are marked as Integration/Explicit and require a SQL Server instance.

    [Test]
    [Category("Integration")]
    [Explicit("GetPredictions uses raw SQL with OFFSET/FETCH and ISNULL which require SQL Server")]
    public async Task Should_return_paged_predictions_sorted_by_profit()
    {
        var job = AddJob();
        var l1 = AddListing(job.Id, "001", 100m, "New", "Active");
        var l2 = AddListing(job.Id, "002", 200m, "New", "Active");
        var l3 = AddListing(job.Id, "003", 300m, "New", "Active");
        var l4 = AddListing(job.Id, "004", 400m, "New", "Active");
        var l5 = AddListing(job.Id, "005", 500m, "New", "Active");

        AddPrediction(l1.Id, avgSoldPrice: 150m, potentialProfit: 10m);
        AddPrediction(l2.Id, avgSoldPrice: 280m, potentialProfit: 40m);
        AddPrediction(l3.Id, avgSoldPrice: 500m, potentialProfit: 100m);
        AddPrediction(l4.Id, avgSoldPrice: 550m, potentialProfit: 80m);
        AddPrediction(l5.Id, avgSoldPrice: 520m, potentialProfit: 5m);

        var result = await _service.GetPredictions(
            new PredictionFilters(), jobIds: null,
            sortBy: "potentialProfit", sortDir: "desc",
            page: 1, pageSize: 3);

        Assert.Multiple(() =>
        {
            Assert.That(result.TotalCount, Is.EqualTo(5));
            Assert.That(result.PageSize, Is.EqualTo(3));
            Assert.That(result.TotalPages, Is.EqualTo(2));
            Assert.That(result.Page, Is.EqualTo(1));
        });

        var items = result.Items.ToList();
        Assert.That(items, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(items[0].PotentialProfit, Is.EqualTo(100m));
            Assert.That(items[1].PotentialProfit, Is.EqualTo(80m));
            Assert.That(items[2].PotentialProfit, Is.EqualTo(40m));
        });
    }

    [Test]
    [Category("Integration")]
    [Explicit("GetPredictions uses raw SQL with OFFSET/FETCH and ISNULL which require SQL Server")]
    public async Task Should_filter_predictions_by_job_ids()
    {
        var job1 = AddJob("PS5");
        var job2 = AddJob("Xbox");

        var l1 = AddListing(job1.Id, "001", 300m, "New", "Active");
        var l2 = AddListing(job1.Id, "002", 350m, "New", "Active");
        var l3 = AddListing(job2.Id, "003", 250m, "New", "Active");

        AddPrediction(l1.Id, avgSoldPrice: 400m, potentialProfit: 50m);
        AddPrediction(l2.Id, avgSoldPrice: 450m, potentialProfit: 60m);
        AddPrediction(l3.Id, avgSoldPrice: 300m, potentialProfit: 20m);

        var result = await _service.GetPredictions(
            new PredictionFilters(), jobIds: new[] { job1.Id },
            sortBy: "potentialProfit", sortDir: "desc",
            page: 1, pageSize: 10);

        Assert.That(result.TotalCount, Is.EqualTo(2));
        var items = result.Items.ToList();
        Assert.That(items, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(items[0].PotentialProfit, Is.EqualTo(60m));
            Assert.That(items[1].PotentialProfit, Is.EqualTo(50m));
        });
    }

    // -- GetAggregates tests --
    // GetAggregates uses raw ADO.NET with SQL Server-specific syntax (ISNULL, TOP,
    // [Condition] bracket escaping). SQLite does not support these functions,
    // so these tests require a SQL Server instance.

    [Test]
    [Category("Integration")]
    [Explicit("GetAggregates uses raw SQL with ISNULL, TOP, and [Condition] bracket escaping which require SQL Server")]
    public async Task Should_return_aggregates_from_table()
    {
        var job = AddJob();
        var l1 = AddListing(job.Id, "001", 100m, "New", "Active");
        var l2 = AddListing(job.Id, "002", 200m, "Used", "Active");
        var l3 = AddListing(job.Id, "003", 300m, "New", "Active");

        AddPrediction(l1.Id, avgSoldPrice: 150m, potentialProfit: 25m);
        AddPrediction(l2.Id, avgSoldPrice: 280m, potentialProfit: 40m);
        AddPrediction(l3.Id, avgSoldPrice: 450m, potentialProfit: 75m);

        var result = await _service.GetAggregates(new PredictionFilters());

        Assert.Multiple(() =>
        {
            Assert.That(result.Opportunities, Is.EqualTo(3));
            Assert.That(result.AggregateProfit, Is.EqualTo(140m));
        });
    }
}
