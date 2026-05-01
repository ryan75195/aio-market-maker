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

    [Test]
    public void Should_split_high_variance_cell_and_use_subcell_medians()
    {
        // Cell has dust bags (£20-30) and real bags (£1800-2200) — same taxonomy cell.
        // Without splitting: blended median ~£915, so Picotin at £1200 has no profit.
        // With splitting: real-bag sub-cell median = £2000, Picotin at £1200 is a real opportunity.
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("bundle", new[] { "bag", "charm" }) },
            assignments: Enumerable.Range(0, 11).Select(i =>
                (i, new Dictionary<string, string> { ["bundle"] = "bag" })).ToArray());

        var listings = new[]
        {
            // 5 sold dust bags at low prices
            new PricedListing(1, "Hermes Dust Bag Cover", 20m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "Hermes Dust Bag Pouch", 25m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "Hermes Dust Bag Storage", 22m, IsSold: true, ListingIndex: 2),
            new PricedListing(4, "Hermes Dust Bag Sleeper", 28m, IsSold: true, ListingIndex: 3),
            new PricedListing(5, "Hermes Dust Bag Cover Large", 30m, IsSold: true, ListingIndex: 4),
            // 5 sold real bags at high prices
            new PricedListing(6, "Hermes Evelyne Shoulder Bag", 1800m, IsSold: true, ListingIndex: 5),
            new PricedListing(7, "Hermes Garden Party Tote Bag", 1900m, IsSold: true, ListingIndex: 6),
            new PricedListing(8, "Hermes Picotin Bag Leather", 2000m, IsSold: true, ListingIndex: 7),
            new PricedListing(9, "Hermes Bolide Bag Travel", 2100m, IsSold: true, ListingIndex: 8),
            new PricedListing(10, "Hermes Kelly Bag Classic", 2200m, IsSold: true, ListingIndex: 9),
            // 1 active real bag — should be opportunity vs real-bag median (£2000)
            new PricedListing(11, "Hermes Picotin Bag Deal", 1200m, IsSold: false, ListingIndex: 10),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 13.25, minComps: 3);

        // Key assertion: Picotin at £1200 becomes an opportunity against real-bag median £2000
        // Without splitting, blended median ~£915 means £1200 > median → no opportunity
        var opp = result.Opportunities.FirstOrDefault(o => o.Title.Contains("Picotin"));
        Assert.That(opp, Is.Not.Null, "Picotin should be an opportunity after cell splitting");
        Assert.That(opp!.MedianSoldPrice, Is.EqualTo(2000m),
            "Opportunity should use sub-cell median (real bags only), not blended median");
    }

    [Test]
    public void Should_preserve_original_cell_key_in_split_subcells()
    {
        // After splitting, both sub-cells should contain the original axis values
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("bundle", new[] { "bag", "charm" }) },
            assignments: Enumerable.Range(0, 10).Select(i =>
                (i, new Dictionary<string, string> { ["bundle"] = "bag" })).ToArray());

        var listings = new[]
        {
            new PricedListing(1, "Hermes Dust Bag Cover", 20m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "Hermes Dust Bag Pouch", 25m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "Hermes Dust Bag Storage", 22m, IsSold: true, ListingIndex: 2),
            new PricedListing(4, "Hermes Dust Bag Sleeper", 28m, IsSold: true, ListingIndex: 3),
            new PricedListing(5, "Hermes Dust Bag Large", 30m, IsSold: true, ListingIndex: 4),
            new PricedListing(6, "Hermes Evelyne Shoulder Bag", 1800m, IsSold: true, ListingIndex: 5),
            new PricedListing(7, "Hermes Garden Party Tote Bag", 1900m, IsSold: true, ListingIndex: 6),
            new PricedListing(8, "Hermes Picotin Bag Leather", 2000m, IsSold: true, ListingIndex: 7),
            new PricedListing(9, "Hermes Bolide Bag Travel", 2100m, IsSold: true, ListingIndex: 8),
            new PricedListing(10, "Hermes Kelly Bag Classic", 2200m, IsSold: true, ListingIndex: 9),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 0, minComps: 3);
        var cellKeys = result.Cells.Select(c => c.CellKey).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(result.Cells.Count(), Is.EqualTo(2), "Should produce 2 sub-cells");
            Assert.That(cellKeys, Has.All.Contain("bundle=bag"),
                "Both sub-cells should preserve the original 'bundle=bag' axis");
        });
    }

    [Test]
    public void Should_not_split_cell_when_price_variance_is_low()
    {
        // All sold prices tightly clustered (£180-220) — no reason to split
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("edition", new[] { "digital", "disc" }) },
            assignments: Enumerable.Range(0, 9).Select(i =>
                (i, new Dictionary<string, string> { ["edition"] = "digital" })).ToArray());

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital Alpha", 180m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "PS5 Digital Beta", 190m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "PS5 Digital Gamma", 195m, IsSold: true, ListingIndex: 2),
            new PricedListing(4, "PS5 Digital Delta", 200m, IsSold: true, ListingIndex: 3),
            new PricedListing(5, "PS5 Digital Epsilon", 205m, IsSold: true, ListingIndex: 4),
            new PricedListing(6, "PS5 Digital Zeta", 210m, IsSold: true, ListingIndex: 5),
            new PricedListing(7, "PS5 Digital Eta", 215m, IsSold: true, ListingIndex: 6),
            new PricedListing(8, "PS5 Digital Theta", 220m, IsSold: true, ListingIndex: 7),
            new PricedListing(9, "PS5 Digital Deal", 150m, IsSold: false, ListingIndex: 8),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 0, minComps: 3);

        // Should produce exactly 1 cell (no splitting), 1 opportunity
        Assert.Multiple(() =>
        {
            Assert.That(result.Cells.Count(), Is.EqualTo(1), "Low-variance cell should not be split");
            Assert.That(result.Opportunities.Count(), Is.EqualTo(1));
            var opp = result.Opportunities.Single();
            Assert.That(opp.MedianSoldPrice, Is.EqualTo(202.5m),
                "Median of [180,190,195,200,205,210,215,220] = (200+205)/2 = 202.5");
        });
    }

    [Test]
    public void Should_not_split_cell_with_too_few_sold_listings()
    {
        // High variance but only 4 sold — below MinSoldForSplit threshold
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("bundle", new[] { "bag", "charm" }) },
            assignments: Enumerable.Range(0, 5).Select(i =>
                (i, new Dictionary<string, string> { ["bundle"] = "bag" })).ToArray());

        var listings = new[]
        {
            new PricedListing(1, "Dust Bag Small", 20m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "Dust Bag Large", 25m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "Real Bag Evelyne", 1800m, IsSold: true, ListingIndex: 2),
            new PricedListing(4, "Real Bag Kelly", 2000m, IsSold: true, ListingIndex: 3),
            new PricedListing(5, "Real Bag Deal", 1200m, IsSold: false, ListingIndex: 4),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 0, minComps: 3);

        // Only 4 sold — should NOT split, compute against blended median
        Assert.That(result.Cells.Count(), Is.EqualTo(1),
            "Cell with fewer than 6 sold should not be split");
    }

    [Test]
    public void Should_not_generate_opportunities_when_cell_has_high_price_variance()
    {
        // Contaminated cell mixing cheap items (£20-30) with expensive items (£200-250).
        // All titles share identical tokens so the token-based split can't distinguish them.
        // CV = std/mean ≈ 91/126 ≈ 0.72 — well above 0.5 threshold.
        // Without gate: active at £50 looks profitable vs £120 blended median.
        // With gate: high-CV cell should produce no opportunities.
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("color", new[] { "blue", "black" }) },
            assignments: Enumerable.Range(0, 10).Select(i =>
                (i, new Dictionary<string, string> { ["color"] = "blue" })).ToArray());

        var listings = new[]
        {
            // Cheap items — identical title tokens
            new PricedListing(1, "Blue Widget Premium", 20m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "Blue Widget Premium", 25m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "Blue Widget Premium", 30m, IsSold: true, ListingIndex: 2),
            // Expensive items — identical title tokens
            new PricedListing(4, "Blue Widget Premium", 200m, IsSold: true, ListingIndex: 3),
            new PricedListing(5, "Blue Widget Premium", 210m, IsSold: true, ListingIndex: 4),
            new PricedListing(6, "Blue Widget Premium", 220m, IsSold: true, ListingIndex: 5),
            new PricedListing(7, "Blue Widget Premium", 230m, IsSold: true, ListingIndex: 6),
            new PricedListing(8, "Blue Widget Premium", 240m, IsSold: true, ListingIndex: 7),
            new PricedListing(9, "Blue Widget Premium", 250m, IsSold: true, ListingIndex: 8),
            // Active listing — would be false opportunity against blended median
            new PricedListing(10, "Blue Widget Premium", 50m, IsSold: false, ListingIndex: 9),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 13.25, minComps: 3);

        Assert.That(result.Opportunities, Is.Empty,
            "Contaminated cell with high price variance should not generate opportunities");
    }

    [Test]
    public void Should_still_generate_opportunities_when_cell_has_low_price_variance()
    {
        // Samsung 990 Pro SSD — tight price clustering. CV well below 0.5.
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("storage", new[] { "1tb", "4tb" }) },
            assignments: Enumerable.Range(0, 8).Select(i =>
                (i, new Dictionary<string, string> { ["storage"] = "1tb" })).ToArray());

        var listings = new[]
        {
            new PricedListing(1, "Samsung 990 Pro 1TB NVMe", 95m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "Samsung 990 Pro 1TB PCIe", 100m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "Samsung 990 Pro 1TB Drive", 105m, IsSold: true, ListingIndex: 2),
            new PricedListing(4, "Samsung 990 Pro 1TB SSD", 110m, IsSold: true, ListingIndex: 3),
            new PricedListing(5, "Samsung 990 Pro 1TB New", 115m, IsSold: true, ListingIndex: 4),
            new PricedListing(6, "Samsung 990 Pro 1TB Box", 120m, IsSold: true, ListingIndex: 5),
            new PricedListing(7, "Samsung 990 Pro 1TB Sealed", 125m, IsSold: true, ListingIndex: 6),
            // Active listing — should still be opportunity
            new PricedListing(8, "Samsung 990 Pro 1TB Deal", 70m, IsSold: false, ListingIndex: 7),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 13.25, minComps: 3);

        Assert.That(result.Opportunities.Count(), Is.EqualTo(1),
            "Low-variance cell should still generate opportunities");
    }

    [Test]
    public void Should_expose_coefficient_of_variation_on_cell_pricing()
    {
        var taxonomy = BuildTaxonomy(
            axes: new[] { ("edition", new[] { "digital", "disc" }) },
            assignments: Enumerable.Range(0, 3).Select(i =>
                (i, new Dictionary<string, string> { ["edition"] = "digital" })).ToArray());

        var listings = new[]
        {
            new PricedListing(1, "PS5 Digital", 100m, IsSold: true, ListingIndex: 0),
            new PricedListing(2, "PS5 Digital", 200m, IsSold: true, ListingIndex: 1),
            new PricedListing(3, "PS5 Digital", 300m, IsSold: true, ListingIndex: 2),
        };

        var result = _service.Compute(taxonomy, listings, feePercent: 0, minComps: 1);
        var cell = result.Cells.Single();

        // Mean = 200, Std = sqrt(((100-200)^2 + (200-200)^2 + (300-200)^2) / 3) = sqrt(6666.67) ≈ 81.65
        // CV = 81.65 / 200 ≈ 0.408
        Assert.That(cell.CoefficientOfVariation, Is.Not.Null);
        Assert.That(cell.CoefficientOfVariation!.Value, Is.EqualTo(0.408).Within(0.01));
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
