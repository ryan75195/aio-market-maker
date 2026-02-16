using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class EffectiveEnabled_UnitTests
{
    private EtlDbContext _db = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<EtlDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new EtlDbContext(options);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    [Test]
    public async Task Should_include_uncategorized_enabled_job()
    {
        _db.ScrapeJobs.Add(new ScrapeJob { SearchTerm = "PS5", IsEnabled = true });
        await _db.SaveChangesAsync();

        var jobs = await _db.ScrapeJobs.WhereEffectivelyEnabled().ToListAsync();
        Assert.That(jobs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Should_exclude_uncategorized_disabled_job()
    {
        _db.ScrapeJobs.Add(new ScrapeJob { SearchTerm = "PS5", IsEnabled = false });
        await _db.SaveChangesAsync();

        var jobs = await _db.ScrapeJobs.WhereEffectivelyEnabled().ToListAsync();
        Assert.That(jobs, Is.Empty);
    }

    [Test]
    public async Task Should_include_job_with_one_enabled_category()
    {
        var job = new ScrapeJob { SearchTerm = "PS5", IsEnabled = true };
        var cat = new Category { Name = "Electronics", IsEnabled = true };
        _db.ScrapeJobs.Add(job);
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();

        _db.JobCategories.Add(new JobCategory { JobId = job.Id, CategoryId = cat.Id });
        await _db.SaveChangesAsync();

        var jobs = await _db.ScrapeJobs.WhereEffectivelyEnabled().ToListAsync();
        Assert.That(jobs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Should_exclude_job_with_all_categories_disabled()
    {
        var job = new ScrapeJob { SearchTerm = "PS5", IsEnabled = true };
        var cat = new Category { Name = "Electronics", IsEnabled = false };
        _db.ScrapeJobs.Add(job);
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();

        _db.JobCategories.Add(new JobCategory { JobId = job.Id, CategoryId = cat.Id });
        await _db.SaveChangesAsync();

        var jobs = await _db.ScrapeJobs.WhereEffectivelyEnabled().ToListAsync();
        Assert.That(jobs, Is.Empty);
    }

    [Test]
    public async Task Should_include_job_when_any_category_is_enabled()
    {
        var job = new ScrapeJob { SearchTerm = "Rolex", IsEnabled = true };
        var catEnabled = new Category { Name = "Watches", IsEnabled = true };
        var catDisabled = new Category { Name = "Luxury", IsEnabled = false };
        _db.ScrapeJobs.Add(job);
        _db.Categories.AddRange(catEnabled, catDisabled);
        await _db.SaveChangesAsync();

        _db.JobCategories.Add(new JobCategory { JobId = job.Id, CategoryId = catEnabled.Id });
        _db.JobCategories.Add(new JobCategory { JobId = job.Id, CategoryId = catDisabled.Id });
        await _db.SaveChangesAsync();

        var jobs = await _db.ScrapeJobs.WhereEffectivelyEnabled().ToListAsync();
        Assert.That(jobs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Should_exclude_job_when_job_disabled_even_if_category_enabled()
    {
        var job = new ScrapeJob { SearchTerm = "PS5", IsEnabled = false };
        var cat = new Category { Name = "Electronics", IsEnabled = true };
        _db.ScrapeJobs.Add(job);
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();

        _db.JobCategories.Add(new JobCategory { JobId = job.Id, CategoryId = cat.Id });
        await _db.SaveChangesAsync();

        var jobs = await _db.ScrapeJobs.WhereEffectivelyEnabled().ToListAsync();
        Assert.That(jobs, Is.Empty);
    }
}
