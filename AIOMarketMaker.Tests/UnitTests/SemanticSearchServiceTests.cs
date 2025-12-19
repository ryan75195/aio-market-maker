using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Pinecone;

namespace AIOMarketMaker.Tests.UnitTests;

[TestFixture]
public class SemanticSearchServiceTests
{
    private Mock<IPineconeIndexClient> _mockPinecone = null!;
    private Mock<IEmbeddingService> _mockEmbedding = null!;
    private Mock<ILogger<SemanticSearchService>> _mockLogger = null!;
    private PineconeConfig _config = null!;
    private SemanticSearchService _service = null!;

    [SetUp]
    public void Setup()
    {
        _mockPinecone = new Mock<IPineconeIndexClient>();
        _mockEmbedding = new Mock<IEmbeddingService>();
        _mockLogger = new Mock<ILogger<SemanticSearchService>>();
        _config = new PineconeConfig("test-key", "test-index", TopK: 5, SimilarityThreshold: 0.7f);
        _service = new SemanticSearchService(_config, _mockPinecone.Object, _mockEmbedding.Object, _mockLogger.Object);
    }

    [Test]
    public async Task Should_return_zero_counts_when_indexing_empty_list()
    {
        var result = await _service.IndexListingsAsync(Array.Empty<Listing>());

        Assert.Multiple(() =>
        {
            Assert.That(result.UpsertedCount, Is.EqualTo(0));
            Assert.That(result.SkippedCount, Is.EqualTo(0));
            Assert.That(result.Errors, Is.Empty);
        });
        _mockPinecone.Verify(x => x.UpsertAsync(It.IsAny<UpsertRequest>(), It.IsAny<CancellationToken>()), Times.Never);
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

        var result = await _service.IndexListingsAsync(new[] { listing });

        Assert.Multiple(() =>
        {
            Assert.That(result.UpsertedCount, Is.EqualTo(0));
            Assert.That(result.SkippedCount, Is.EqualTo(1));
        });
        _mockEmbedding.Verify(x => x.GetEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
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
            .Setup(x => x.GetEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new float[] { 0.1f } });

        var result = await _service.IndexListingsAsync(new[] { listing });

        Assert.That(result.UpsertedCount, Is.EqualTo(1));
        _mockPinecone.Verify(x => x.UpsertAsync(
            It.Is<UpsertRequest>(r => r.Vectors.Count() == 1 && r.Vectors.First().Id == "123"),
            It.IsAny<CancellationToken>()), Times.Once);
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
        Assert.ThrowsAsync<ArgumentException>(async () => await _service.SearchAsync(query));
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
            .Setup(x => x.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryEmbedding);

        _mockPinecone
            .Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse
            {
                Matches = new List<ScoredVector> { new() { Id = "test-id", Score = score } }
            });

        var result = await _service.SearchAsync("test query");

        Assert.That(result.Hits.Any(h => h.ListingId == "test-id"), Is.EqualTo(shouldBeIncluded));
    }

    [Test]
    public async Task Should_exclude_self_when_finding_similar()
    {
        _mockPinecone
            .Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse
            {
                Matches = new List<ScoredVector>
                {
                    new() { Id = "source-listing", Score = 1.0f },
                    new() { Id = "similar-listing", Score = 0.9f }
                }
            });

        var result = await _service.FindSimilarAsync("source-listing");

        Assert.Multiple(() =>
        {
            Assert.That(result.Hits, Has.Count.EqualTo(1));
            Assert.That(result.Hits[0].ListingId, Is.EqualTo("similar-listing"));
        });
    }

    [Test]
    public async Task Should_not_call_delete_when_list_is_empty()
    {
        await _service.DeleteAsync(Array.Empty<string>());

        _mockPinecone.Verify(x => x.DeleteAsync(It.IsAny<DeleteRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Should_call_delete_with_correct_ids()
    {
        var ids = new[] { "id1", "id2" };

        await _service.DeleteAsync(ids);

        _mockPinecone.Verify(x => x.DeleteAsync(
            It.Is<DeleteRequest>(r => r.Ids.SequenceEqual(ids)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Should_return_true_when_listing_exists()
    {
        _mockPinecone
            .Setup(x => x.FetchAsync(It.IsAny<FetchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FetchResponse
            {
                Vectors = new Dictionary<string, Vector> { ["listing-123"] = new Vector { Id = "listing-123" } }
            });

        var exists = await _service.ExistsAsync("listing-123");

        Assert.That(exists, Is.True);
    }

    [Test]
    public async Task Should_return_false_when_listing_does_not_exist()
    {
        _mockPinecone
            .Setup(x => x.FetchAsync(It.IsAny<FetchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FetchResponse
            {
                Vectors = new Dictionary<string, Vector>()
            });

        var exists = await _service.ExistsAsync("non-existent");

        Assert.That(exists, Is.False);
    }

    [Test]
    public async Task Should_use_config_topk_when_not_specified()
    {
        var queryEmbedding = new float[] { 0.1f };
        _mockEmbedding.Setup(x => x.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryEmbedding);
        _mockPinecone.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Matches = new List<ScoredVector>() });

        await _service.SearchAsync("test");

        _mockPinecone.Verify(x => x.QueryAsync(
            It.Is<QueryRequest>(r => r.TopK == 5),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Should_override_topk_when_specified()
    {
        var queryEmbedding = new float[] { 0.1f };
        _mockEmbedding.Setup(x => x.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryEmbedding);
        _mockPinecone.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Matches = new List<ScoredVector>() });

        await _service.SearchAsync("test", topK: 20);

        _mockPinecone.Verify(x => x.QueryAsync(
            It.Is<QueryRequest>(r => r.TopK == 20),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Should_pass_filter_to_pinecone_when_filtering_by_listing_ids()
    {
        var queryEmbedding = new float[] { 0.1f };
        _mockEmbedding.Setup(x => x.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryEmbedding);
        _mockPinecone.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Matches = new List<ScoredVector>() });

        var filterIds = new[] { "id1", "id2", "id3" };
        await _service.SearchAsync("test", filterToListingIds: filterIds);

        _mockPinecone.Verify(x => x.QueryAsync(
            It.Is<QueryRequest>(r => r.Filter != null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Should_not_pass_filter_when_no_listing_ids_specified()
    {
        var queryEmbedding = new float[] { 0.1f };
        _mockEmbedding.Setup(x => x.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryEmbedding);
        _mockPinecone.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Matches = new List<ScoredVector>() });

        await _service.SearchAsync("test");

        _mockPinecone.Verify(x => x.QueryAsync(
            It.Is<QueryRequest>(r => r.Filter == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Should_batch_upserts_according_to_config()
    {
        var config = new PineconeConfig("test-key", "test-index", UpsertBatchSize: 2);
        var service = new SemanticSearchService(config, _mockPinecone.Object, _mockEmbedding.Object, _mockLogger.Object);

        var listings = new[]
        {
            new Listing { ListingId = "1", Title = "Item 1" },
            new Listing { ListingId = "2", Title = "Item 2" },
            new Listing { ListingId = "3", Title = "Item 3" }
        };

        _mockEmbedding
            .Setup(x => x.GetEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _) =>
                texts.Select(_ => new float[] { 0.1f }).ToArray());

        var result = await service.IndexListingsAsync(listings);

        Assert.That(result.UpsertedCount, Is.EqualTo(3));
        _mockPinecone.Verify(x => x.UpsertAsync(It.IsAny<UpsertRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task Should_continue_processing_and_capture_errors_when_batch_fails()
    {
        var config = new PineconeConfig("test-key", "test-index", UpsertBatchSize: 1);
        var service = new SemanticSearchService(config, _mockPinecone.Object, _mockEmbedding.Object, _mockLogger.Object);

        var listings = new[]
        {
            new Listing { ListingId = "1", Title = "Item 1" },
            new Listing { ListingId = "2", Title = "Item 2" },
            new Listing { ListingId = "3", Title = "Item 3" }
        };

        _mockEmbedding
            .Setup(x => x.GetEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new float[] { 0.1f } });

        var callCount = 0;
        _mockPinecone
            .Setup(x => x.UpsertAsync(It.IsAny<UpsertRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 2)
                    throw new Exception("Simulated Pinecone failure");
                return Task.CompletedTask;
            });

        var result = await service.IndexListingsAsync(listings);

        Assert.Multiple(() =>
        {
            Assert.That(result.UpsertedCount, Is.EqualTo(2));
            Assert.That(result.Errors, Has.Count.EqualTo(1));
            Assert.That(result.Errors[0], Does.Contain("Simulated Pinecone failure"));
        });
        _mockPinecone.Verify(x => x.UpsertAsync(It.IsAny<UpsertRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
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
            .Setup(x => x.GetEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _) =>
                texts.Select(_ => new float[] { 0.1f }).ToArray());

        var result = await _service.IndexListingsAsync(listings);

        Assert.Multiple(() =>
        {
            Assert.That(result.UpsertedCount, Is.EqualTo(2));
            Assert.That(result.SkippedCount, Is.EqualTo(1));
        });
        _mockPinecone.Verify(x => x.UpsertAsync(
            It.Is<UpsertRequest>(r => r.Vectors.Count() == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Should_request_topk_plus_one_when_finding_similar()
    {
        _mockPinecone
            .Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Matches = new List<ScoredVector>() });

        await _service.FindSimilarAsync("source-id", topK: 5);

        _mockPinecone.Verify(x => x.QueryAsync(
            It.Is<QueryRequest>(r => r.TopK == 6),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Should_return_empty_hits_when_matches_is_null()
    {
        var queryEmbedding = new float[] { 0.1f };
        _mockEmbedding.Setup(x => x.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryEmbedding);
        _mockPinecone.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Matches = null });

        var result = await _service.SearchAsync("test");

        Assert.That(result.Hits, Is.Empty);
    }
}
