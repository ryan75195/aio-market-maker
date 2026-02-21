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

    private async Task<IResult> CallDismissComparable(int listingId, int relationshipId)
    {
        var method = typeof(ListingEndpoints).GetMethod(
            "DismissComparable",
            BindingFlags.NonPublic | BindingFlags.Static);

        var resultTask = (Task<IResult>)method!.Invoke(null, new object[] { _db, listingId, relationshipId })!;
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
            Condition = "New", ListingStatus = "Active", ScrapeJobId = job.Id
        };
        var sold = new Listing
        {
            ListingId = "222", Title = "PS5 Sold", Price = 380m,
            Condition = "New", ListingStatus = "Sold", ScrapeJobId = job.Id,
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
            Condition = "New", ListingStatus = "Active", ScrapeJobId = job.Id
        };
        var sold = new Listing
        {
            ListingId = "222", Title = "PS5 Sold", Price = 380m,
            Condition = "New", ListingStatus = "Sold", ScrapeJobId = job.Id
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
            ListingId = "111", Condition = "New", ListingStatus = "Active", ScrapeJobId = job.Id
        };
        var sold = new Listing
        {
            ListingId = "222", Condition = "New", ListingStatus = "Sold", ScrapeJobId = job.Id
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

    [Test]
    public async Task Should_exclude_non_sold_and_condition_mismatched_comparables()
    {
        var job = new ScrapeJob { SearchTerm = "PS5" };
        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync();

        var active = new Listing
        {
            ListingId = "111", Title = "PS5 Active", Price = 350m,
            Condition = "New", ListingStatus = "Active", ScrapeJobId = job.Id
        };
        var soldSameCondition = new Listing
        {
            ListingId = "222", Title = "PS5 Sold New", Price = 380m,
            Condition = "New", ListingStatus = "Sold", ScrapeJobId = job.Id
        };
        var soldDifferentCondition = new Listing
        {
            ListingId = "333", Title = "PS5 Sold Used", Price = 320m,
            Condition = "Used", ListingStatus = "Sold", ScrapeJobId = job.Id
        };
        var otherActive = new Listing
        {
            ListingId = "444", Title = "PS5 Also Active", Price = 360m,
            Condition = "New", ListingStatus = "Active", ScrapeJobId = job.Id
        };
        var ended = new Listing
        {
            ListingId = "555", Title = "PS5 Ended", Price = 370m,
            Condition = "New", ListingStatus = "Ended", ScrapeJobId = job.Id
        };
        _db.Listings.AddRange(active, soldSameCondition, soldDifferentCondition, otherActive, ended);
        await _db.SaveChangesAsync();

        _db.ListingRelationships.AddRange(
            new ListingRelationship
            {
                ListingIdA = active.Id, ListingIdB = soldSameCondition.Id,
                IsComparable = true, SimilarityScore = 0.9, Explanation = "Same"
            },
            new ListingRelationship
            {
                ListingIdA = active.Id, ListingIdB = soldDifferentCondition.Id,
                IsComparable = true, SimilarityScore = 0.85, Explanation = "Same"
            },
            new ListingRelationship
            {
                ListingIdA = active.Id, ListingIdB = otherActive.Id,
                IsComparable = true, SimilarityScore = 0.85, Explanation = "Same"
            },
            new ListingRelationship
            {
                ListingIdA = active.Id, ListingIdB = ended.Id,
                IsComparable = true, SimilarityScore = 0.88, Explanation = "Same"
            });
        await _db.SaveChangesAsync();

        var result = await CallGetListingDetail(active.Id);
        var ok = (Ok<ListingDetailResponse>)result;
        var response = ok.Value!;

        Assert.Multiple(() =>
        {
            // Only the Sold listing with matching condition should appear
            Assert.That(response.Comparables.Count(), Is.EqualTo(1));
            Assert.That(response.Comparables.First().ListingId, Is.EqualTo("222"));
        });
    }

    [Test]
    public async Task Should_return_404_when_relationship_not_found()
    {
        var job = new ScrapeJob { SearchTerm = "PS5" };
        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync();

        var listing = new Listing
        {
            ListingId = "111", ListingStatus = "Active", ScrapeJobId = job.Id
        };
        _db.Listings.Add(listing);
        await _db.SaveChangesAsync();

        var result = await CallDismissComparable(listing.Id, 999);
        Assert.That(result, Is.TypeOf<NotFound>());
    }

    [Test]
    public async Task Should_delete_relationship_and_return_updated_detail()
    {
        var job = new ScrapeJob { SearchTerm = "PS5" };
        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync();

        var active = new Listing
        {
            ListingId = "111", Title = "PS5", Price = 350m,
            Condition = "New", ListingStatus = "Active", ScrapeJobId = job.Id
        };
        var sold1 = new Listing
        {
            ListingId = "222", Title = "PS5 Sold 1", Price = 380m,
            Condition = "New", ListingStatus = "Sold", ScrapeJobId = job.Id
        };
        var sold2 = new Listing
        {
            ListingId = "333", Title = "PS5 Sold 2", Price = 400m,
            Condition = "New", ListingStatus = "Sold", ScrapeJobId = job.Id
        };
        _db.Listings.AddRange(active, sold1, sold2);
        await _db.SaveChangesAsync();

        var rel1 = new ListingRelationship
        {
            ListingIdA = active.Id, ListingIdB = sold1.Id,
            IsComparable = true, SimilarityScore = 0.9, Explanation = "Same"
        };
        var rel2 = new ListingRelationship
        {
            ListingIdA = active.Id, ListingIdB = sold2.Id,
            IsComparable = true, SimilarityScore = 0.85, Explanation = "Same"
        };
        _db.ListingRelationships.AddRange(rel1, rel2);
        await _db.SaveChangesAsync();

        // Dismiss rel1
        var result = await CallDismissComparable(active.Id, rel1.Id);
        var ok = (Ok<ListingDetailResponse>)result;
        var response = ok.Value!;

        Assert.Multiple(() =>
        {
            // Should only have 1 comp remaining
            Assert.That(response.Comparables.Count(), Is.EqualTo(1));
            Assert.That(response.Comparables.First().ListingId, Is.EqualTo("333"));

            // Relationship should be deleted from DB
            Assert.That(_db.ListingRelationships.Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Should_reject_dismiss_when_relationship_does_not_belong_to_listing()
    {
        var job = new ScrapeJob { SearchTerm = "PS5" };
        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync();

        var listing1 = new Listing
        {
            ListingId = "111", Condition = "New", ListingStatus = "Active", ScrapeJobId = job.Id
        };
        var listing2 = new Listing
        {
            ListingId = "222", Condition = "New", ListingStatus = "Active", ScrapeJobId = job.Id
        };
        var sold = new Listing
        {
            ListingId = "333", Condition = "New", ListingStatus = "Sold", ScrapeJobId = job.Id
        };
        _db.Listings.AddRange(listing1, listing2, sold);
        await _db.SaveChangesAsync();

        var rel = new ListingRelationship
        {
            ListingIdA = listing2.Id, ListingIdB = sold.Id,
            IsComparable = true, SimilarityScore = 0.9, Explanation = "Same"
        };
        _db.ListingRelationships.Add(rel);
        await _db.SaveChangesAsync();

        // Try to dismiss rel via listing1 (doesn't own it)
        var result = await CallDismissComparable(listing1.Id, rel.Id);
        Assert.That(result, Is.TypeOf<NotFound>());

        // Relationship should still exist
        Assert.That(_db.ListingRelationships.Count(), Is.EqualTo(1));
    }
}
