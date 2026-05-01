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
public class OpportunityEndpoints_UnitTests
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

    private async Task<OpportunityPagedResponse> CallGetOpportunities(
        int page = 1,
        int pageSize = 50,
        string sortBy = "estimatedProfit",
        string sortDir = "desc",
        string? jobIds = null,
        string? categoryIds = null,
        decimal maxPrice = 0,
        string? search = null,
        int minComps = 0)
    {
        var method = typeof(OpportunityEndpoints).GetMethod(
            "GetOpportunities",
            BindingFlags.NonPublic | BindingFlags.Static);

        var resultTask = (Task<IResult>)method!.Invoke(null,
            new object?[] { _db, page, pageSize, sortBy, sortDir, jobIds, categoryIds, maxPrice, search, minComps })!;
        var result = await resultTask;

        var okResult = (Ok<OpportunityPagedResponse>)result;
        return okResult.Value!;
    }

    [Test]
    public async Task Should_return_opportunities_sorted_by_profit_descending()
    {
        await SeedTestData();

        var response = await CallGetOpportunities();

        var items = response.Items.ToList();
        Assert.Multiple(() =>
        {
            Assert.That(items, Has.Count.EqualTo(2));
            Assert.That(items[0].EstimatedProfit, Is.GreaterThan(items[1].EstimatedProfit));
            Assert.That(response.TotalCount, Is.EqualTo(2));
            Assert.That(response.TotalPages, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Should_return_opportunities_sorted_by_profit_ascending()
    {
        await SeedTestData();

        var response = await CallGetOpportunities(sortDir: "asc");

        var items = response.Items.ToList();
        Assert.That(items[0].EstimatedProfit, Is.LessThan(items[1].EstimatedProfit));
    }

    [Test]
    public async Task Should_filter_by_job_ids()
    {
        await SeedTestData();

        var response = await CallGetOpportunities(jobIds: "1");

        var items = response.Items.ToList();
        Assert.Multiple(() =>
        {
            Assert.That(items, Has.Count.EqualTo(1));
            Assert.That(items[0].SearchTerm, Is.EqualTo("PS5"));
            Assert.That(response.TotalCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Should_filter_by_max_price()
    {
        await SeedTestData();

        var response = await CallGetOpportunities(maxPrice: 120);

        var items = response.Items.ToList();
        Assert.Multiple(() =>
        {
            Assert.That(items, Has.Count.EqualTo(1));
            Assert.That(items[0].AskPrice, Is.LessThanOrEqualTo(120));
        });
    }

    [Test]
    public async Task Should_filter_by_text_search()
    {
        await SeedTestData();

        var response = await CallGetOpportunities(search: "PS5");

        var items = response.Items.ToList();
        Assert.Multiple(() =>
        {
            Assert.That(items, Has.Count.EqualTo(1));
            Assert.That(items[0].Title, Does.Contain("PS5"));
        });
    }

    [Test]
    public async Task Should_filter_by_min_comps()
    {
        await SeedTestData();

        var response = await CallGetOpportunities(minComps: 6);

        var items = response.Items.ToList();
        Assert.Multiple(() =>
        {
            Assert.That(items, Has.Count.EqualTo(1));
            Assert.That(items[0].SoldComps, Is.GreaterThanOrEqualTo(6));
        });
    }

    [Test]
    public async Task Should_paginate_results()
    {
        await SeedTestData();

        var response = await CallGetOpportunities(page: 1, pageSize: 1);

        Assert.Multiple(() =>
        {
            Assert.That(response.Items.Count(), Is.EqualTo(1));
            Assert.That(response.TotalCount, Is.EqualTo(2));
            Assert.That(response.TotalPages, Is.EqualTo(2));
            Assert.That(response.Page, Is.EqualTo(1));
            Assert.That(response.PageSize, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Should_calculate_aggregate_profit()
    {
        await SeedTestData();

        var response = await CallGetOpportunities();

        Assert.That(response.AggregateProfit, Is.EqualTo(73.50m + 92.90m).Within(0.01m));
    }

    [Test]
    public async Task Should_return_empty_when_no_opportunities()
    {
        var response = await CallGetOpportunities();

        Assert.Multiple(() =>
        {
            Assert.That(response.Items, Is.Empty);
            Assert.That(response.TotalCount, Is.EqualTo(0));
            Assert.That(response.AggregateProfit, Is.EqualTo(0m));
        });
    }

    [Test]
    public async Task Should_clamp_page_below_one_to_one()
    {
        await SeedTestData();

        var response = await CallGetOpportunities(page: -5);

        Assert.That(response.Page, Is.EqualTo(1));
        Assert.That(response.Items.Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task Should_clamp_page_size_above_200()
    {
        await SeedTestData();

        var response = await CallGetOpportunities(pageSize: 500);

        Assert.That(response.PageSize, Is.EqualTo(200));
    }

    [Test]
    public async Task Should_filter_by_category_ids()
    {
        await SeedTestData();

        // Add a category and link job 1 to it
        var category = new Category { Name = "Gaming" };
        _db.Categories.Add(category);
        await _db.SaveChangesAsync();

        _db.JobCategories.Add(new JobCategory { JobId = 1, CategoryId = category.Id });
        await _db.SaveChangesAsync();

        var response = await CallGetOpportunities(categoryIds: category.Id.ToString());

        var items = response.Items.ToList();
        Assert.Multiple(() =>
        {
            Assert.That(items, Has.Count.EqualTo(1));
            Assert.That(items[0].SearchTerm, Is.EqualTo("PS5"));
        });
    }

    [Test]
    public async Task Should_sort_by_ask_price()
    {
        await SeedTestData();

        var response = await CallGetOpportunities(sortBy: "askprice", sortDir: "asc");

        var items = response.Items.ToList();
        Assert.That(items[0].AskPrice, Is.LessThanOrEqualTo(items[1].AskPrice));
    }

    private async Task SeedTestData()
    {
        var job1 = new ScrapeJob { Id = 1, SearchTerm = "PS5", IsEnabled = true };
        var job2 = new ScrapeJob { Id = 2, SearchTerm = "Xbox", IsEnabled = true };
        _db.ScrapeJobs.AddRange(job1, job2);
        await _db.SaveChangesAsync();

        var listing1 = new Listing
        {
            Id = 10, ScrapeJobId = 1, ListingId = "ebay-10",
            Title = "PS5 Cheap", Price = 100, ListingStatus = "Active"
        };
        var listing2 = new Listing
        {
            Id = 20, ScrapeJobId = 2, ListingId = "ebay-20",
            Title = "Xbox Cheap", Price = 150, ListingStatus = "Active"
        };
        _db.Listings.AddRange(listing1, listing2);
        await _db.SaveChangesAsync();

        _db.TaxonomyOpportunities.AddRange(
            new TaxonomyOpportunity
            {
                ScrapeJobId = 1, ListingId = 10, CellKey = "edition=Digital",
                AskPrice = 100, MedianSoldPrice = 200, EstimatedProfit = 73.50m,
                MarginPercent = 36.75, SoldComps = 5, AvgDaysToSell = 7,
                ComputedUtc = DateTime.UtcNow
            },
            new TaxonomyOpportunity
            {
                ScrapeJobId = 2, ListingId = 20, CellKey = "bundle=Console",
                AskPrice = 150, MedianSoldPrice = 280, EstimatedProfit = 92.90m,
                MarginPercent = 33.18, SoldComps = 8, AvgDaysToSell = 12,
                ComputedUtc = DateTime.UtcNow
            });
        await _db.SaveChangesAsync();
    }
}
