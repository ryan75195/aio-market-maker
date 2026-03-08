using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class ApplyRefinementTests
{
    private static Axis MakeAxis(string name, params string[] valueLabels)
    {
        var values = valueLabels.Select(label =>
            new AxisValue(label, new[] { new Ngram(label, new[] { label }, 10) })).ToList();
        return new Axis(name, values);
    }

    private static TaxonomyRefinement EmptyRefinement()
    {
        return new TaxonomyRefinement(
            Enumerable.Empty<RefinedAxis>(),
            Enumerable.Empty<AxisMerge>(),
            Enumerable.Empty<string>());
    }

    [Test]
    public void Should_rename_axis()
    {
        var axes = new List<Axis> { MakeAxis("Axis 0", "standard", "deluxe") };
        var refinement = new TaxonomyRefinement(
            new[]
            {
                new RefinedAxis("Axis 0", "Edition", 1,
                    Enumerable.Empty<string>(), Enumerable.Empty<string>())
            },
            Enumerable.Empty<AxisMerge>(),
            Enumerable.Empty<string>());

        var result = TaxonomyService.ApplyRefinement(axes, refinement);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Edition"));
    }

    [Test]
    public void Should_drop_axis()
    {
        var axes = new List<Axis>
        {
            MakeAxis("Axis 0", "standard", "deluxe"),
            MakeAxis("Axis 7", "noise", "junk"),
        };
        var refinement = new TaxonomyRefinement(
            Enumerable.Empty<RefinedAxis>(),
            Enumerable.Empty<AxisMerge>(),
            new[] { "Axis 7" });

        var result = TaxonomyService.ApplyRefinement(axes, refinement);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Axis 0"));
    }

    [Test]
    public void Should_remove_values_from_axis()
    {
        var axes = new List<Axis>
        {
            MakeAxis("Axis 0", "standard", "deluxe", "portal"),
        };
        var refinement = new TaxonomyRefinement(
            new[]
            {
                new RefinedAxis("Axis 0", "Axis 0", 1,
                    new[] { "portal" }, Enumerable.Empty<string>())
            },
            Enumerable.Empty<AxisMerge>(),
            Enumerable.Empty<string>());

        var result = TaxonomyService.ApplyRefinement(axes, refinement);

        Assert.That(result, Has.Count.EqualTo(1));
        var labels = result[0].Values.Select(v => v.Label).ToList();
        Assert.That(labels, Does.Contain("standard"));
        Assert.That(labels, Does.Contain("deluxe"));
        Assert.That(labels, Does.Not.Contain("portal"));
    }

    [Test]
    public void Should_add_values_to_axis()
    {
        var axes = new List<Axis>
        {
            MakeAxis("Axis 0", "standard", "deluxe"),
        };
        var refinement = new TaxonomyRefinement(
            new[]
            {
                new RefinedAxis("Axis 0", "Axis 0", 1,
                    Enumerable.Empty<string>(), new[] { "slim" })
            },
            Enumerable.Empty<AxisMerge>(),
            Enumerable.Empty<string>());

        var result = TaxonomyService.ApplyRefinement(axes, refinement);

        Assert.That(result, Has.Count.EqualTo(1));
        var labels = result[0].Values.Select(v => v.Label).ToList();
        Assert.That(labels, Does.Contain("slim"));
        Assert.That(labels, Has.Count.EqualTo(3));

        var addedValue = result[0].Values.First(v => v.Label == "slim");
        var ngram = addedValue.Ngrams.First();
        Assert.That(ngram.Canonical, Is.EqualTo("slim"));
        Assert.That(ngram.Frequency, Is.EqualTo(0));
    }

    [Test]
    public void Should_merge_axes()
    {
        var axes = new List<Axis>
        {
            MakeAxis("Axis 0", "standard", "deluxe"),
            MakeAxis("Axis 3", "limited", "collectors"),
        };
        var refinement = new TaxonomyRefinement(
            Enumerable.Empty<RefinedAxis>(),
            new[] { new AxisMerge("Axis 0", "Axis 3") },
            Enumerable.Empty<string>());

        var result = TaxonomyService.ApplyRefinement(axes, refinement);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Axis 0"));
        var labels = result[0].Values.Select(v => v.Label).ToList();
        Assert.That(labels, Is.EquivalentTo(new[] { "standard", "deluxe", "limited", "collectors" }));
    }

    [Test]
    public void Should_set_importance_on_axis()
    {
        var axes = new List<Axis> { MakeAxis("Axis 0", "standard", "deluxe") };
        var refinement = new TaxonomyRefinement(
            new[]
            {
                new RefinedAxis("Axis 0", "Edition", 5,
                    Enumerable.Empty<string>(), Enumerable.Empty<string>())
            },
            Enumerable.Empty<AxisMerge>(),
            Enumerable.Empty<string>());

        var result = TaxonomyService.ApplyRefinement(axes, refinement);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Importance, Is.EqualTo(5));
    }

    [Test]
    public void Should_return_axes_unchanged_when_refinement_is_empty()
    {
        var axes = new List<Axis>
        {
            MakeAxis("Axis 0", "standard", "deluxe"),
            MakeAxis("Axis 1", "new", "used"),
        };

        var result = TaxonomyService.ApplyRefinement(axes, EmptyRefinement());

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0].Name, Is.EqualTo("Axis 0"));
        Assert.That(result[1].Name, Is.EqualTo("Axis 1"));
        Assert.That(result[0].Values.Select(v => v.Label),
            Is.EquivalentTo(new[] { "standard", "deluxe" }));
        Assert.That(result[1].Values.Select(v => v.Label),
            Is.EquivalentTo(new[] { "new", "used" }));
    }
}
