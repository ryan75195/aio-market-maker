using System.Reflection;
using AIOMarketMaker.Api.Endpoints;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AIOMarketMaker.Tests.Unit.Endpoints;

[TestFixture]
[Category("Unit")]
public class ListingEndpoints_UnitTests
{
    private EtlDbContext _db = null!;

    [SetUp]
    public void SetUp()
    {
        _db = InMemoryDbContextFactory.Create();
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    private async Task<IResult> CallGetListingDetail(int id)
    {
        var method = typeof(ListingEndpoints).GetMethod(
            "GetListingDetail",
            BindingFlags.NonPublic | BindingFlags.Static);

        var resultTask = (Task<IResult>)method!.Invoke(null, new object[] { _db, id })!;
        return await resultTask;
    }

    [Test]
    public async Task Should_return_404_when_listing_not_found()
    {
        var result = await CallGetListingDetail(999);
        Assert.That(result, Is.TypeOf<NotFound>());
    }

    [Test]
    public async Task Should_return_listing_with_empty_comparables()
    {
        var job = new ScrapeJob { SearchTerm = "PS5" };
        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync();

        var listing = new Listing
        {
            ListingId = "111", Title = "PS5 Console", Price = 350m,
            Currency = "GBP", Condition = "New", ListingStatus = "Active",
            ScrapeJobId = job.Id
        };
        _db.Listings.Add(listing);
        await _db.SaveChangesAsync();

        var result = await CallGetListingDetail(listing.Id);
        var ok = (Ok<ListingDetailResponse>)result;
        var response = ok.Value!;

        Assert.Multiple(() =>
        {
            Assert.That(response.Listing.ListingId, Is.EqualTo("111"));
            Assert.That(response.Listing.Title, Is.EqualTo("PS5 Console"));
            Assert.That(response.Listing.SearchTerm, Is.EqualTo("PS5"));
            Assert.That(response.Comparables, Is.Empty);
        });
    }

    [Test]
    public async Task Should_return_comparables_where_listing_is_A_side()
    {
        var job = new ScrapeJob { SearchTerm = "PS5" };
        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync();

        var active = new Listing
        {
            ListingId = "111", Title = "PS5 Active", Price = 350m,
            ListingStatus = "Active", ScrapeJobId = job.Id
        };
        var sold = new Listing
        {
            ListingId = "222", Title = "PS5 Sold", Price = 380m,
            ListingStatus = "Sold", ScrapeJobId = job.Id,
            Description = "Good condition PS5"
        };
        _db.Listings.AddRange(active, sold);
        await _db.SaveChangesAsync();

        _db.ListingRelationships.Add(new ListingRelationship
        {
            ListingIdA = active.Id, ListingIdB = sold.Id,
            IsComparable = true, SimilarityScore = 0.92,
            Explanation = "Same console"
        });
        await _db.SaveChangesAsync();

        var result = await CallGetListingDetail(active.Id);
        var ok = (Ok<ListingDetailResponse>)result;
        var response = ok.Value!;

        Assert.Multiple(() =>
        {
            Assert.That(response.Comparables.Count(), Is.EqualTo(1));
            var comp = response.Comparables.First();
            Assert.That(comp.ListingId, Is.EqualTo("222"));
            Assert.That(comp.Title, Is.EqualTo("PS5 Sold"));
            Assert.That(comp.Description, Is.EqualTo("Good condition PS5"));
            Assert.That(comp.SimilarityScore, Is.EqualTo(0.92).Within(0.001));
        });
    }

    [Test]
    public async Task Should_return_comparables_where_listing_is_B_side()
    {
        var job = new ScrapeJob { SearchTerm = "PS5" };
        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync();

        var active = new Listing
        {
            ListingId = "111", Title = "PS5 Active", Price = 350m,
            ListingStatus = "Active", ScrapeJobId = job.Id
        };
        var sold = new Listing
        {
            ListingId = "222", Title = "PS5 Sold", Price = 380m,
            ListingStatus = "Sold", ScrapeJobId = job.Id
        };
        _db.Listings.AddRange(active, sold);
        await _db.SaveChangesAsync();

        // Relationship where active listing is on B side
        _db.ListingRelationships.Add(new ListingRelationship
        {
            ListingIdA = sold.Id, ListingIdB = active.Id,
            IsComparable = true, SimilarityScore = 0.88,
            Explanation = "Same product"
        });
        await _db.SaveChangesAsync();

        var result = await CallGetListingDetail(active.Id);
        var ok = (Ok<ListingDetailResponse>)result;
        var response = ok.Value!;

        Assert.That(response.Comparables.Count(), Is.EqualTo(1));
        Assert.That(response.Comparables.First().ListingId, Is.EqualTo("222"));
    }

    [Test]
    public async Task Should_exclude_non_comparable_relationships()
    {
        var job = new ScrapeJob { SearchTerm = "PS5" };
        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync();

        var active = new Listing
        {
            ListingId = "111", ListingStatus = "Active", ScrapeJobId = job.Id
        };
        var sold = new Listing
        {
            ListingId = "222", ListingStatus = "Sold", ScrapeJobId = job.Id
        };
        _db.Listings.AddRange(active, sold);
        await _db.SaveChangesAsync();

        _db.ListingRelationships.Add(new ListingRelationship
        {
            ListingIdA = active.Id, ListingIdB = sold.Id,
            IsComparable = false, SimilarityScore = 0.3,
            Explanation = "Different product"
        });
        await _db.SaveChangesAsync();

        var result = await CallGetListingDetail(active.Id);
        var ok = (Ok<ListingDetailResponse>)result;

        Assert.That(ok.Value!.Comparables, Is.Empty);
    }
}
