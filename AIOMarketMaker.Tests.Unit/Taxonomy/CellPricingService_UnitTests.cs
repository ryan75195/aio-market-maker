using AIOMarketMaker.Core.Services.Taxonomy;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class CellPricingService_UnitTests
{
    private CellPricingService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new CellPricingService();
    }

    [Test]
    public void Should_compute_median_sold_price_per_cell()
    {
        // 3 sold listings in same cell at £100, £200, £300 → median = £200
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("Axis 0", new[] { "digital", "disc" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (1, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (2, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
            });

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital", 100m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "PS5 Digital", 200m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "PS5 Digital", 300m, IsSold: true, ListingIndex: 2),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 13.25, minComps: 1);
        var cell = result.Cells.Single();

        Assert.That(cell.MedianSoldPrice, Is.EqualTo(200m));
    }

    [Test]
    public void Should_flag_underpriced_active_listing_as_opportunity()
    {
        // Sold at £200 median, active at £100, fees 13.25% → profit = £200 - £100 - £26.50 = £73.50
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("Axis 0", new[] { "digital", "disc" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (1, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (2, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (3, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
            });

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital Sold 1", 150m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "PS5 Digital Sold 2", 200m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "PS5 Digital Sold 3", 250m, IsSold: true, ListingIndex: 2),
            new PricedListing(4, "PS5 Digital Cheap", 100m, IsSold: false, ListingIndex: 3),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 13.25, minComps: 3);

        Assert.That(result.Opportunities.Count(), Is.EqualTo(1));
        var opp = result.Opportunities.Single();
        Assert.Multiple(() =>
        {
            Assert.That(opp.ListingId, Is.EqualTo(4));
            Assert.That(opp.AskPrice, Is.EqualTo(100m));
            Assert.That(opp.MedianSoldPrice, Is.EqualTo(200m));
            Assert.That(opp.EstimatedProfit, Is.EqualTo(73.50m));
        });
    }

    [Test]
    public void Should_not_flag_overpriced_active_listing()
    {
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("Axis 0", new[] { "digital", "disc" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (1, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (2, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
            });

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital Sold", 200m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "PS5 Digital Sold", 200m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "PS5 Digital Active", 250m, IsSold: false, ListingIndex: 2),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 13.25, minComps: 1);

        Assert.That(result.Opportunities.Count(), Is.EqualTo(0));
    }

    [Test]
    public void Should_skip_cells_with_fewer_than_min_comps()
    {
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("Axis 0", new[] { "digital", "disc" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (1, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
            });

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital Sold", 200m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "PS5 Digital Active", 50m, IsSold: false, ListingIndex: 1),
        };

        // minComps=3 but only 1 sold → no opportunity
        var result = _service.Compute(taxonomy, listings, feePercent: 13.25, minComps: 3);

        Assert.That(result.Opportunities.Count(), Is.EqualTo(0));
    }

    [Test]
    public void Should_handle_unassigned_listings()
    {
        // Listing at index 1 has empty cell → should be excluded from pricing
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("Axis 0", new[] { "digital", "disc" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (1, new Dictionary<string, string>()),
            });

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital Sold", 200m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "Random Uncovered", 10m, IsSold: false, ListingIndex: 1),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 13.25, minComps: 1);

        Assert.That(result.Opportunities.Count(), Is.EqualTo(0));
        Assert.That(result.Cells.Count(), Is.EqualTo(1));
    }

    [Test]
    public void Should_separate_pricing_across_different_cells()
    {
        // Digital sells for £200, Disc sells for £400
        // Active Digital at £150 → opportunity. Active Disc at £350 → opportunity.
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("Axis 0", new[] { "digital", "disc" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (1, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (2, new Dictionary<string, string> { ["Axis 0"] = "digital" }),
                (3, new Dictionary<string, string> { ["Axis 0"] = "disc" }),
                (4, new Dictionary<string, string> { ["Axis 0"] = "disc" }),
                (5, new Dictionary<string, string> { ["Axis 0"] = "disc" }),
            });

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital Sold", 200m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "PS5 Digital Sold", 200m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "PS5 Digital Active", 150m, IsSold: false, ListingIndex: 2),
            new PricedListing(4, "PS5 Disc Sold", 400m, IsSold: true, ListingIndex: 3),
            new PricedListing(5, "PS5 Disc Sold", 400m, IsSold: true, ListingIndex: 4),
            new PricedListing(6, "PS5 Disc Active", 350m, IsSold: false, ListingIndex: 5),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 0, minComps: 2);

        Assert.That(result.Opportunities.Count(), Is.EqualTo(2));
        var digital = result.Opportunities.First(o => o.CellKey.Contains("digital"));
        var disc = result.Opportunities.First(o => o.CellKey.Contains("disc"));
        Assert.Multiple(() =>
        {
            Assert.That(digital.MedianSoldPrice, Is.EqualTo(200m));
            Assert.That(disc.MedianSoldPrice, Is.EqualTo(400m));
        });
    }

    [Test]
    public void Should_separate_cells_by_condition()
    {
        // Same taxonomy cell (digital), but NEW sells for £400, USED sells for £200
        // Without condition filtering, median would be £300 — wrong for both
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("edition", new[] { "digital", "disc" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["edition"] = "digital" }),
                (1, new Dictionary<string, string> { ["edition"] = "digital" }),
                (2, new Dictionary<string, string> { ["edition"] = "digital" }),
                (3, new Dictionary<string, string> { ["edition"] = "digital" }),
            });

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital New", 400m, IsSold: true, ListingIndex: 0, Condition: "NEW"),
            new PricedListing(2, "PS5 Digital New", 400m, IsSold: true, ListingIndex: 1, Condition: "NEW"),
            new PricedListing(3, "PS5 Digital Used", 200m, IsSold: true, ListingIndex: 2, Condition: "USED"),
            new PricedListing(4, "PS5 Digital Used", 200m, IsSold: true, ListingIndex: 3, Condition: "USED"),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 0, minComps: 1);
        var cells = result.Cells.ToList();

        Assert.That(cells, Has.Count.EqualTo(2));
        var newCell = cells.First(c => c.CellKey.Contains("NEW"));
        var usedCell = cells.First(c => c.CellKey.Contains("USED"));
        Assert.Multiple(() =>
        {
            Assert.That(newCell.MedianSoldPrice, Is.EqualTo(400m));
            Assert.That(usedCell.MedianSoldPrice, Is.EqualTo(200m));
        });
    }

    [Test]
    public void Should_handle_null_condition_as_separate_group()
    {
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("edition", new[] { "digital", "disc" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["edition"] = "digital" }),
                (1, new Dictionary<string, string> { ["edition"] = "digital" }),
            });

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital", 400m, IsSold: true, ListingIndex: 0, Condition: "NEW"),
            new PricedListing(2, "PS5 Digital", 200m, IsSold: true, ListingIndex: 1, Condition: null),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 0, minComps: 1);

        Assert.That(result.Cells.Count(), Is.EqualTo(2));
    }

    [Test]
    public void Should_exclude_listings_with_ask_price_far_below_cell_median()
    {
        // Rain cover at £30 in a cell with £10,000 median — clearly not the same product
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("size", new[] { "25", "30" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["size"] = "25" }),
                (1, new Dictionary<string, string> { ["size"] = "25" }),
                (2, new Dictionary<string, string> { ["size"] = "25" }),
                (3, new Dictionary<string, string> { ["size"] = "25" }),
            });

        var listings = new[]
        {
            new PricedListing(1, "Birkin 25 Togo", 10000m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "Birkin 25 Epsom", 11000m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "Birkin 25 Swift", 12000m, IsSold: true, ListingIndex: 2),
            new PricedListing(4, "Rain Cover for Birkin 25", 30m, IsSold: false, ListingIndex: 3),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 13.25, minComps: 3);

        // £30 / £11000 = 0.27% — way below 15% threshold, should be excluded
        Assert.That(result.Opportunities.Count(), Is.EqualTo(0),
            "Listing priced at <15% of cell median should be excluded as likely misclassified");
    }

    [Test]
    public void Should_include_listings_with_reasonable_discount()
    {
        // Listing at £7000 in a cell with £10000 median — 70% ratio, legitimate deal
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("size", new[] { "25", "30" }) },
            assignments: new[]
            {
                (0, new Dictionary<string, string> { ["size"] = "25" }),
                (1, new Dictionary<string, string> { ["size"] = "25" }),
                (2, new Dictionary<string, string> { ["size"] = "25" }),
                (3, new Dictionary<string, string> { ["size"] = "25" }),
            });

        var listings = new[]
        {
            new PricedListing(1, "Birkin 25 Sold 1", 9000m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "Birkin 25 Sold 2", 10000m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "Birkin 25 Sold 3", 11000m, IsSold: true, ListingIndex: 2),
            new PricedListing(4, "Birkin 25 Good Deal", 7000m, IsSold: false, ListingIndex: 3),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 13.25, minComps: 3);

        Assert.That(result.Opportunities.Count(), Is.EqualTo(1),
            "Listing at 70% of cell median is a legitimate opportunity");
    }

    // -- Helpers --

    private static TaxonomyResult BuildTaxonomy(
        (string Name, string[] Values)[] axes,
        (int ListingIndex, Dictionary<string, string> Cell)[] assignments)
    {
        var taxAxes = axes.Select(a =>
            new Axis(a.Name, a.Values.Select(v =>
                new AxisValue(v, new[] { new Ngram(v, new[] { v }, 10) })))).ToList();

        var taxAssignments = assignments.Select(a =>
            new CellAssignment(a.ListingIndex, a.Cell, false)).ToList();

        return new TaxonomyResult(taxAxes, taxAssignments, Enumerable.Empty<CellStats>(), 100.0, 0.0);
    }
}
