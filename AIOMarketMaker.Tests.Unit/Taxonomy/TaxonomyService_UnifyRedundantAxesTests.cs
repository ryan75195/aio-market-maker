using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class TaxonomyService_UnifyRedundantAxesTests
{
    private static Dictionary<string, float[]> NoEmbeddings => new();

    // ── Phase 1: Token subsumption ─────────────────────────────────────

    [Test]
    public void Should_prune_values_whose_tokens_are_superset_of_shorter_axis_value()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 30) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            }),
            new("Axis 1", new[]
            {
                new AxisValue("disc edition", new[] { new Ngram("disc edition", new[] { "disc edition" }, 30) }),
                new AxisValue("digital edition", new[] { new Ngram("digital edition", new[] { "digital edition" }, 30) }),
            }),
        };

        var matchSets = BuildMatchSets(new Dictionary<string, HashSet<int>>
        {
            ["disc"] = new() { 0, 1, 2 },
            ["digital"] = new() { 3, 4, 5 },
            ["disc edition"] = new() { 0, 1 },
            ["digital edition"] = new() { 3, 4 },
        });

        var result = TaxonomyService.UnifyRedundantAxes(axes, matchSets, NoEmbeddings);

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Axis 0"));
    }

    [Test]
    public void Should_keep_axes_with_independent_tokens()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 30) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            }),
            new("Axis 1", new[]
            {
                new AxisValue("white", new[] { new Ngram("white", new[] { "white" }, 20) }),
                new AxisValue("black", new[] { new Ngram("black", new[] { "black" }, 20) }),
            }),
        };

        var matchSets = BuildMatchSets(new Dictionary<string, HashSet<int>>
        {
            ["disc"] = new() { 0, 1, 2 },
            ["digital"] = new() { 3, 4, 5 },
            ["white"] = new() { 10, 11, 12 },
            ["black"] = new() { 13, 14, 15 },
        });

        var result = TaxonomyService.UnifyRedundantAxes(axes, matchSets, NoEmbeddings);

        Assert.That(result.Count, Is.EqualTo(2));
    }

    [Test]
    public void Should_handle_chain_of_token_subsumption()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 50) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 50) }),
            }),
            new("Axis 1", new[]
            {
                new AxisValue("slim digital", new[] { new Ngram("slim digital", new[] { "slim digital" }, 30) }),
                new AxisValue("slim disc", new[] { new Ngram("slim disc", new[] { "slim disc" }, 30) }),
            }),
            new("Axis 2", new[]
            {
                new AxisValue("slim digital edition", new[] { new Ngram("slim digital edition", new[] { "slim digital edition" }, 15) }),
                new AxisValue("slim disc edition", new[] { new Ngram("slim disc edition", new[] { "slim disc edition" }, 15) }),
            }),
        };

        var matchSets = BuildMatchSets(new Dictionary<string, HashSet<int>>
        {
            ["disc"] = new() { 0, 1, 2 },
            ["digital"] = new() { 3, 4, 5 },
            ["slim digital"] = new() { 3, 4 },
            ["slim disc"] = new() { 0, 1 },
            ["slim digital edition"] = new() { 3 },
            ["slim disc edition"] = new() { 0 },
        });

        var result = TaxonomyService.UnifyRedundantAxes(axes, matchSets, NoEmbeddings);

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Axis 0"));
    }

    [Test]
    public void Should_return_empty_list_for_empty_input()
    {
        var result = TaxonomyService.UnifyRedundantAxes(
            new List<Axis>(), new Dictionary<string, MatchSet>(), NoEmbeddings);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Should_return_single_axis_unchanged()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 30) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            }),
        };

        var matchSets = BuildMatchSets(new Dictionary<string, HashSet<int>>
        {
            ["disc"] = new() { 0, 1, 2 },
            ["digital"] = new() { 3, 4, 5 },
        });

        var result = TaxonomyService.UnifyRedundantAxes(axes, matchSets, NoEmbeddings);

        Assert.That(result.Count, Is.EqualTo(1));
    }

    // ── Phase 2: Listing overlap merge ─────────────────────────────────

    [Test]
    public void Should_merge_axes_with_identical_listing_coverage()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 30) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            }),
            new("Axis 1", new[]
            {
                new AxisValue("physical", new[] { new Ngram("physical", new[] { "physical" }, 30) }),
                new AxisValue("download", new[] { new Ngram("download", new[] { "download" }, 30) }),
            }),
        };

        var matchSets = BuildMatchSets(new Dictionary<string, HashSet<int>>
        {
            ["disc"] = new() { 0, 1, 2 },
            ["digital"] = new() { 3, 4, 5 },
            ["physical"] = new() { 0, 1, 2 },
            ["download"] = new() { 3, 4, 5 },
        });

        var result = TaxonomyService.UnifyRedundantAxes(axes, matchSets, NoEmbeddings);

        Assert.That(result.Count, Is.EqualTo(1));
    }

    [Test]
    public void Should_not_merge_axes_with_different_listings()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 30) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            }),
            new("Axis 1", new[]
            {
                new AxisValue("white", new[] { new Ngram("white", new[] { "white" }, 30) }),
                new AxisValue("black", new[] { new Ngram("black", new[] { "black" }, 30) }),
            }),
        };

        var matchSets = BuildMatchSets(new Dictionary<string, HashSet<int>>
        {
            ["disc"] = new() { 0, 1, 2, 3, 4 },
            ["digital"] = new() { 5, 6, 7, 8, 9 },
            ["white"] = new() { 15, 16, 17, 18, 19 },
            ["black"] = new() { 20, 21, 22, 23, 24 },
        });

        var result = TaxonomyService.UnifyRedundantAxes(axes, matchSets, NoEmbeddings);

        Assert.That(result.Count, Is.EqualTo(2));
    }

    [Test]
    public void Should_merge_when_smaller_axis_is_contained_within_larger()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 50) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 50) }),
            }),
            new("Axis 1", new[]
            {
                new AxisValue("physical", new[] { new Ngram("physical", new[] { "physical" }, 30) }),
                new AxisValue("download", new[] { new Ngram("download", new[] { "download" }, 30) }),
            }),
        };

        var matchSets = BuildMatchSets(new Dictionary<string, HashSet<int>>
        {
            ["disc"] = new(Enumerable.Range(0, 10)),
            ["digital"] = new(Enumerable.Range(10, 10)),
            ["physical"] = new(Enumerable.Range(0, 6)),
            ["download"] = new(Enumerable.Range(10, 6)),
        });

        var result = TaxonomyService.UnifyRedundantAxes(axes, matchSets, NoEmbeddings);

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Values.Select(v => v.Label), Does.Contain("disc"));
    }

    // ── Phase 2: Chain merge (the bug fix) ─────────────────────────────

    [Test]
    public void Should_merge_chain_of_three_redundant_axes_via_union_find()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 30) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            }),
            new("Axis 1", new[]
            {
                new AxisValue("physical", new[] { new Ngram("physical", new[] { "physical" }, 30) }),
                new AxisValue("download", new[] { new Ngram("download", new[] { "download" }, 30) }),
            }),
            new("Axis 2", new[]
            {
                new AxisValue("tangible", new[] { new Ngram("tangible", new[] { "tangible" }, 30) }),
                new AxisValue("virtual", new[] { new Ngram("virtual", new[] { "virtual" }, 30) }),
            }),
        };

        var matchSets = BuildMatchSets(new Dictionary<string, HashSet<int>>
        {
            ["disc"] = new() { 0, 1, 2 },
            ["digital"] = new() { 3, 4, 5 },
            ["physical"] = new() { 0, 1, 2 },
            ["download"] = new() { 3, 4, 5 },
            ["tangible"] = new() { 0, 1, 2 },
            ["virtual"] = new() { 3, 4, 5 },
        });

        var result = TaxonomyService.UnifyRedundantAxes(axes, matchSets, NoEmbeddings);

        Assert.That(result.Count, Is.EqualTo(1),
            "Union-Find should transitively collapse all three redundant axes");
    }

    // ── Phase 2: Embedding similarity ──────────────────────────────────

    [Test]
    public void Should_merge_axes_with_semantically_similar_values()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("ps5", new[] { new Ngram("ps5", new[] { "ps5" }, 50) }),
                new AxisValue("ps4", new[] { new Ngram("ps4", new[] { "ps4" }, 30) }),
            }),
            new("Axis 1", new[]
            {
                new AxisValue("playstation 5", new[] { new Ngram("playstation 5", new[] { "playstation 5" }, 40) }),
                new AxisValue("playstation 4", new[] { new Ngram("playstation 4", new[] { "playstation 4" }, 25) }),
            }),
        };

        var matchSets = BuildMatchSets(new Dictionary<string, HashSet<int>>
        {
            ["ps5"] = new() { 0, 1, 2, 3, 4 },
            ["ps4"] = new() { 5, 6, 7, 8, 9 },
            ["playstation 5"] = new() { 10, 11, 12, 13, 14 },
            ["playstation 4"] = new() { 15, 16, 17, 18, 19 },
        });

        var embeddings = new Dictionary<string, float[]>
        {
            ["ps5"] = Normalize(new[] { 1f, 0f, 0f, 0f }),
            ["playstation 5"] = Normalize(new[] { 0.95f, 0.05f, 0f, 0f }),
            ["ps4"] = Normalize(new[] { 0f, 0f, 1f, 0f }),
            ["playstation 4"] = Normalize(new[] { 0f, 0.05f, 0.95f, 0f }),
        };

        var result = TaxonomyService.UnifyRedundantAxes(axes, matchSets, embeddings);

        Assert.That(result.Count, Is.EqualTo(1),
            "Semantically similar axes should merge via embedding signal");
    }

    [Test]
    public void Should_not_merge_axes_with_dissimilar_embeddings()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 30) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            }),
            new("Axis 1", new[]
            {
                new AxisValue("white", new[] { new Ngram("white", new[] { "white" }, 30) }),
                new AxisValue("black", new[] { new Ngram("black", new[] { "black" }, 30) }),
            }),
        };

        var matchSets = BuildMatchSets(new Dictionary<string, HashSet<int>>
        {
            ["disc"] = new() { 0, 1, 2, 3, 4 },
            ["digital"] = new() { 5, 6, 7, 8, 9 },
            ["white"] = new() { 15, 16, 17, 18, 19 },
            ["black"] = new() { 20, 21, 22, 23, 24 },
        });

        var embeddings = new Dictionary<string, float[]>
        {
            ["disc"] = new[] { 1f, 0f, 0f, 0f },
            ["digital"] = new[] { 0f, 1f, 0f, 0f },
            ["white"] = new[] { 0f, 0f, 1f, 0f },
            ["black"] = new[] { 0f, 0f, 0f, 1f },
        };

        var result = TaxonomyService.UnifyRedundantAxes(axes, matchSets, embeddings);

        Assert.That(result.Count, Is.EqualTo(2));
    }

    // ── Phase 3: Representative selection ──────────────────────────────

    [Test]
    public void Should_keep_axis_with_shortest_tokens_when_merging()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 50) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 50) }),
            }),
            new("Axis 1", new[]
            {
                new AxisValue("disc edition", new[] { new Ngram("disc edition", new[] { "disc edition" }, 30) }),
                new AxisValue("digital edition", new[] { new Ngram("digital edition", new[] { "digital edition" }, 30) }),
            }),
        };

        var matchSets = BuildMatchSets(new Dictionary<string, HashSet<int>>
        {
            ["disc"] = new() { 0, 1, 2, 3, 4 },
            ["digital"] = new() { 5, 6, 7, 8, 9 },
            ["disc edition"] = new() { 0, 1, 2, 3, 4 },
            ["digital edition"] = new() { 5, 6, 7, 8, 9 },
        });

        var result = TaxonomyService.UnifyRedundantAxes(axes, matchSets, NoEmbeddings);

        Assert.That(result.Count, Is.EqualTo(1));
        var labels = result[0].Values.Select(v => v.Label).ToList();
        Assert.That(labels, Does.Contain("disc"), "Should keep the shorter-token axis");
        Assert.That(labels, Does.Contain("digital"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static Dictionary<string, MatchSet> BuildMatchSets(
        Dictionary<string, HashSet<int>> data)
    {
        return data.ToDictionary(
            kvp => kvp.Key,
            kvp => new MatchSet(
                new Ngram(kvp.Key, new[] { kvp.Key }, kvp.Value.Count),
                kvp.Value));
    }

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
