using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class TaxonomyQueryService_Tests
{
    [Test]
    public void Should_count_values_per_axis_with_no_filters()
    {
        var assignments = new List<ParsedAssignment>
        {
            new(1, new Dictionary<string, string> { ["Axis 0"] = "Digital", ["Axis 1"] = "Console" }),
            new(2, new Dictionary<string, string> { ["Axis 0"] = "Digital", ["Axis 1"] = "Bundle" }),
            new(3, new Dictionary<string, string> { ["Axis 0"] = "Disc", ["Axis 1"] = "Console" }),
        };

        var result = TaxonomyFacets.ComputeFacets(assignments, new Dictionary<string, string>()).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(2));
            var axis0 = result.First(a => a.Name == "Axis 0");
            Assert.That(axis0.Values.First(v => v.Label == "Digital").Count, Is.EqualTo(2));
            Assert.That(axis0.Values.First(v => v.Label == "Disc").Count, Is.EqualTo(1));
            var axis1 = result.First(a => a.Name == "Axis 1");
            Assert.That(axis1.Values.First(v => v.Label == "Console").Count, Is.EqualTo(2));
            Assert.That(axis1.Values.First(v => v.Label == "Bundle").Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void Should_cascade_counts_when_axis_filter_applied()
    {
        var assignments = new List<ParsedAssignment>
        {
            new(1, new Dictionary<string, string> { ["Axis 0"] = "Digital", ["Axis 1"] = "Console" }),
            new(2, new Dictionary<string, string> { ["Axis 0"] = "Digital", ["Axis 1"] = "Bundle" }),
            new(3, new Dictionary<string, string> { ["Axis 0"] = "Disc", ["Axis 1"] = "Console" }),
        };

        var filters = new Dictionary<string, string> { ["Axis 0"] = "Digital" };
        var result = TaxonomyFacets.ComputeFacets(assignments, filters);

        Assert.Multiple(() =>
        {
            // Axis 0 counts should still reflect all listings (so user can see alternatives)
            var axis0 = result.First(a => a.Name == "Axis 0");
            Assert.That(axis0.Values.First(v => v.Label == "Digital").Count, Is.EqualTo(2));
            Assert.That(axis0.Values.First(v => v.Label == "Disc").Count, Is.EqualTo(1));
            // Axis 1 counts should only reflect Digital listings
            var axis1 = result.First(a => a.Name == "Axis 1");
            Assert.That(axis1.Values.First(v => v.Label == "Console").Count, Is.EqualTo(1));
            Assert.That(axis1.Values.First(v => v.Label == "Bundle").Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void Should_show_zero_count_for_values_with_no_matching_listings()
    {
        var assignments = new List<ParsedAssignment>
        {
            new(1, new Dictionary<string, string> { ["Axis 0"] = "Digital", ["Axis 1"] = "Console" }),
            new(2, new Dictionary<string, string> { ["Axis 0"] = "Disc", ["Axis 1"] = "Bundle" }),
        };

        var filters = new Dictionary<string, string> { ["Axis 0"] = "Digital" };
        var result = TaxonomyFacets.ComputeFacets(assignments, filters);

        var axis1 = result.First(a => a.Name == "Axis 1");
        Assert.That(axis1.Values.First(v => v.Label == "Bundle").Count, Is.EqualTo(0));
    }

    [Test]
    public void Should_return_empty_when_no_assignments()
    {
        var result = TaxonomyFacets.ComputeFacets(
            new List<ParsedAssignment>(), new Dictionary<string, string>());

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Should_filter_assignments_by_selected_axes()
    {
        var assignments = new List<ParsedAssignment>
        {
            new(1, new Dictionary<string, string> { ["Axis 0"] = "Digital", ["Axis 1"] = "Console" }),
            new(2, new Dictionary<string, string> { ["Axis 0"] = "Digital", ["Axis 1"] = "Bundle" }),
            new(3, new Dictionary<string, string> { ["Axis 0"] = "Disc", ["Axis 1"] = "Console" }),
        };

        var filters = new Dictionary<string, string> { ["Axis 0"] = "Digital" };
        var filtered = TaxonomyFacets.FilterAssignments(assignments, filters);

        Assert.That(filtered.Select(a => a.ListingId), Is.EquivalentTo(new[] { 1, 2 }));
    }
}
