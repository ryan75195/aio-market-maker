using System.Text.Json;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services.Taxonomy;
using AIOMarketMaker.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class TaxonomyPersistenceService_UnitTests
{
    private EtlDbContext _db = null!;
    private TaxonomyPersistenceService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _db = InMemoryDbContextFactory.Create();
        _service = new TaxonomyPersistenceService(_db);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    [Test]
    public async Task Should_save_run_with_axes_and_values()
    {
        var job = AddJob();
        var listings = AddListings(job.Id, 3);
        var result = BuildTaxonomyResult(
            axes: new[] { new Axis("Axis 0", new[]
            {
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 50) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 40) })
            }) },
            assignmentCount: 3,
            coveragePercent: 66.7);

        var persisted = await _service.Save(job.Id, result, listings.Select(l => l.Id), 100);

        Assert.Multiple(() =>
        {
            Assert.That(persisted.RunId, Is.GreaterThan(0));
            Assert.That(persisted.AxisCount, Is.EqualTo(1));
            Assert.That(persisted.CoveragePercent, Is.EqualTo(66.7).Within(0.1));
        });

        var savedRun = await _db.TaxonomyRuns
            .Include(r => r.Axes).ThenInclude(a => a.Values)
            .FirstAsync(r => r.Id == persisted.RunId);

        Assert.Multiple(() =>
        {
            Assert.That(savedRun.ScrapeJobId, Is.EqualTo(job.Id));
            Assert.That(savedRun.AxisCount, Is.EqualTo(1));
            Assert.That(savedRun.DurationMs, Is.EqualTo(100));
            Assert.That(savedRun.Axes.Count, Is.EqualTo(1));
        });

        var axis = savedRun.Axes.First();
        Assert.Multiple(() =>
        {
            Assert.That(axis.Name, Is.EqualTo("Axis 0"));
            Assert.That(axis.Values.Count, Is.EqualTo(2));
            Assert.That(axis.Values.Any(v => v.Label == "disc"), Is.True);
            Assert.That(axis.Values.Any(v => v.Label == "digital"), Is.True);
        });
    }

    [Test]
    public async Task Should_save_listing_assignments_with_cell_json()
    {
        var job = AddJob();
        var listings = AddListings(job.Id, 2);
        var cell = new Dictionary<string, string> { ["Axis 0"] = "disc" };
        var result = BuildTaxonomyResult(
            assignmentCount: 0,
            assignments: new[]
            {
                new CellAssignment(0, cell, false),
                new CellAssignment(1, new Dictionary<string, string>(), false)
            });

        var persisted = await _service.Save(job.Id, result, listings.Select(l => l.Id), 50);

        // Re-fetch to avoid tracker issues after ChangeTracker.Clear()
        var assignments = await _db.TaxonomyListingAssignments
            .Where(a => a.TaxonomyRunId == persisted.RunId)
            .OrderBy(a => a.ListingId)
            .ToListAsync();

        Assert.That(assignments.Count, Is.EqualTo(2));
        var first = assignments.First(a => a.ListingId == listings[0].Id);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(first.CellJson);
        Assert.That(parsed!["Axis 0"], Is.EqualTo("disc"));
    }

    [Test]
    public async Task Should_replace_existing_run_for_same_job()
    {
        var job = AddJob();
        var listings = AddListings(job.Id, 2);
        var result1 = BuildTaxonomyResult(assignmentCount: 2, coveragePercent: 50.0);
        var result2 = BuildTaxonomyResult(assignmentCount: 2, coveragePercent: 80.0);

        await _service.Save(job.Id, result1, listings.Select(l => l.Id), 100);

        // Need fresh service since ChangeTracker was cleared
        var service2 = new TaxonomyPersistenceService(_db);
        await service2.Save(job.Id, result2, listings.Select(l => l.Id), 200);

        var runs = await _db.TaxonomyRuns.Where(r => r.ScrapeJobId == job.Id).ToListAsync();
        Assert.That(runs.Count, Is.EqualTo(1));
        Assert.That(runs[0].CoveragePercent, Is.EqualTo(80.0).Within(0.1));
    }

    [Test]
    public async Task Should_handle_empty_axes()
    {
        var job = AddJob();
        var listings = AddListings(job.Id, 2);
        var result = BuildTaxonomyResult(assignmentCount: 2, coveragePercent: 0);

        var persisted = await _service.Save(job.Id, result, listings.Select(l => l.Id), 10);

        Assert.Multiple(() =>
        {
            Assert.That(persisted.AxisCount, Is.EqualTo(0));
            Assert.That(persisted.AssignedListings, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Should_batch_large_assignment_sets()
    {
        var job = AddJob();
        var listings = AddListings(job.Id, 600);
        var assignments = Enumerable.Range(0, 600)
            .Select(i => new CellAssignment(i,
                new Dictionary<string, string> { ["Axis 0"] = $"val{i % 3}" }, false))
            .ToList();
        var result = new TaxonomyResult(
            Enumerable.Empty<Axis>(),
            assignments,
            Enumerable.Empty<CellStats>(),
            100.0, 0.0);

        var persisted = await _service.Save(job.Id, result, listings.Select(l => l.Id), 500);

        // Re-query since ChangeTracker was cleared during batching
        var count = await _db.TaxonomyListingAssignments
            .CountAsync(a => a.TaxonomyRunId == persisted.RunId);
        Assert.That(count, Is.EqualTo(600));
    }

    [Test]
    public async Task Should_mark_conflict_flag_on_assignments()
    {
        var job = AddJob();
        var listings = AddListings(job.Id, 2);
        var result = BuildTaxonomyResult(
            assignmentCount: 0,
            assignments: new[]
            {
                new CellAssignment(0, new Dictionary<string, string> { ["Axis 0"] = "disc" }, true),
                new CellAssignment(1, new Dictionary<string, string> { ["Axis 0"] = "digital" }, false)
            });

        var persisted = await _service.Save(job.Id, result, listings.Select(l => l.Id), 50);

        var assignments = await _db.TaxonomyListingAssignments
            .Where(a => a.TaxonomyRunId == persisted.RunId)
            .ToListAsync();

        Assert.That(assignments.Count(a => a.HasConflict), Is.EqualTo(1));
    }

    // -- Helpers --

    private ScrapeJob AddJob(string searchTerm = "PS5")
    {
        var job = new ScrapeJob { SearchTerm = searchTerm };
        _db.ScrapeJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    private List<Listing> AddListings(int jobId, int count)
    {
        var listings = new List<Listing>();
        for (var i = 0; i < count; i++)
        {
            var listing = new Listing
            {
                ListingId = $"ebay-{jobId}-{i}",
                ScrapeJobId = jobId,
                Title = $"Test Item {i}",
                Price = 100m + i
            };
            _db.Listings.Add(listing);
            listings.Add(listing);
        }
        _db.SaveChanges();
        return listings;
    }

    private static TaxonomyResult BuildTaxonomyResult(
        IEnumerable<Axis>? axes = null,
        int assignmentCount = 0,
        IEnumerable<CellAssignment>? assignments = null,
        double coveragePercent = 0)
    {
        var axesList = axes?.ToList() ?? new List<Axis>();
        var assignmentsList = assignments?.ToList()
            ?? Enumerable.Range(0, assignmentCount)
                .Select(i => new CellAssignment(i,
                    axesList.Count > 0
                        ? new Dictionary<string, string> { [axesList[0].Name] = axesList[0].Values.First().Label }
                        : new Dictionary<string, string>(),
                    false))
                .ToList();

        return new TaxonomyResult(axesList, assignmentsList, Enumerable.Empty<CellStats>(),
            coveragePercent, 0.0);
    }
}
