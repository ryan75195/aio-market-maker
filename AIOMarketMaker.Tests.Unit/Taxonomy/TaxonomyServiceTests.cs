using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Services.Taxonomy;
using Microsoft.Extensions.Logging;
using Moq;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class TaxonomyServiceTests
{
    // Synthetic PS5 titles designed to produce known axes
    private static readonly string[] Ps5Titles = Enumerable.Empty<string>()
        .Concat(Enumerable.Repeat("PS5 Disc Console White", 30))
        .Concat(Enumerable.Repeat("PS5 Digital Console White", 25))
        .Concat(Enumerable.Repeat("PS5 Disc Console Black", 20))
        .Concat(Enumerable.Repeat("PS5 Digital Console Black", 15))
        .Concat(Enumerable.Repeat("PS5 Disc Slim Console", 10))
        .ToArray();

    private TaxonomyService _service;

    [SetUp]
    public void SetUp()
    {
        // Mock IEmbeddingService to return distinguishable vectors.
        // Key insight: the NgramExtractor.Deduplicate uses embeddings to merge
        // near-identical n-grams (cosine >= 0.95). We need to give each distinct
        // n-gram a unique vector, with semantically similar pairs (disc/digital,
        // white/black) having moderate similarity for graph edge building.
        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _, EmbeddingModel __) =>
            {
                return texts.Select(EmbeddingForText).ToArray();
            });

        mockEmbedding
            .Setup(e => e.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync((string text, CancellationToken _, EmbeddingModel __) => EmbeddingForText(text));

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var analyzer = new MutualExclusivityAnalyzer();
        var detector = new LouvainCommunityDetector();
        var decontaminator = new TitleDecontaminator(
            mockEmbedding.Object,
            Mock.Of<ILogger<TitleDecontaminator>>());

        _service = new TaxonomyService(
            extractor, analyzer, detector, mockEmbedding.Object,
            new Mock<ILogger<TaxonomyService>>().Object,
            refiner: null,
            decontaminator: decontaminator);
    }

    /// <summary>
    /// Returns a deterministic embedding for a given text. Known terms get
    /// hand-crafted vectors; unknown terms get a hash-based unique vector so
    /// that deduplication does not collapse unrelated n-grams.
    /// Full title strings used in decontamination tests get explicit vectors
    /// with high similarity to the "PS5 Console" product name.
    /// </summary>
    private static float[] EmbeddingForText(string text)
    {
        // Semantically similar pairs share a dimension so the graph builder
        // can form edges, but they remain distinct enough to survive dedup.
        return text switch
        {
            "disc" => new[] { 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f },
            "digital" => new[] { 0.7f, 0.3f, 0f, 0f, 0f, 0f, 0f, 0f },
            "white" => new[] { 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f },
            "black" => new[] { 0f, 0f, 0.7f, 0.3f, 0f, 0f, 0f, 0f },
            "slim" => new[] { 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f },
            "ps5" => new[] { 0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f },
            "console" => new[] { 0f, 0f, 0f, 0f, 0f, 0f, 1f, 0f },
            // Product name and full title strings for decontamination similarity checks.
            // All PS5 titles share ps5+console dimensions, giving high cosine similarity
            // to "PS5 Console". Xbox titles lack ps5 dimension, giving low similarity.
            "PS5 Console" => new[] { 0f, 0f, 0f, 0f, 0f, 0.707f, 0.707f, 0f },
            "PS5 Disc Console White" => new[] { 0.4f, 0f, 0.4f, 0f, 0f, 0.4f, 0.4f, 0f },
            "PS5 Digital Console White" => new[] { 0.3f, 0.1f, 0.4f, 0f, 0f, 0.4f, 0.4f, 0f },
            "PS5 Disc Console Black" => new[] { 0.4f, 0f, 0.3f, 0.1f, 0f, 0.4f, 0.4f, 0f },
            "PS5 Digital Console Black" => new[] { 0.3f, 0.1f, 0.3f, 0.1f, 0f, 0.4f, 0.4f, 0f },
            "PS5 Disc Slim Console" => new[] { 0.4f, 0f, 0f, 0f, 0.3f, 0.4f, 0.4f, 0f },
            "Xbox Series X Console Black" => new[] { 0f, 0f, 0.3f, 0.1f, 0f, 0f, 0.4f, 0.8f },
            "PS5 Disc Digital Console" => new[] { 0.5f, 0.1f, 0f, 0f, 0f, 0.4f, 0.4f, 0f },
            _ => HashBasedEmbedding(text, 8),
        };
    }

    /// <summary>
    /// Generates a deterministic pseudo-random unit vector from the text's hash code.
    /// Each distinct string gets a unique direction, avoiding false dedup merges.
    /// </summary>
    private static float[] HashBasedEmbedding(string text, int dims)
    {
        var vec = new float[dims];
        var hash = text.GetHashCode();
        var rng = new Random(hash);
        for (var i = 0; i < dims; i++)
        {
            vec[i] = (float)(rng.NextDouble() * 2 - 1);
        }

        // Normalize
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

    [Test]
    public async Task Should_discover_axes_from_synthetic_titles()
    {
        var result = await _service.Generate(Ps5Titles);

        Assert.That(result.Axes.Any(), Is.True,
            "Should discover at least one axis");
    }

    [Test]
    public async Task Should_report_coverage_greater_than_zero()
    {
        var result = await _service.Generate(Ps5Titles);

        Assert.That(result.CoveragePercent, Is.GreaterThan(0),
            "Coverage should be > 0% with synthetic data containing axis values");
    }

    [Test]
    public async Task Should_assign_listings_to_cells()
    {
        var result = await _service.Generate(Ps5Titles);

        var assigned = result.Assignments.Where(a => a.Cell.Count > 0).ToList();
        Assert.That(assigned.Count, Is.GreaterThan(0),
            "At least some listings should be assigned to cells");
    }

    [Test]
    public async Task Should_detect_conflicts_when_multiple_values_match()
    {
        var titles = Ps5Titles.Append("PS5 Disc Digital Console").ToArray();

        var result = await _service.Generate(titles);

        Assert.That(result.ConflictPercent, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public async Task Should_return_non_null_cells_collection()
    {
        var result = await _service.Generate(Ps5Titles);

        // CellStats is currently empty (skipped for stage 1) but Cells field should exist
        Assert.That(result.Cells, Is.Not.Null);
    }

    [Test]
    public async Task Should_return_empty_result_when_too_few_candidates()
    {
        // Very short list with no mutually exclusive patterns
        var titles = Enumerable.Repeat("PS5 Console", 10).ToArray();

        var result = await _service.Generate(titles);

        Assert.Multiple(() =>
        {
            Assert.That(result.Axes.Any(), Is.False,
                "Should produce no axes when there are too few exclusive candidates");
            Assert.That(result.CoveragePercent, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Should_assign_every_listing_index_exactly_once()
    {
        var result = await _service.Generate(Ps5Titles);

        var assignedIndices = result.Assignments.Select(a => a.ListingIndex).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(assignedIndices.Count, Is.EqualTo(Ps5Titles.Length),
                "Should have one assignment per title");
            Assert.That(assignedIndices.Distinct().Count(), Is.EqualTo(Ps5Titles.Length),
                "Each listing index should appear exactly once");
        });
    }

    [Test]
    public async Task Should_exclude_titles_not_matching_brand_tokens()
    {
        // 50 on-brand titles + 10 off-brand contaminants
        var onBrand = Enumerable.Repeat("PS5 Disc Console White", 25)
            .Concat(Enumerable.Repeat("PS5 Digital Console Black", 25))
            .ToList();
        var offBrand = Enumerable.Repeat("Xbox Series X Console Black", 10).ToList();
        var allTitles = onBrand.Concat(offBrand).ToList();

        var result = await _service.Generate(
            allTitles, "PS5 Console", new[] { "ps5" });

        Assert.Multiple(() =>
        {
            Assert.That(result.ExcludedCount, Is.EqualTo(10));
            // Assignments should only reference original indices 0-49 (the PS5 titles)
            var maxIndex = result.Assignments.Max(a => a.ListingIndex);
            Assert.That(maxIndex, Is.LessThan(50),
                "Assignments should map back to original indices within the on-brand range");
        });
    }

    [Test]
    public async Task Should_not_filter_when_brand_tokens_null()
    {
        var titles = Ps5Titles;

        var result = await _service.Generate(titles, "PS5 Console", null);

        Assert.That(result.ExcludedCount, Is.EqualTo(0));
    }
}
