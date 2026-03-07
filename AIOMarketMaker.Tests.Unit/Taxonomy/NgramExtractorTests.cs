using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Services.Taxonomy;
using Moq;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class NgramExtractorTests
{
    private NgramExtractor _extractor;

    [SetUp]
    public void SetUp()
    {
        _extractor = new NgramExtractor(null!);
    }

    [Test]
    public void Should_extract_unigrams_from_single_title()
    {
        var titles = Enumerable.Repeat("PlayStation Console Digital", 25);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Canonical == "playstation"), Is.True);
        Assert.That(result.Any(n => n.Canonical == "console"), Is.True);
        Assert.That(result.Any(n => n.Canonical == "digital"), Is.True);
    }

    [Test]
    public void Should_extract_bigrams()
    {
        var titles = Enumerable.Repeat("PS5 Slim Console White", 25);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Canonical == "ps5 slim"), Is.True);
        Assert.That(result.Any(n => n.Canonical == "slim console"), Is.True);
    }

    [Test]
    public void Should_extract_trigrams()
    {
        var titles = Enumerable.Repeat("PS5 Slim Digital Console", 25);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Canonical == "ps5 slim digital"), Is.True);
    }

    [Test]
    public void Should_filter_stop_words()
    {
        var titles = Enumerable.Repeat("the best new console for gaming", 25);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Canonical == "the"), Is.False);
        Assert.That(result.Any(n => n.Canonical == "new"), Is.False);
        Assert.That(result.Any(n => n.Canonical == "for"), Is.False);
    }

    [Test]
    public void Should_filter_single_character_words()
    {
        var titles = Enumerable.Repeat("PS5 x controller", 25);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Canonical == "x"), Is.False);
    }

    [Test]
    public void Should_count_frequency_across_titles()
    {
        var titles = Enumerable.Repeat("PS5 Console", 50)
            .Concat(Enumerable.Repeat("Xbox Console", 30));
        var result = _extractor.Extract(titles).ToList();

        var console = result.First(n => n.Canonical == "console");
        Assert.That(console.Frequency, Is.EqualTo(80));
    }

    [Test]
    public void Should_scale_frequency_threshold_with_listing_count()
    {
        var titles = Enumerable.Repeat("PS5 Console", 4000);
        var rare = Enumerable.Repeat("RareWord Console", 19);
        var result = _extractor.Extract(titles.Concat(rare)).ToList();

        Assert.That(result.Any(n => n.Canonical == "rareword"), Is.False);
    }

    [Test]
    public void Should_return_lowercase_canonicals()
    {
        var titles = Enumerable.Repeat("PlayStation CONSOLE Digital", 25);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.All(n => n.Canonical == n.Canonical.ToLowerInvariant()));
    }

    [Test]
    public void Should_identify_numeric_variants()
    {
        Assert.That(NgramExtractor.AreNumericVariants("256gb", "512gb"), Is.True);
        Assert.That(NgramExtractor.AreNumericVariants("size 10", "size 11"), Is.True);
    }

    [Test]
    public void Should_not_flag_non_numeric_differences_as_variants()
    {
        Assert.That(NgramExtractor.AreNumericVariants("red", "blue"), Is.False);
        Assert.That(NgramExtractor.AreNumericVariants("256gb", "256gb"), Is.False);
    }

    [Test]
    public void Should_not_flag_structurally_different_strings_as_variants()
    {
        Assert.That(NgramExtractor.AreNumericVariants("256gb ssd", "512"), Is.False);
    }

    [Test]
    public async Task Should_merge_synonyms_above_similarity_threshold()
    {
        var ngrams = new[]
        {
            new Ngram("playstation 5", new[] { "playstation 5" }, 100),
            new Ngram("ps5", new[] { "ps5" }, 200),
            new Ngram("digital", new[] { "digital" }, 50),
        };

        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(new[]
            {
                new[] { 1f, 0f, 0f },
                new[] { 0.98f, 0.1f, 0f },
                new[] { 0f, 0f, 1f },
            });

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var result = (await extractor.Deduplicate(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(2));
        var merged = result.First(n => n.Canonical == "ps5");
        Assert.That(merged.Frequency, Is.EqualTo(300));
        Assert.That(merged.Forms, Does.Contain("ps5"));
        Assert.That(merged.Forms, Does.Contain("playstation 5"));
    }

    [Test]
    public async Task Should_not_merge_numeric_variants_even_when_similar()
    {
        var ngrams = new[]
        {
            new Ngram("256gb", new[] { "256gb" }, 100),
            new Ngram("512gb", new[] { "512gb" }, 80),
        };

        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(new[]
            {
                new[] { 1f, 0f },
                new[] { 0.99f, 0.01f },
            });

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var result = (await extractor.Deduplicate(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(2));
    }

    // ======================================================================
    // BUG TESTS — discovered from backfill data analysis (2026-03-07)
    // ======================================================================

    // BUG 1: Marketplace noise words become axis values.
    // Real data: Vitamix Blender has axis values "only", "non", "brand".
    //            Ninja Foodi has "disposable", "accessories", "brand".
    //            Dyson Airwrap has "attachment", "attachments", "brand".
    // These aren't product variants — they're listing description filler.
    [Test]
    [TestCase("only")]
    [TestCase("non")]
    [TestCase("brand")]
    [TestCase("used")]
    [TestCase("item")]
    [TestCase("lot")]
    [TestCase("set")]
    [TestCase("genuine")]
    [TestCase("original")]
    [TestCase("compatible")]
    [TestCase("replacement")]
    public void Should_filter_marketplace_noise_words(string noiseWord)
    {
        var titles = Enumerable.Repeat($"Dyson Airwrap {noiseWord} complete", 25);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Canonical == noiseWord), Is.False,
            $"'{noiseWord}' should be filtered as marketplace noise");
    }

    // BUG 2: Plural and singular forms produce separate n-grams.
    // Real data: Dyson Airwrap has both "attachment" and "attachments" as
    //            separate Axis 0 values. Nike Air Jordan 1 has "sneakers"
    //            and "sneaker". These should normalize to the same form.
    [Test]
    [TestCase("attachment", "attachments")]
    [TestCase("accessory", "accessories")]
    [TestCase("controller", "controllers")]
    [TestCase("sneaker", "sneakers")]
    [TestCase("charger", "chargers")]
    public void Should_normalize_simple_plurals_to_singular(string singular, string plural)
    {
        // Mix of singular and plural forms — both should produce the same canonical
        var titles = Enumerable.Repeat($"PS5 {singular} white", 15)
            .Concat(Enumerable.Repeat($"PS5 {plural} white", 15));
        var result = _extractor.Extract(titles).ToList();

        var singularResult = result.FirstOrDefault(n => n.Canonical == singular);
        var pluralResult = result.FirstOrDefault(n => n.Canonical == plural);

        Assert.Multiple(() =>
        {
            // Should have one form, not both
            Assert.That(singularResult != null || pluralResult != null, Is.True,
                "At least one form should exist");
            Assert.That(singularResult != null && pluralResult != null, Is.False,
                $"Should not have both '{singular}' AND '{plural}' as separate n-grams");
        });
    }

    // BUG 3: Spelling variants like "disc"/"disk" and "grey"/"gray" escape
    //         the embedding dedup (0.95 threshold) and become separate axis values.
    // Real data: PlayStation 5 has both "disc" (1932 listings) and "disk" (227 listings)
    //            as separate Axis 0 values. These are the same variant.
    // This test verifies that the Deduplicate step merges spelling variants
    // even when embedding similarity is below 0.95 (which it often is for
    // single-word near-synonyms).
    [Test]
    [TestCase("disc", "disk")]
    [TestCase("grey", "gray")]
    [TestCase("colour", "color")]
    public async Task Should_merge_spelling_variants_via_edit_distance(string formA, string formB)
    {
        var ngrams = new[]
        {
            new Ngram(formA, new[] { formA }, 200),
            new Ngram(formB, new[] { formB }, 50),
            new Ngram("console", new[] { "console" }, 300),
        };

        // Simulate embeddings where disc/disk are similar but below 0.95 cosine
        // cosine(formA, formB) = 0.85 / (1.0 * sqrt(0.85^2 + 0.527^2)) = 0.85
        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(new[]
            {
                new[] { 1f, 0f, 0f },         // formA
                new[] { 0.85f, 0.527f, 0f },  // formB — cosine ~0.85, below 0.95
                new[] { 0f, 0f, 1f },          // console — unrelated
            });

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var result = (await extractor.Deduplicate(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(2),
            $"'{formA}' and '{formB}' should merge into one, leaving 2 total n-grams");
        var merged = result.First(n => n.Canonical == formA);
        Assert.That(merged.Forms, Does.Contain(formB),
            $"'{formB}' should be tracked as an alternative form of '{formA}'");
    }
}
