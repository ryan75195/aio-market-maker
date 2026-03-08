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
    public async Task Should_subsume_longer_ngram_into_shorter_when_tokens_overlap_and_similar()
    {
        // "disc edition" contains all tokens of "disc" and they're semantically similar
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

        Assert.That(result.Count, Is.EqualTo(1), "Longer ngram should be subsumed into shorter");
        Assert.That(result[0].Canonical, Is.EqualTo("disc"));
        Assert.That(result[0].Forms, Does.Contain("disc edition"));
        Assert.That(result[0].Frequency, Is.EqualTo(150));
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
    public async Task Should_subsume_chain_of_longer_ngrams_into_root()
    {
        // "disc" subsumes "disc edition" and "disc console" (both contain "disc")
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

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Canonical, Is.EqualTo("disc"));
        Assert.That(result[0].Forms, Does.Contain("disc edition"));
        Assert.That(result[0].Forms, Does.Contain("disc console"));
        Assert.That(result[0].Frequency, Is.EqualTo(190));
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
}
