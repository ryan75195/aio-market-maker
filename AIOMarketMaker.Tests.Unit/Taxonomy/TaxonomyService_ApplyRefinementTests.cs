using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Services.Taxonomy;
using Microsoft.Extensions.Logging;
using Moq;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class TaxonomyService_ApplyRefinementTests
{
    // Synthetic Cartier Love Bracelet titles designed to produce a "baby" vs "small"
    // sub-product axis. "love" is part of the search term and gets excluded from
    // tokenization, so "baby" appears as a standalone unigram.
    //
    // Real data: "baby" freq=105/1612 (6.5%), "small" freq=202/1612 (12.5%).
    // Both are above the 3% significance threshold and mutually exclusive.
    private static readonly string[] CartierTitles = BuildCartierTitles();

    private static string[] BuildCartierTitles()
    {
        var titles = new List<string>();

        // Baby Love — distinct sub-product (~$1,500)
        titles.AddRange(Enumerable.Repeat("Cartier Baby Love Bracelet 18K Yellow Gold Size 16", 25));
        titles.AddRange(Enumerable.Repeat("Cartier Baby Love Bracelet 18K White Gold Size 17", 20));
        titles.AddRange(Enumerable.Repeat("Cartier Baby Love Bracelet 18K Rose Gold", 15));
        titles.AddRange(Enumerable.Repeat("Cartier Baby Love Bracelet Pink Gold", 10));
        titles.AddRange(Enumerable.Repeat("Authentic Cartier Baby Love 18K Yellow Gold Bracelet", 10));

        // Small — another sub-product (varied phrasing, "small" not always next to "model")
        titles.AddRange(Enumerable.Repeat("Cartier Love Bracelet Small 18K Yellow Gold Size 16", 30));
        titles.AddRange(Enumerable.Repeat("Cartier Love Small Bracelet White Gold Size 17", 25));
        titles.AddRange(Enumerable.Repeat("Cartier Love Bracelet Small Rose Gold", 20));
        titles.AddRange(Enumerable.Repeat("Cartier Small Love Bracelet 18K Pink Gold", 15));

        // Classic (no baby/small) — the "standard" product (~$5,000)
        titles.AddRange(Enumerable.Repeat("Cartier Love Bracelet 18K Yellow Gold Size 17", 40));
        titles.AddRange(Enumerable.Repeat("Cartier Love Bracelet 18K White Gold Size 16", 35));
        titles.AddRange(Enumerable.Repeat("Cartier Love Bracelet Rose Gold Size 19", 25));
        titles.AddRange(Enumerable.Repeat("Cartier Love Bracelet 18K Yellow Gold Diamond Size 17", 20));
        titles.AddRange(Enumerable.Repeat("Cartier Love Bracelet 18K White Gold 4 Diamond", 15));

        // Color variety to ensure color tokens are distinct from model tokens
        titles.AddRange(Enumerable.Repeat("Cartier Love Bracelet 18K Yellow Gold Size 16 Box Cert", 15));
        titles.AddRange(Enumerable.Repeat("Cartier Love Bracelet White Gold Pre-Owned Size 17", 10));
        titles.AddRange(Enumerable.Repeat("Cartier Love Bracelet 18K Rose Gold Size 18", 10));

        return titles.ToArray();
    }

    private TaxonomyService _serviceWithoutRefiner;

    [SetUp]
    public void SetUp()
    {
        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding
            .Setup(e => e.GetEmbeddings(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<EmbeddingModel>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _, EmbeddingModel __) =>
                texts.Select(EmbeddingForText).ToArray());

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var analyzer = new MutualExclusivityAnalyzer();
        var detector = new LouvainCommunityDetector();

        _serviceWithoutRefiner = new TaxonomyService(
            extractor, analyzer, detector, mockEmbedding.Object,
            new Mock<ILogger<TaxonomyService>>().Object);
    }

    [Test]
    public async Task Should_preserve_sub_product_token_through_statistical_pipeline()
    {
        // "baby" should survive extraction, significance, exclusivity, graph edges,
        // Louvain, and all 7 PostProcess steps — based on our real-data trace.
        var result = await _serviceWithoutRefiner.Generate(
            CartierTitles, "Cartier Love Bracelet");

        var allValues = result.Axes
            .SelectMany(a => a.Values.Select(v => v.Label))
            .ToList();

        Assert.That(allValues, Has.Some.EqualTo("baby"),
            "The statistical pipeline should preserve 'baby' as an axis value. " +
            "It has freq=80/340 (23%) which is well above the 3% significance threshold, " +
            "and is mutually exclusive with 'small'.");
    }

    [Test]
    public async Task Should_preserve_small_model_token_through_statistical_pipeline()
    {
        var result = await _serviceWithoutRefiner.Generate(
            CartierTitles, "Cartier Love Bracelet");

        var allValues = result.Axes
            .SelectMany(a => a.Values.Select(v => v.Label))
            .ToList();

        Assert.That(allValues, Has.Some.EqualTo("small"),
            "'small' should survive the statistical pipeline as a distinct sub-product value.");
    }

    [Test]
    public async Task Should_place_baby_and_small_on_same_axis()
    {
        var result = await _serviceWithoutRefiner.Generate(
            CartierTitles, "Cartier Love Bracelet");

        var axisWithBaby = result.Axes
            .FirstOrDefault(a => a.Values.Any(v => v.Label == "baby"));
        var axisWithSmall = result.Axes
            .FirstOrDefault(a => a.Values.Any(v => v.Label == "small"));

        Assert.Multiple(() =>
        {
            Assert.That(axisWithBaby, Is.Not.Null, "'baby' should be on an axis");
            Assert.That(axisWithSmall, Is.Not.Null, "'small' should be on an axis");
        });

        if (axisWithBaby != null && axisWithSmall != null)
        {
            Assert.That(axisWithBaby.Name, Is.EqualTo(axisWithSmall.Name),
                "'baby' and 'small' represent the same concept (model variant) " +
                "and should be on the same axis.");
        }
    }

    // --- ApplyRefinement tests ---

    [Test]
    public void Should_apply_basic_refinement_correctly()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new List<AxisValue>
            {
                MakeValue("yellow"),
                MakeValue("white"),
                MakeValue("pink"),
            }),
            new("Axis 1", new List<AxisValue>
            {
                MakeValue("baby"),
                MakeValue("small"),
            }),
        };

        var refinement = new TaxonomyRefinement(
            new[]
            {
                new RefinedAxis("Axis 0", "color", 4,
                    Enumerable.Empty<string>(), Enumerable.Empty<string>()),
                new RefinedAxis("Axis 1", "model", 3,
                    Enumerable.Empty<string>(), new[] { "love cuff" }),
            },
            Enumerable.Empty<AxisMerge>(),
            Enumerable.Empty<string>());

        var result = TaxonomyService.ApplyRefinement(axes, refinement);

        var modelAxis = result.First(a => a.Name == "model");
        var labels = modelAxis.Values.Select(v => v.Label).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(labels, Has.Member("baby"));
            Assert.That(labels, Has.Member("small"));
            Assert.That(labels, Has.Member("love cuff"),
                "LLM-added value should appear");
        });
    }

    [Test]
    public void Should_not_remove_high_frequency_value_during_refinement()
    {
        // This is the exact scenario that kills "baby" in production:
        // The LLM refiner sees {baby, small} and removes "baby" thinking
        // it's vague/generic, when in fact it represents Baby Love (~$1,500).
        var babyNgram = new Ngram("baby", new[] { "baby" }, 105);
        var smallNgram = new Ngram("small", new[] { "small" }, 202);

        var axes = new List<Axis>
        {
            new("Axis 0", new List<AxisValue>
            {
                new("baby", new[] { babyNgram }),
                new("small", new[] { smallNgram }),
            }),
        };

        // Match sets proving "baby" is statistically significant
        var matchSets = new Dictionary<string, MatchSet>
        {
            ["baby"] = new(babyNgram, MakeIndexSet(0, 105)),   // 6.5% of 1612
            ["small"] = new(smallNgram, MakeIndexSet(105, 202)), // 12.5% of 1612
        };

        // LLM tries to remove "baby" — this is what happens in production
        var refinement = new TaxonomyRefinement(
            new[]
            {
                new RefinedAxis("Axis 0", "model", 3,
                    new[] { "baby" }, // LLM says remove "baby"
                    new[] { "love cuff" }),
            },
            Enumerable.Empty<AxisMerge>(),
            Enumerable.Empty<string>());

        var result = TaxonomyService.ApplyRefinement(axes, refinement, matchSets, 1612);

        var modelAxis = result.First(a => a.Name == "model");
        var labels = modelAxis.Values.Select(v => v.Label).ToList();

        Assert.That(labels, Has.Member("baby"),
            "ApplyRefinement should guard against removing 'baby' because it has " +
            "105 matches (6.5% of 1612), well above the 3% significance threshold. " +
            "The LLM incorrectly considers it vague, but the statistical pipeline " +
            "validated it as a real sub-product variant.");
    }

    [Test]
    public void Should_allow_removal_of_low_frequency_noise_values()
    {
        // Noise values below significance threshold SHOULD be removable by the LLM
        var noiseNgram = new Ngram("box", new[] { "box" }, 15);
        var smallNgram = new Ngram("small", new[] { "small" }, 202);
        var babyNgram = new Ngram("baby", new[] { "baby" }, 105);

        var axes = new List<Axis>
        {
            new("Axis 0", new List<AxisValue>
            {
                new("box", new[] { noiseNgram }),
                new("small", new[] { smallNgram }),
                new("baby", new[] { babyNgram }),
            }),
        };

        var matchSets = new Dictionary<string, MatchSet>
        {
            ["box"] = new(noiseNgram, MakeIndexSet(0, 15)),      // 0.9% — below 3%
            ["small"] = new(smallNgram, MakeIndexSet(15, 202)),
            ["baby"] = new(babyNgram, MakeIndexSet(217, 105)),
        };

        var refinement = new TaxonomyRefinement(
            new[]
            {
                new RefinedAxis("Axis 0", "model", 3,
                    new[] { "box" }, // LLM removes noise — should be allowed
                    Enumerable.Empty<string>()),
            },
            Enumerable.Empty<AxisMerge>(),
            Enumerable.Empty<string>());

        var result = TaxonomyService.ApplyRefinement(axes, refinement, matchSets, 1612);

        var modelAxis = result.First(a => a.Name == "model");
        var labels = modelAxis.Values.Select(v => v.Label).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(labels, Has.No.Member("box"),
                "Low-frequency noise values should be removable by the LLM");
            Assert.That(labels, Has.Member("small"));
            Assert.That(labels, Has.Member("baby"));
        });
    }

    [Test]
    public void Should_still_work_without_match_sets_for_backward_compatibility()
    {
        // When matchSets is null (old call sites), ApplyRefinement should
        // behave exactly as before — no guard rail.
        var axes = new List<Axis>
        {
            new("Axis 0", new List<AxisValue>
            {
                MakeValue("baby"),
                MakeValue("small"),
            }),
        };

        var refinement = new TaxonomyRefinement(
            new[]
            {
                new RefinedAxis("Axis 0", "model", 3,
                    new[] { "baby" },
                    Enumerable.Empty<string>()),
            },
            Enumerable.Empty<AxisMerge>(),
            Enumerable.Empty<string>());

        // No matchSets — old behavior, removal goes through
        var result = TaxonomyService.ApplyRefinement(axes, refinement);

        var modelAxis = result.First(a => a.Name == "model");
        var labels = modelAxis.Values.Select(v => v.Label).ToList();

        Assert.That(labels, Has.No.Member("baby"),
            "Without matchSets, removal should proceed as before for backward compatibility");
    }

    // --- Helpers ---

    private static AxisValue MakeValue(string label)
    {
        return new AxisValue(label, new[] { new Ngram(label, new[] { label }, 0) });
    }

    private static HashSet<int> MakeIndexSet(int start, int count)
    {
        return new HashSet<int>(Enumerable.Range(start, count));
    }

    /// <summary>
    /// Deterministic embeddings. "baby" and "small" are moderately similar (sub-product
    /// variants) but distinct from color tokens. This mirrors real embedding behavior.
    /// </summary>
    private static float[] EmbeddingForText(string text)
    {
        return text switch
        {
            // Model variants — share dimension 0, differ in dimension 1
            "baby" => Normalize(new[] { 0.8f, 0.4f, 0f, 0f, 0f, 0f, 0f, 0f }),
            "small" => Normalize(new[] { 0.8f, -0.2f, 0f, 0f, 0f, 0f, 0f, 0f }),

            // Colors — share dimension 2
            "yellow" => Normalize(new[] { 0f, 0f, 0.9f, 0.3f, 0f, 0f, 0f, 0f }),
            "white" => Normalize(new[] { 0f, 0f, 0.9f, -0.2f, 0f, 0f, 0f, 0f }),
            "pink" => Normalize(new[] { 0f, 0f, 0.8f, 0.1f, 0.3f, 0f, 0f, 0f }),
            "rose" => Normalize(new[] { 0f, 0f, 0.8f, 0.2f, 0.2f, 0f, 0f, 0f }),

            // Multi-word color variants
            "yellow gold" => Normalize(new[] { 0f, 0f, 0.9f, 0.3f, 0f, 0.1f, 0f, 0f }),
            "white gold" => Normalize(new[] { 0f, 0f, 0.9f, -0.2f, 0f, 0.1f, 0f, 0f }),
            "rose gold" => Normalize(new[] { 0f, 0f, 0.8f, 0.2f, 0.2f, 0.1f, 0f, 0f }),
            "18k yellow gold" => Normalize(new[] { 0f, 0f, 0.9f, 0.3f, 0f, 0.2f, 0f, 0f }),
            "18k white gold" => Normalize(new[] { 0f, 0f, 0.9f, -0.2f, 0f, 0.2f, 0f, 0f }),
            "18k rose gold" => Normalize(new[] { 0f, 0f, 0.8f, 0.2f, 0.2f, 0.2f, 0f, 0f }),
            "pink gold" => Normalize(new[] { 0f, 0f, 0.8f, 0.1f, 0.3f, 0.1f, 0f, 0f }),

            // Sizes — share dimension 4
            "16" => Normalize(new[] { 0f, 0f, 0f, 0f, 0.9f, 0.1f, 0f, 0f }),
            "17" => Normalize(new[] { 0f, 0f, 0f, 0f, 0.9f, -0.1f, 0f, 0f }),
            "18" => Normalize(new[] { 0f, 0f, 0f, 0f, 0.9f, 0.2f, 0f, 0f }),
            "19" => Normalize(new[] { 0f, 0f, 0f, 0f, 0.9f, -0.2f, 0f, 0f }),
            "size 16" => Normalize(new[] { 0f, 0f, 0f, 0f, 0.9f, 0.1f, 0.1f, 0f }),
            "size 17" => Normalize(new[] { 0f, 0f, 0f, 0f, 0.9f, -0.1f, 0.1f, 0f }),

            // Condition terms
            "pre" => Normalize(new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0.9f, 0.2f }),
            "owned" => Normalize(new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0.9f, -0.1f }),
            "cert" => Normalize(new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0.8f, 0.3f }),
            "box" => Normalize(new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0.7f, 0.4f }),
            "authentic" => Normalize(new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0.8f, -0.2f }),

            // Other tokens
            "diamond" => Normalize(new[] { 0f, 0f, 0f, 0f, 0f, 0.8f, 0f, 0.3f }),
            "18k" => Normalize(new[] { 0f, 0f, 0f, 0f, 0f, 0.7f, 0f, 0.4f }),
            "gold" => Normalize(new[] { 0f, 0f, 0.4f, 0f, 0f, 0.5f, 0f, 0f }),
            "size" => Normalize(new[] { 0f, 0f, 0f, 0f, 0.7f, 0f, 0f, 0.3f }),

            _ => HashBasedEmbedding(text, 8),
        };
    }

    private static float[] Normalize(float[] v)
    {
        var mag = MathF.Sqrt(v.Sum(x => x * x));
        if (mag > 0)
        {
            for (var i = 0; i < v.Length; i++)
            {
                v[i] /= mag;
            }
        }
        return v;
    }

    private static float[] HashBasedEmbedding(string text, int dims)
    {
        // Use a deterministic hash (GetHashCode is non-deterministic in .NET Core)
        var hash = 0;
        foreach (var c in text)
        {
            hash = hash * 31 + c;
        }

        var vec = new float[dims];
        var rng = new Random(hash & 0x7FFFFFFF);
        for (var i = 0; i < dims; i++)
        {
            vec[i] = (float)(rng.NextDouble() * 2 - 1);
        }

        var mag = MathF.Sqrt(vec.Sum(v => v * v));
        if (mag > 0)
        {
            for (var i = 0; i < dims; i++)
            {
                vec[i] /= mag;
            }
        }
        return vec;
    }
}
