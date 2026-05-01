using System.Reflection;
using AIOMarketMaker.Api.Endpoints;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace AIOMarketMaker.Tests.Unit.Endpoints;

[TestFixture]
[Category("Unit")]
public class OverviewEndpoints_UnitTests
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

    private async Task<OverviewResponse> CallOverview(int? minComps = null)
    {
        var method = typeof(OverviewEndpoints).GetMethod(
            "GetOverview",
            BindingFlags.NonPublic | BindingFlags.Static);

        var pricingOptions = Options.Create(new PricingOptions());
        var resultTask = (Task<IResult>)method!.Invoke(null,
            new object?[] { _db, pricingOptions, minComps })!;
        var result = await resultTask;

        var okResult = (Ok<OverviewResponse>)result;
        return okResult.Value!;
    }

    [Test]
    public async Task Should_return_correct_listing_counts_by_status()
    {
        var job = new ScrapeJob { SearchTerm = "test" };
        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync();

        _db.Listings.AddRange(
            new Listing { ListingId = "1", ListingStatus = "Active", ScrapeJobId = job.Id },
            new Listing { ListingId = "2", ListingStatus = "Active", ScrapeJobId = job.Id },
            new Listing { ListingId = "3", ListingStatus = "Sold", ScrapeJobId = job.Id },
            new Listing { ListingId = "4", ListingStatus = "Ended", ScrapeJobId = job.Id },
            new Listing { ListingId = "5", ListingStatus = "OutOfStock", ScrapeJobId = job.Id });
        await _db.SaveChangesAsync();

        var response = await CallOverview();

        Assert.Multiple(() =>
        {
            Assert.That(response.TotalListings, Is.EqualTo(5));
            Assert.That(response.ActiveListings, Is.EqualTo(2));
            Assert.That(response.SoldListings, Is.EqualTo(1));
            Assert.That(response.EndedListings, Is.EqualTo(2), "Ended should include both Ended and OutOfStock");
        });
    }

    [Test]
    public async Task Should_return_empty_overview_when_no_data()
    {
        var response = await CallOverview();

        Assert.Multiple(() =>
        {
            Assert.That(response.TotalListings, Is.EqualTo(0));
            Assert.That(response.ActiveListings, Is.EqualTo(0));
            Assert.That(response.SoldListings, Is.EqualTo(0));
            Assert.That(response.EndedListings, Is.EqualTo(0));
            Assert.That(response.Opportunities, Is.EqualTo(0));
            Assert.That(response.AggregateProfit, Is.EqualTo(0m));
            Assert.That(response.LastScrape, Is.Null);
            Assert.That(response.CumulativeGrowth, Is.Empty);
            Assert.That(response.TopJobsByOpportunities, Is.Empty);
        });
    }

    [Test]
    public async Task Should_return_cumulative_growth_by_date()
    {
        var job = new ScrapeJob { SearchTerm = "test" };
        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync();

        var day1 = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 1, 11, 14, 0, 0, DateTimeKind.Utc);
        var day3 = new DateTime(2026, 1, 12, 9, 0, 0, DateTimeKind.Utc);

        _db.Listings.AddRange(
            new Listing { ListingId = "1", ListingStatus = "Active", ScrapeJobId = job.Id, CreatedUtc = day1 },
            new Listing { ListingId = "2", ListingStatus = "Active", ScrapeJobId = job.Id, CreatedUtc = day1 },
            new Listing { ListingId = "3", ListingStatus = "Sold", ScrapeJobId = job.Id, CreatedUtc = day2 },
            new Listing { ListingId = "4", ListingStatus = "Active", ScrapeJobId = job.Id, CreatedUtc = day3 },
            new Listing { ListingId = "5", ListingStatus = "Active", ScrapeJobId = job.Id, CreatedUtc = day3 },
            new Listing { ListingId = "6", ListingStatus = "Active", ScrapeJobId = job.Id, CreatedUtc = day3 });
        await _db.SaveChangesAsync();

        var response = await CallOverview();

        var growth = response.CumulativeGrowth.ToList();

        Assert.Multiple(() =>
        {
            Assert.That(growth, Has.Count.EqualTo(3));

            Assert.That(growth[0].Date, Is.EqualTo("2026-01-10"));
            Assert.That(growth[0].Cumulative, Is.EqualTo(2));

            Assert.That(growth[1].Date, Is.EqualTo("2026-01-11"));
            Assert.That(growth[1].Cumulative, Is.EqualTo(3));

            Assert.That(growth[2].Date, Is.EqualTo("2026-01-12"));
            Assert.That(growth[2].Cumulative, Is.EqualTo(6));
        });
    }

    [Test]
    public async Task Should_return_empty_top_jobs_on_sqlite()
    {
        var job1 = new ScrapeJob { SearchTerm = "PlayStation 5" };
        _db.ScrapeJobs.Add(job1);
        await _db.SaveChangesAsync();

        _db.Listings.Add(
            new Listing { ListingId = "1", ListingStatus = "Active", ScrapeJobId = job1.Id });
        await _db.SaveChangesAsync();

        var response = await CallOverview();

        // No TaxonomyOpportunities seeded — top jobs should be empty
        Assert.That(response.TopJobsByOpportunities, Is.Empty);
    }

    [Test]
    public async Task Should_return_last_scrape_run()
    {
        var job = new ScrapeJob { SearchTerm = "test item" };
        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync();

        var olderRun = new ScrapeRun
        {
            StartedUtc = new DateTime(2026, 1, 10, 10, 0, 0, DateTimeKind.Utc),
            Status = "Completed",
            JobId = job.Id,
            ListingsAddedActive = 5,
            ListingsAddedSold = 2
        };
        var newerRun = new ScrapeRun
        {
            StartedUtc = new DateTime(2026, 1, 11, 10, 0, 0, DateTimeKind.Utc),
            Status = "Running",
            JobId = job.Id,
            ListingsAddedActive = 10,
            ListingsAddedSold = 3
        };
        _db.ScrapeRuns.AddRange(olderRun, newerRun);
        await _db.SaveChangesAsync();

        var response = await CallOverview();

        Assert.That(response.LastScrape, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response.LastScrape!.StartedUtc, Is.EqualTo(newerRun.StartedUtc));
            Assert.That(response.LastScrape.Status, Is.EqualTo("Running"));
            Assert.That(response.LastScrape.JobSearchTerm, Is.EqualTo("test item"));
            Assert.That(response.LastScrape.ListingsAddedActive, Is.EqualTo(10));
            Assert.That(response.LastScrape.ListingsAddedSold, Is.EqualTo(3));
        });
    }

}
