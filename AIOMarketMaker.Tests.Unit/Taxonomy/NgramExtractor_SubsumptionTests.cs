using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Services.Taxonomy;
using Moq;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class NgramExtractor_SubsumptionTests
{
    private Mock<IEmbeddingService> _embeddingMock;
    private NgramExtractor _extractor;

    [SetUp]
    public void SetUp()
    {
        _embeddingMock = new Mock<IEmbeddingService>();
        _extractor = new NgramExtractor(_embeddingMock.Object);
    }

    [Test]
    public async Task Should_not_subsume_bigram_into_unigram_when_tokens_overlap_and_similar()
    {
        // "disc edition" contains all tokens of "disc" and they're semantically similar,
        // but bigrams should not be subsumed into unigrams — they are different categories
        var ngrams = new List<Ngram>
        {
            new("disc", new[] { "disc" }, 100),
            new("disc edition", new[] { "disc edition" }, 50),
        };

        // Return vectors with high cosine similarity (same direction)
        _embeddingMock
            .Setup(e => e.GetEmbeddings(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), EmbeddingModel.Small))
            .ReturnsAsync(new[]
            {
                new[] { 1.0f, 0.0f, 0.0f },  // "disc"
                new[] { 0.95f, 0.05f, 0.0f }, // "disc edition" — very similar to "disc"
            });

        var result = (await _extractor.SubsumeByTokenOverlap(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(2),
            "Bigram should NOT be subsumed into unigram — they are different categories");
        Assert.That(result.Any(n => n.Canonical == "disc"), Is.True);
        Assert.That(result.Any(n => n.Canonical == "disc edition"), Is.True);
    }

    [Test]
    public async Task Should_not_subsume_when_tokens_dont_overlap()
    {
        // "digital" does not contain "disc" tokens
        var ngrams = new List<Ngram>
        {
            new("disc", new[] { "disc" }, 100),
            new("digital", new[] { "digital" }, 80),
        };

        _embeddingMock
            .Setup(e => e.GetEmbeddings(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), EmbeddingModel.Small))
            .ReturnsAsync(new[]
            {
                new[] { 1.0f, 0.0f, 0.0f },
                new[] { 0.9f, 0.1f, 0.0f }, // high similarity but tokens don't overlap
            });

        var result = (await _extractor.SubsumeByTokenOverlap(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(2), "Non-overlapping tokens should stay separate");
    }

    [Test]
    public async Task Should_not_subsume_when_similarity_below_threshold()
    {
        // "disc edition" contains "disc" but they're not similar enough
        var ngrams = new List<Ngram>
        {
            new("disc", new[] { "disc" }, 100),
            new("disc edition", new[] { "disc edition" }, 50),
        };

        _embeddingMock
            .Setup(e => e.GetEmbeddings(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), EmbeddingModel.Small))
            .ReturnsAsync(new[]
            {
                new[] { 1.0f, 0.0f, 0.0f },  // "disc"
                new[] { 0.0f, 1.0f, 0.0f },   // "disc edition" — orthogonal, low similarity
            });

        var result = (await _extractor.SubsumeByTokenOverlap(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(2), "Low similarity should prevent subsumption");
    }

    [Test]
    public async Task Should_not_subsume_chain_of_bigrams_into_unigram()
    {
        // "disc edition" and "disc console" contain all tokens of "disc" but they are
        // bigrams — they should not be subsumed into the unigram "disc"
        var ngrams = new List<Ngram>
        {
            new("disc", new[] { "disc" }, 100),
            new("disc edition", new[] { "disc edition" }, 50),
            new("disc console", new[] { "disc console" }, 40),
        };

        _embeddingMock
            .Setup(e => e.GetEmbeddings(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), EmbeddingModel.Small))
            .ReturnsAsync(new[]
            {
                new[] { 1.0f, 0.0f, 0.0f },    // "disc"
                new[] { 0.95f, 0.05f, 0.0f },   // "disc edition"
                new[] { 0.93f, 0.0f, 0.07f },   // "disc console"
            });

        var result = (await _extractor.SubsumeByTokenOverlap(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(3),
            "Bigrams should NOT be subsumed into unigram — all three survive");
    }

    [Test]
    public async Task Should_not_subsume_equal_length_ngrams()
    {
        // "disc edition" and "disc console" are both bigrams — neither subsumes the other
        var ngrams = new List<Ngram>
        {
            new("disc edition", new[] { "disc edition" }, 50),
            new("disc console", new[] { "disc console" }, 40),
        };

        _embeddingMock
            .Setup(e => e.GetEmbeddings(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), EmbeddingModel.Small))
            .ReturnsAsync(new[]
            {
                new[] { 1.0f, 0.0f, 0.0f },
                new[] { 0.95f, 0.05f, 0.0f },
            });

        var result = (await _extractor.SubsumeByTokenOverlap(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(2), "Equal-length ngrams should not subsume each other");
    }

    [Test]
    public async Task Should_return_unchanged_when_single_ngram()
    {
        var ngrams = new List<Ngram>
        {
            new("disc", new[] { "disc" }, 100),
        };

        var result = (await _extractor.SubsumeByTokenOverlap(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Canonical, Is.EqualTo("disc"));
    }

    [Test]
    public async Task Should_not_subsume_bigram_into_unigram_even_when_similar()
    {
        var ngrams = new List<Ngram>
        {
            new("gold", new[] { "gold" }, 100),
            new("gold plated", new[] { "gold plated" }, 50),
        };

        _embeddingMock
            .Setup(e => e.GetEmbeddings(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), EmbeddingModel.Small))
            .ReturnsAsync(new[]
            {
                new[] { 1.0f, 0.0f, 0.0f },
                new[] { 0.95f, 0.05f, 0.0f },
            });

        var result = (await _extractor.SubsumeByTokenOverlap(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(2),
            "Bigram should NOT be subsumed into unigram — they are different categories");
        Assert.That(result.Any(n => n.Canonical == "gold"), Is.True);
        Assert.That(result.Any(n => n.Canonical == "gold plated"), Is.True);
    }

    [Test]
    public async Task Should_subsume_trigram_into_bigram_when_tokens_overlap_and_similar()
    {
        var ngrams = new List<Ngram>
        {
            new("gold plated", new[] { "gold plated" }, 100),
            new("gold plated ring", new[] { "gold plated ring" }, 30),
        };

        _embeddingMock
            .Setup(e => e.GetEmbeddings(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), EmbeddingModel.Small))
            .ReturnsAsync(new[]
            {
                new[] { 1.0f, 0.0f, 0.0f },
                new[] { 0.95f, 0.05f, 0.0f },
            });

        var result = (await _extractor.SubsumeByTokenOverlap(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(1),
            "Trigram should be subsumed into bigram — same multi-word level");
        Assert.That(result[0].Canonical, Is.EqualTo("gold plated"));
        Assert.That(result[0].Forms, Does.Contain("gold plated ring"));
    }
}
