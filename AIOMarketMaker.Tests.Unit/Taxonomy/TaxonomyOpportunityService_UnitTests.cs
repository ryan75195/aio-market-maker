using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services.Taxonomy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class TaxonomyOpportunityService_UnitTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<EtlDbContext> _options;
    private EtlDbContext _db = null!;
    private Mock<IDbContextFactory<EtlDbContext>> _dbFactory = null!;
    private Mock<ITaxonomyQueryService> _taxonomyQuery = null!;
    private ICellPricingService _cellPricing = null!;
    private TaxonomyOpportunityService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<EtlDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new EtlDbContext(_options);
        _db.Database.EnsureCreated();

        _dbFactory = new Mock<IDbContextFactory<EtlDbContext>>();
        _dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new EtlDbContext(_options));

        _taxonomyQuery = new Mock<ITaxonomyQueryService>();
        _cellPricing = new CellPricingService();

        _service = new TaxonomyOpportunityService(
            _dbFactory.Object, _taxonomyQuery.Object, _cellPricing);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task Should_persist_opportunities_for_profitable_active_listings()
    {
        // Arrange: 3 sold at ~200 + 1 active at 100, same cell
        var job = AddJob();
        var sold1 = AddListing(job.Id, "sold-1", "PS5 Digital Sold 1", 190m, "Sold",
            createdUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endDateUtc: new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc));
        var sold2 = AddListing(job.Id, "sold-2", "PS5 Digital Sold 2", 200m, "Sold",
            createdUtc: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            endDateUtc: new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc));
        var sold3 = AddListing(job.Id, "sold-3", "PS5 Digital Sold 3", 210m, "Sold",
            createdUtc: new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            endDateUtc: new DateTime(2026, 1, 13, 0, 0, 0, DateTimeKind.Utc));
        var active = AddListing(job.Id, "active-1", "PS5 Digital Cheap", 100m, "Active");
        _db.SaveChanges();

        var cell = new Dictionary<string, string> { ["edition"] = "digital" };
        _taxonomyQuery.Setup(q => q.GetAssignments(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ParsedAssignment(sold1.Id, new Dictionary<string, string>(cell)),
                new ParsedAssignment(sold2.Id, new Dictionary<string, string>(cell)),
                new ParsedAssignment(sold3.Id, new Dictionary<string, string>(cell)),
                new ParsedAssignment(active.Id, new Dictionary<string, string>(cell)),
            });

        // Act
        var count = await _service.Compute(job.Id, feePercent: 13.25, minComps: 3);

        // Assert
        Assert.That(count, Is.EqualTo(1));

        await using var verifyDb = new EtlDbContext(_options);
        var opps = await verifyDb.TaxonomyOpportunities
            .Where(o => o.ScrapeJobId == job.Id)
            .ToListAsync();

        Assert.That(opps, Has.Count.EqualTo(1));
        var opp = opps[0];
        Assert.Multiple(() =>
        {
            Assert.That(opp.ListingId, Is.EqualTo(active.Id));
            Assert.That(opp.AskPrice, Is.EqualTo(100m));
            Assert.That(opp.MedianSoldPrice, Is.EqualTo(200m));
            Assert.That(opp.EstimatedProfit, Is.GreaterThan(0));
            Assert.That(opp.MarginPercent, Is.GreaterThan(0));
            Assert.That(opp.SoldComps, Is.EqualTo(3));
            Assert.That(opp.CellKey, Does.Contain("digital"));
            Assert.That(opp.AvgDaysToSell, Is.Not.Null);
            Assert.That(opp.AvgDaysToSell, Is.GreaterThan(0));
            Assert.That(opp.ComputedUtc, Is.GreaterThan(DateTime.MinValue));
        });
    }

    [Test]
    public async Task Should_delete_previous_opportunities_for_job_before_inserting()
    {
        // Arrange: seed an old opportunity, then compute with empty assignments
        var job = AddJob();
        var listing = AddListing(job.Id, "old-1", "Old Listing", 100m, "Active");
        _db.SaveChanges();

        _db.TaxonomyOpportunities.Add(new TaxonomyOpportunity
        {
            ScrapeJobId = job.Id,
            ListingId = listing.Id,
            CellKey = "old-cell",
            AskPrice = 50m,
            MedianSoldPrice = 200m,
            EstimatedProfit = 100m,
            MarginPercent = 50.0,
            SoldComps = 5,
            ComputedUtc = DateTime.UtcNow.AddDays(-1)
        });
        _db.SaveChanges();

        _taxonomyQuery.Setup(q => q.GetAssignments(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ParsedAssignment>());

        // Act
        var count = await _service.Compute(job.Id, feePercent: 13.25, minComps: 3);

        // Assert: old opportunity deleted, nothing new inserted
        Assert.That(count, Is.EqualTo(0));

        await using var verifyDb = new EtlDbContext(_options);
        var remaining = await verifyDb.TaxonomyOpportunities
            .Where(o => o.ScrapeJobId == job.Id)
            .CountAsync();
        Assert.That(remaining, Is.EqualTo(0));
    }

    [Test]
    public async Task Should_skip_unprofitable_listings()
    {
        // Arrange: sold at 100, active at 150 → no profit after fees
        var job = AddJob();
        var sold1 = AddListing(job.Id, "sold-1", "PS5 Sold 1", 100m, "Sold");
        var sold2 = AddListing(job.Id, "sold-2", "PS5 Sold 2", 100m, "Sold");
        var sold3 = AddListing(job.Id, "sold-3", "PS5 Sold 3", 100m, "Sold");
        var active = AddListing(job.Id, "active-1", "PS5 Overpriced", 150m, "Active");
        _db.SaveChanges();

        var cell = new Dictionary<string, string> { ["edition"] = "digital" };
        _taxonomyQuery.Setup(q => q.GetAssignments(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ParsedAssignment(sold1.Id, new Dictionary<string, string>(cell)),
                new ParsedAssignment(sold2.Id, new Dictionary<string, string>(cell)),
                new ParsedAssignment(sold3.Id, new Dictionary<string, string>(cell)),
                new ParsedAssignment(active.Id, new Dictionary<string, string>(cell)),
            });

        // Act
        var count = await _service.Compute(job.Id, feePercent: 13.25, minComps: 3);

        // Assert: no opportunities (active costs more than median sold minus fees)
        Assert.That(count, Is.EqualTo(0));

        await using var verifyDb = new EtlDbContext(_options);
        var opps = await verifyDb.TaxonomyOpportunities
            .Where(o => o.ScrapeJobId == job.Id)
            .CountAsync();
        Assert.That(opps, Is.EqualTo(0));
    }

    // -- Helpers --

    private ScrapeJob AddJob(string searchTerm = "PS5")
    {
        var job = new ScrapeJob { SearchTerm = searchTerm };
        _db.ScrapeJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    private Listing AddListing(
        int jobId, string listingId, string title, decimal price, string status,
        string? condition = null, DateTime? createdUtc = null, DateTime? endDateUtc = null)
    {
        var listing = new Listing
        {
            ListingId = listingId,
            ScrapeJobId = jobId,
            Title = title,
            Price = price,
            ListingStatus = status,
            Condition = condition,
            CreatedUtc = createdUtc ?? DateTime.UtcNow,
            EndDateUtc = endDateUtc
        };
        _db.Listings.Add(listing);
        return listing;
    }
}
