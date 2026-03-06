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
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
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
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new[] { 1f, 0f },
                new[] { 0.99f, 0.01f },
            });

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var result = (await extractor.Deduplicate(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(2));
    }
}
