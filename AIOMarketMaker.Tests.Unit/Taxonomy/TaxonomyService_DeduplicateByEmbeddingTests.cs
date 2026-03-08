using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class TaxonomyService_DeduplicateByEmbeddingTests
{
    [Test]
    public void Should_merge_near_synonym_values_with_high_embedding_similarity()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("box", new[] { new Ngram("box", new[] { "box" }, 30) }),
                new AxisValue("boxed", new[] { new Ngram("boxed", new[] { "boxed" }, 20) }),
                new AxisValue("sealed", new[] { new Ngram("sealed", new[] { "sealed" }, 25) }),
            }),
        };

        var embeddings = new Dictionary<string, float[]>
        {
            ["box"] = Normalize(new[] { 1f, 0f, 0f }),
            ["boxed"] = Normalize(new[] { 0.98f, 0.02f, 0f }),
            ["sealed"] = Normalize(new[] { 0f, 0f, 1f }),
        };

        var result = TaxonomyService.DeduplicateByEmbedding(axes, embeddings);

        Assert.That(result.Count, Is.EqualTo(1));
        var labels = result[0].Values.Select(v => v.Label).ToList();
        Assert.That(labels, Has.Count.EqualTo(2));
        Assert.That(labels, Does.Contain("box"), "Should keep the shorter label");
        Assert.That(labels, Does.Contain("sealed"));
    }

    [Test]
    public void Should_not_merge_values_with_low_embedding_similarity()
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
            ["disc"] = Normalize(new[] { 1f, 0f, 0f }),
            ["digital"] = Normalize(new[] { 0f, 1f, 0f }),
        };

        var result = TaxonomyService.DeduplicateByEmbedding(axes, embeddings);

        Assert.That(result[0].Values.Count(), Is.EqualTo(2));
    }

    [Test]
    public void Should_merge_spelling_variants()
    {
        // "disk" and "disc" should have very high embedding similarity
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("disk", new[] { new Ngram("disk", new[] { "disk" }, 30) }),
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 25) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            }),
        };

        var embeddings = new Dictionary<string, float[]>
        {
            ["disk"] = Normalize(new[] { 1f, 0f, 0f }),
            ["disc"] = Normalize(new[] { 0.99f, 0.01f, 0f }),
            ["digital"] = Normalize(new[] { 0f, 1f, 0f }),
        };

        var result = TaxonomyService.DeduplicateByEmbedding(axes, embeddings);

        var labels = result[0].Values.Select(v => v.Label).ToList();
        Assert.That(labels, Has.Count.EqualTo(2));
        Assert.That(labels, Does.Contain("digital"));
    }

    [Test]
    public void Should_drop_axis_when_dedup_leaves_fewer_than_two_values()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("box", new[] { new Ngram("box", new[] { "box" }, 30) }),
                new AxisValue("boxed", new[] { new Ngram("boxed", new[] { "boxed" }, 20) }),
            }),
        };

        var embeddings = new Dictionary<string, float[]>
        {
            ["box"] = Normalize(new[] { 1f, 0f }),
            ["boxed"] = Normalize(new[] { 0.99f, 0.01f }),
        };

        var result = TaxonomyService.DeduplicateByEmbedding(axes, embeddings);

        Assert.That(result.Count, Is.EqualTo(0));
    }

    [Test]
    public void Should_return_axes_unchanged_when_no_embeddings_and_no_stem_variants()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 30) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 20) }),
            }),
        };

        var result = TaxonomyService.DeduplicateByEmbedding(axes, new Dictionary<string, float[]>());

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Values.Count(), Is.EqualTo(2));
    }

    [Test]
    public void Should_keep_shorter_label_when_merging()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("excellent condition", new[] { new Ngram("excellent condition", new[] { "excellent condition" }, 20) }),
                new AxisValue("excellent", new[] { new Ngram("excellent", new[] { "excellent" }, 30) }),
                new AxisValue("good", new[] { new Ngram("good", new[] { "good" }, 25) }),
            }),
        };

        var embeddings = new Dictionary<string, float[]>
        {
            ["excellent condition"] = Normalize(new[] { 1f, 0f, 0f }),
            ["excellent"] = Normalize(new[] { 0.97f, 0.03f, 0f }),
            ["good"] = Normalize(new[] { 0f, 1f, 0f }),
        };

        var result = TaxonomyService.DeduplicateByEmbedding(axes, embeddings);

        var labels = result[0].Values.Select(v => v.Label).ToList();
        Assert.That(labels, Does.Contain("excellent"), "Should keep shorter label");
        Assert.That(labels, Does.Not.Contain("excellent condition"));
    }

    // ── Stem variant tests ────────────────────────────────────────────

    [TestCase("box", "boxed")]
    [TestCase("hand", "handed")]
    [TestCase("case", "cased")]
    [TestCase("use", "used")]
    [TestCase("robot", "robotic")]
    [TestCase("steam", "steamer")]
    public void Should_merge_morphological_variants_without_embeddings(string shorter, string longer)
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue(shorter, new[] { new Ngram(shorter, new[] { shorter }, 30) }),
                new AxisValue(longer, new[] { new Ngram(longer, new[] { longer }, 30) }),
                new AxisValue("sealed", new[] { new Ngram("sealed", new[] { "sealed" }, 25) }),
            }),
        };

        var result = TaxonomyService.DeduplicateByEmbedding(axes, new Dictionary<string, float[]>());

        var labels = result[0].Values.Select(v => v.Label).ToList();
        Assert.That(labels, Has.Count.EqualTo(2));
        Assert.That(labels, Does.Contain(shorter), "Should keep the shorter stem");
        Assert.That(labels, Does.Not.Contain(longer));
    }

    [TestCase("disk", "disc")]
    public void Should_merge_spelling_variants_by_edit_distance(string a, string b)
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue(a, new[] { new Ngram(a, new[] { a }, 30) }),
                new AxisValue(b, new[] { new Ngram(b, new[] { b }, 25) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            }),
        };

        var result = TaxonomyService.DeduplicateByEmbedding(axes, new Dictionary<string, float[]>());

        var labels = result[0].Values.Select(v => v.Label).ToList();
        Assert.That(labels, Has.Count.EqualTo(2));
        Assert.That(labels, Does.Contain("digital"));
    }

    [Test]
    public void Should_not_merge_unrelated_short_words()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("box", new[] { new Ngram("box", new[] { "box" }, 30) }),
                new AxisValue("bin", new[] { new Ngram("bin", new[] { "bin" }, 30) }),
            }),
        };

        var result = TaxonomyService.DeduplicateByEmbedding(axes, new Dictionary<string, float[]>());

        Assert.That(result[0].Values.Count(), Is.EqualTo(2),
            "box and bin are different words, not stem variants");
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
