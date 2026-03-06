using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class MutualExclusivityAnalyzerTests
{
    private MutualExclusivityAnalyzer _analyzer;

    [SetUp]
    public void SetUp()
    {
        _analyzer = new MutualExclusivityAnalyzer();
    }

    [Test]
    public void Should_compute_match_sets_for_ngrams()
    {
        var titles = new[]
        {
            "PS5 Disc Console",
            "PS5 Digital Console",
            "PS5 Disc Edition",
        };
        var ngrams = new[]
        {
            new Ngram("disc", new[] { "disc" }, 20),
            new Ngram("digital", new[] { "digital" }, 10),
        };

        var result = _analyzer.ComputeMatchSets(titles, ngrams).ToList();

        var disc = result.First(m => m.Ngram.Canonical == "disc");
        Assert.That(disc.ListingIndices, Is.EquivalentTo(new[] { 0, 2 }));

        var digital = result.First(m => m.Ngram.Canonical == "digital");
        Assert.That(digital.ListingIndices, Is.EquivalentTo(new[] { 1 }));
    }

    [Test]
    public void Should_use_word_boundary_for_single_word_ngrams()
    {
        var titles = new[]
        {
            "PS5 Pro Console",
            "PS5 Professional Grade",
        };
        var ngrams = new[]
        {
            new Ngram("pro", new[] { "pro" }, 20),
        };

        var result = _analyzer.ComputeMatchSets(titles, ngrams).ToList();
        var pro = result.First();

        Assert.That(pro.ListingIndices, Is.EquivalentTo(new[] { 0 }));
    }

    [Test]
    public void Should_use_substring_for_multi_word_ngrams()
    {
        var titles = new[]
        {
            "PS5 Disc Edition Console",
            "PS5 Console Digital",
        };
        var ngrams = new[]
        {
            new Ngram("disc edition", new[] { "disc edition" }, 20),
        };

        var result = _analyzer.ComputeMatchSets(titles, ngrams).ToList();
        Assert.That(result.First().ListingIndices, Is.EquivalentTo(new[] { 0 }));
    }

    [Test]
    public void Should_match_all_forms_of_deduped_ngram()
    {
        var titles = new[]
        {
            "PS5 Console",
            "PlayStation 5 Console",
        };
        var ngrams = new[]
        {
            new Ngram("ps5", new[] { "ps5", "playstation 5" }, 30),
        };

        var result = _analyzer.ComputeMatchSets(titles, ngrams).ToList();
        Assert.That(result.First().ListingIndices, Is.EquivalentTo(new[] { 0, 1 }));
    }

    [Test]
    public void Should_find_exclusive_pair_when_overlap_below_threshold()
    {
        var discSet = new HashSet<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var digitalSet = new HashSet<int> { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 };

        var matchSets = new[]
        {
            new MatchSet(new Ngram("disc", new[] { "disc" }, 10), discSet),
            new MatchSet(new Ngram("digital", new[] { "digital" }, 10), digitalSet),
        };

        var result = _analyzer.FindExclusivePairs(matchSets).ToList();

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Overlap, Is.EqualTo(0.0));
    }

    [Test]
    public void Should_not_flag_pair_when_overlap_above_threshold()
    {
        var setA = new HashSet<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var setB = new HashSet<int> { 0, 1, 10, 11, 12, 13, 14, 15, 16, 17 };

        var matchSets = new[]
        {
            new MatchSet(new Ngram("slim", new[] { "slim" }, 10), setA),
            new MatchSet(new Ngram("console", new[] { "console" }, 10), setB),
        };

        var result = _analyzer.FindExclusivePairs(matchSets).ToList();
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Should_compute_overlap_using_min_denominator()
    {
        var largeSet = new HashSet<int>(Enumerable.Range(0, 100));
        var smallSet = new HashSet<int> { 0, 101, 102, 103, 104 };

        var matchSets = new[]
        {
            new MatchSet(new Ngram("large", new[] { "large" }, 100), largeSet),
            new MatchSet(new Ngram("small", new[] { "small" }, 5), smallSet),
        };

        var result = _analyzer.FindExclusivePairs(matchSets).ToList();
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Should_skip_empty_match_sets()
    {
        var matchSets = new[]
        {
            new MatchSet(new Ngram("disc", new[] { "disc" }, 10), new HashSet<int> { 0, 1 }),
            new MatchSet(new Ngram("empty", new[] { "empty" }, 0), new HashSet<int>()),
        };

        var result = _analyzer.FindExclusivePairs(matchSets).ToList();
        Assert.That(result, Is.Empty);
    }
}
