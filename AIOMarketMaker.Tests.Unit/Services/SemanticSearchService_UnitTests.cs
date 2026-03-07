using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class SemanticSearchService_UnitTests
{
    private Mock<IVectorIndex> _mockVectorIndex = null!;
    private Mock<IEmbeddingService> _mockEmbedding = null!;
    private Mock<ILogger<SemanticSearchService>> _mockLogger = null!;
    private VectorIndexConfig _config = null!;
    private SemanticSearchService _service = null!;

    [SetUp]
    public void Setup()
    {
        _mockVectorIndex = new Mock<IVectorIndex>();
        _mockEmbedding = new Mock<IEmbeddingService>();
        _mockLogger = new Mock<ILogger<SemanticSearchService>>();
        _config = new VectorIndexConfig("test.usearch", "test-idmap.json", TopK: 5, SimilarityThreshold: 0.7f);
        _service = new SemanticSearchService(_config, _mockVectorIndex.Object, _mockEmbedding.Object, _mockLogger.Object);
    }

    [Test]
    public async Task Should_return_zero_counts_when_indexing_empty_list()
    {
        var result = await _service.IndexListings(Array.Empty<Listing>());

        Assert.Multiple(() =>
        {
            Assert.That(result.UpsertedCount, Is.EqualTo(0));
            Assert.That(result.SkippedCount, Is.EqualTo(0));
            Assert.That(result.Errors, Is.Empty);
        });
        _mockVectorIndex.Verify(x => x.UpsertBatch(It.IsAny<IEnumerable<(string Id, float[] Vector)>>()), Times.Never);
    }

    private static IEnumerable<TestCaseData> ListingsToSkipCases()
    {
        yield return new TestCaseData("", "").SetDescription("Both empty strings");
        yield return new TestCaseData(null, null).SetDescription("Both null");
        yield return new TestCaseData("   ", "   ").SetDescription("Both whitespace");
        yield return new TestCaseData("", null).SetDescription("Empty title, null description");
        yield return new TestCaseData(null, "").SetDescription("Null title, empty description");
        yield return new TestCaseData("\t", "\n").SetDescription("Tab and newline");
    }

    [TestCaseSource(nameof(ListingsToSkipCases))]
    public async Task Should_skip_listing_with_no_content(string? title, string? description)
    {
        var listing = new Listing { ListingId = "1", Title = title, Description = description };

        var result = await _service.IndexListings(new[] { listing });

        Assert.Multiple(() =>
        {
            Assert.That(result.UpsertedCount, Is.EqualTo(0));
            Assert.That(result.SkippedCount, Is.EqualTo(1));
        });
        _mockEmbedding.Verify(x => x.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()), Times.Never);
    }

    private static IEnumerable<TestCaseData> ValidContentCases()
    {
        yield return new TestCaseData("Title only", null).SetDescription("Title only");
        yield return new TestCaseData(null, "Description only").SetDescription("Description only");
        yield return new TestCaseData("Title", "Description").SetDescription("Both title and description");
        yield return new TestCaseData("Title", "").SetDescription("Title with empty description");
        yield return new TestCaseData("", "Description").SetDescription("Empty title with description");
    }

    [TestCaseSource(nameof(ValidContentCases))]
    public async Task Should_index_listing_with_valid_content(string? title, string? description)
    {
        var listing = new Listing { ListingId = "123", Title = title, Description = description };
        _mockEmbedding
            .Setup(x => x.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(new[] { new float[] { 0.1f } });

        var result = await _service.IndexListings(new[] { listing });

        Assert.That(result.UpsertedCount, Is.EqualTo(1));
        _mockVectorIndex.Verify(x => x.UpsertBatch(
            It.Is<IEnumerable<(string Id, float[] Vector)>>(items =>
                items.Count() == 1 && items.First().Id == "123")),
            Times.Once);
    }

    private static IEnumerable<TestCaseData> InvalidQueryCases()
    {
        yield return new TestCaseData("").SetDescription("Empty string");
        yield return new TestCaseData("   ").SetDescription("Whitespace only");
        yield return new TestCaseData("\t").SetDescription("Tab only");
        yield return new TestCaseData("\n").SetDescription("Newline only");
        yield return new TestCaseData("  \t\n  ").SetDescription("Mixed whitespace");
    }

    [TestCaseSource(nameof(InvalidQueryCases))]
    public void Should_throw_when_searching_with_invalid_query(string query)
    {
        Assert.ThrowsAsync<ArgumentException>(async () => await _service.Search(query));
    }

    private static IEnumerable<TestCaseData> ThresholdBoundaryCases()
    {
        // Config threshold is 0.7f
        yield return new TestCaseData(0.70f, true).SetDescription("Exactly at threshold - included");
        yield return new TestCaseData(0.71f, true).SetDescription("Just above threshold - included");
        yield return new TestCaseData(0.69f, false).SetDescription("Just below threshold - excluded");
        yield return new TestCaseData(1.0f, true).SetDescription("Perfect score - included");
        yield return new TestCaseData(0.0f, false).SetDescription("Zero score - excluded");
        yield return new TestCaseData(0.699999f, false).SetDescription("Barely below threshold - excluded");
    }

    [TestCaseSource(nameof(ThresholdBoundaryCases))]
    public async Task Should_filter_by_similarity_threshold(float score, bool shouldBeIncluded)
    {
        var queryEmbedding = new float[] { 0.1f };
        _mockEmbedding
            .Setup(x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(queryEmbedding);

        _mockVectorIndex
            .Setup(x => x.Search(It.IsAny<float[]>(), It.IsAny<int>()))
            .Returns(new[] { new VectorSearchHit("test-id", score) });

        var result = await _service.Search("test query");

        Assert.That(result.Hits.Any(h => h.ListingId == "test-id"), Is.EqualTo(shouldBeIncluded));
    }

    [Test]
    public async Task Should_exclude_self_when_finding_similar()
    {
        _mockVectorIndex
            .Setup(x => x.SearchById(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new[]
            {
                new VectorSearchHit("source-listing", 1.0f),
                new VectorSearchHit("similar-listing", 0.9f)
            });

        var result = await _service.FindSimilar("source-listing");

        Assert.Multiple(() =>
        {
            Assert.That(result.Hits, Has.Count.EqualTo(1));
            Assert.That(result.Hits[0].ListingId, Is.EqualTo("similar-listing"));
        });
    }

    [Test]
    public async Task Should_not_call_delete_when_list_is_empty()
    {
        await _service.Delete(Array.Empty<string>());

        _mockVectorIndex.Verify(x => x.Remove(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Test]
    public async Task Should_call_delete_with_correct_ids()
    {
        var ids = new[] { "id1", "id2" };

        await _service.Delete(ids);

        _mockVectorIndex.Verify(x => x.Remove(
            It.Is<IEnumerable<string>>(r => r.SequenceEqual(ids))),
            Times.Once);
    }

    [Test]
    public async Task Should_return_true_when_listing_exists()
    {
        _mockVectorIndex
            .Setup(x => x.Contains("listing-123"))
            .Returns(true);

        var exists = await _service.Exists("listing-123");

        Assert.That(exists, Is.True);
    }

    [Test]
    public async Task Should_return_false_when_listing_does_not_exist()
    {
        _mockVectorIndex
            .Setup(x => x.Contains("non-existent"))
            .Returns(false);

        var exists = await _service.Exists("non-existent");

        Assert.That(exists, Is.False);
    }

    [Test]
    public async Task Should_use_config_topk_when_not_specified()
    {
        var queryEmbedding = new float[] { 0.1f };
        _mockEmbedding.Setup(x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(queryEmbedding);
        _mockVectorIndex.Setup(x => x.Search(It.IsAny<float[]>(), It.IsAny<int>()))
            .Returns(Array.Empty<VectorSearchHit>());

        await _service.Search("test");

        _mockVectorIndex.Verify(x => x.Search(
            It.IsAny<float[]>(),
            5),
            Times.Once);
    }

    [Test]
    public async Task Should_override_topk_when_specified()
    {
        var queryEmbedding = new float[] { 0.1f };
        _mockEmbedding.Setup(x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(queryEmbedding);
        _mockVectorIndex.Setup(x => x.Search(It.IsAny<float[]>(), It.IsAny<int>()))
            .Returns(Array.Empty<VectorSearchHit>());

        await _service.Search("test", topK: 20);

        _mockVectorIndex.Verify(x => x.Search(
            It.IsAny<float[]>(),
            20),
            Times.Once);
    }

    [Test]
    public async Task Should_batch_upserts_according_to_config()
    {
        var config = new VectorIndexConfig("test.usearch", "test-idmap.json", UpsertBatchSize: 2);
        var service = new SemanticSearchService(config, _mockVectorIndex.Object, _mockEmbedding.Object, _mockLogger.Object);

        var listings = new[]
        {
            new Listing { ListingId = "1", Title = "Item 1" },
            new Listing { ListingId = "2", Title = "Item 2" },
            new Listing { ListingId = "3", Title = "Item 3" }
        };

        _mockEmbedding
            .Setup(x => x.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _, EmbeddingModel __) =>
                texts.Select(_ => new float[] { 0.1f }).ToArray());

        var result = await service.IndexListings(listings);

        Assert.That(result.UpsertedCount, Is.EqualTo(3));
        _mockVectorIndex.Verify(x => x.UpsertBatch(It.IsAny<IEnumerable<(string Id, float[] Vector)>>()), Times.Exactly(2));
    }

    [Test]
    public async Task Should_continue_processing_and_capture_errors_when_batch_fails()
    {
        var config = new VectorIndexConfig("test.usearch", "test-idmap.json", UpsertBatchSize: 1);
        var service = new SemanticSearchService(config, _mockVectorIndex.Object, _mockEmbedding.Object, _mockLogger.Object);

        var listings = new[]
        {
            new Listing { ListingId = "1", Title = "Item 1" },
            new Listing { ListingId = "2", Title = "Item 2" },
            new Listing { ListingId = "3", Title = "Item 3" }
        };

        _mockEmbedding
            .Setup(x => x.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(new[] { new float[] { 0.1f } });

        var callCount = 0;
        _mockVectorIndex
            .Setup(x => x.UpsertBatch(It.IsAny<IEnumerable<(string Id, float[] Vector)>>()))
            .Callback(() =>
            {
                callCount++;
                if (callCount == 2)
                {
                    throw new Exception("Simulated vector index failure");
                }
            });

        var result = await service.IndexListings(listings);

        Assert.Multiple(() =>
        {
            Assert.That(result.UpsertedCount, Is.EqualTo(2));
            Assert.That(result.Errors, Has.Count.EqualTo(1));
            Assert.That(result.Errors[0], Does.Contain("Simulated vector index failure"));
        });
        _mockVectorIndex.Verify(x => x.UpsertBatch(It.IsAny<IEnumerable<(string Id, float[] Vector)>>()), Times.Exactly(3));
    }

    [Test]
    public async Task Should_handle_mixed_valid_and_invalid_listings_in_batch()
    {
        var listings = new[]
        {
            new Listing { ListingId = "1", Title = "Valid item" },
            new Listing { ListingId = "2", Title = "", Description = "" },
            new Listing { ListingId = "3", Title = "Another valid" }
        };

        _mockEmbedding
            .Setup(x => x.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _, EmbeddingModel __) =>
                texts.Select(_ => new float[] { 0.1f }).ToArray());

        var result = await _service.IndexListings(listings);

        Assert.Multiple(() =>
        {
            Assert.That(result.UpsertedCount, Is.EqualTo(2));
            Assert.That(result.SkippedCount, Is.EqualTo(1));
        });
        _mockVectorIndex.Verify(x => x.UpsertBatch(
            It.Is<IEnumerable<(string Id, float[] Vector)>>(items => items.Count() == 2)),
            Times.Once);
    }

    [Test]
    public async Task Should_request_topk_plus_one_when_finding_similar()
    {
        _mockVectorIndex
            .Setup(x => x.SearchById(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(Array.Empty<VectorSearchHit>());

        await _service.FindSimilar("source-id", topK: 5);

        _mockVectorIndex.Verify(x => x.SearchById("source-id", 6), Times.Once);
    }

    [Test]
    public async Task Should_return_empty_hits_when_no_matches()
    {
        var queryEmbedding = new float[] { 0.1f };
        _mockEmbedding.Setup(x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(queryEmbedding);
        _mockVectorIndex.Setup(x => x.Search(It.IsAny<float[]>(), It.IsAny<int>()))
            .Returns(Array.Empty<VectorSearchHit>());

        var result = await _service.Search("test");

        Assert.That(result.Hits, Is.Empty);
    }
}
