using AIOMarketMaker.Core.Services.Taxonomy;

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
}
