using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class TaxonomyService_PruneSemanticOutliersTests
{
    [Test]
    public void Should_prune_value_with_low_similarity_to_other_values_in_axis()
    {
        // "condition" and "bundle" are similar (both eBay listing concepts)
        // "cfi" is an outlier (model number, unrelated)
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("condition", new[] { new Ngram("condition", new[] { "condition" }, 30) }),
                new AxisValue("bundle", new[] { new Ngram("bundle", new[] { "bundle" }, 30) }),
                new AxisValue("cfi", new[] { new Ngram("cfi", new[] { "cfi" }, 30) }),
            }),
        };

        var embeddings = new Dictionary<string, float[]>
        {
            ["condition"] = Normalize(new[] { 1f, 0.8f, 0f }),
            ["bundle"] = Normalize(new[] { 0.8f, 1f, 0f }),
            ["cfi"] = Normalize(new[] { 0f, 0f, 1f }),
        };

        var result = TaxonomyService.PruneSemanticOutliers(axes, embeddings);

        Assert.That(result.Count, Is.EqualTo(1));
        var labels = result[0].Values.Select(v => v.Label).ToList();
        Assert.That(labels, Does.Contain("condition"));
        Assert.That(labels, Does.Contain("bundle"));
        Assert.That(labels, Does.Not.Contain("cfi"));
    }

    [Test]
    public void Should_keep_all_values_when_semantically_coherent()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("new", new[] { new Ngram("new", new[] { "new" }, 30) }),
                new AxisValue("used", new[] { new Ngram("used", new[] { "used" }, 30) }),
                new AxisValue("excellent", new[] { new Ngram("excellent", new[] { "excellent" }, 30) }),
            }),
        };

        // All condition words — similar embeddings
        var embeddings = new Dictionary<string, float[]>
        {
            ["new"] = Normalize(new[] { 1f, 0.9f, 0.8f }),
            ["used"] = Normalize(new[] { 0.9f, 1f, 0.85f }),
            ["excellent"] = Normalize(new[] { 0.8f, 0.85f, 1f }),
        };

        var result = TaxonomyService.PruneSemanticOutliers(axes, embeddings);

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Values.Count(), Is.EqualTo(3));
    }

    [Test]
    public void Should_return_axes_unchanged_when_no_embeddings_available()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("condition", new[] { new Ngram("condition", new[] { "condition" }, 30) }),
                new AxisValue("cfi", new[] { new Ngram("cfi", new[] { "cfi" }, 30) }),
                new AxisValue("bundle", new[] { new Ngram("bundle", new[] { "bundle" }, 30) }),
            }),
        };

        var result = TaxonomyService.PruneSemanticOutliers(axes, new Dictionary<string, float[]>());

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Values.Count(), Is.EqualTo(3));
    }

    [Test]
    public void Should_drop_axis_when_pruning_leaves_fewer_than_two_values()
    {
        // Two values that are semantically unrelated — both get flagged,
        // axis drops below MinAxisValues
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("16", new[] { new Ngram("16", new[] { "16" }, 30) }),
                new AxisValue("excellent", new[] { new Ngram("excellent", new[] { "excellent" }, 30) }),
            }),
        };

        var embeddings = new Dictionary<string, float[]>
        {
            ["16"] = Normalize(new[] { 1f, 0f, 0f }),
            ["excellent"] = Normalize(new[] { 0f, 0f, 1f }),
        };

        var result = TaxonomyService.PruneSemanticOutliers(axes, embeddings);

        Assert.That(result.Count, Is.EqualTo(0),
            "Axis with only dissimilar values should be dropped entirely");
    }

    [Test]
    public void Should_only_prune_outliers_and_keep_coherent_majority()
    {
        // 4 values: 3 colors + 1 outlier number
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("white", new[] { new Ngram("white", new[] { "white" }, 30) }),
                new AxisValue("black", new[] { new Ngram("black", new[] { "black" }, 30) }),
                new AxisValue("blue", new[] { new Ngram("blue", new[] { "blue" }, 30) }),
                new AxisValue("128", new[] { new Ngram("128", new[] { "128" }, 30) }),
            }),
        };

        var embeddings = new Dictionary<string, float[]>
        {
            ["white"] = Normalize(new[] { 1f, 0.9f, 0.85f, 0f }),
            ["black"] = Normalize(new[] { 0.9f, 1f, 0.9f, 0f }),
            ["blue"] = Normalize(new[] { 0.85f, 0.9f, 1f, 0f }),
            ["128"] = Normalize(new[] { 0f, 0f, 0f, 1f }),
        };

        var result = TaxonomyService.PruneSemanticOutliers(axes, embeddings);

        Assert.That(result.Count, Is.EqualTo(1));
        var labels = result[0].Values.Select(v => v.Label).ToList();
        Assert.That(labels, Has.Count.EqualTo(3));
        Assert.That(labels, Does.Not.Contain("128"));
    }

    [Test]
    public void Should_not_prune_from_axes_with_only_two_similar_values()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 30) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            }),
        };

        var embeddings = new Dictionary<string, float[]>
        {
            ["disc"] = Normalize(new[] { 1f, 0.7f }),
            ["digital"] = Normalize(new[] { 0.7f, 1f }),
        };

        var result = TaxonomyService.PruneSemanticOutliers(axes, embeddings);

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Values.Count(), Is.EqualTo(2));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static float[] Normalize(float[] vec)
    {
        var mag = MathF.Sqrt(vec.Sum(v => v * v));
        if (mag == 0)
        {
            return vec;
        }
        return vec.Select(v => v / mag).ToArray();
    }
}
