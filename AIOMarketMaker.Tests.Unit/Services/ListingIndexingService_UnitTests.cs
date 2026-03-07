using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ListingIndexingService_UnitTests
{
    private Mock<IEmbeddingService> _embeddingMock = null!;
    private Mock<IVectorIndex> _vectorIndexMock = null!;
    private Mock<ILogger<ListingIndexingService>> _loggerMock = null!;
    private ListingIndexingService _service = null!;

    [SetUp]
    public void Setup()
    {
        _embeddingMock = new Mock<IEmbeddingService>();
        _vectorIndexMock = new Mock<IVectorIndex>();
        _loggerMock = new Mock<ILogger<ListingIndexingService>>();
        _service = new ListingIndexingService(_embeddingMock.Object, _vectorIndexMock.Object, _loggerMock.Object);
    }

    [Test]
    public async Task Should_embed_and_upsert_when_embed_content_is_true()
    {
        var listing = CreateListing();
        var expectedEmbedding = new float[] { 0.1f, 0.2f, 0.3f };

        _embeddingMock
            .Setup(x => x.GetEmbedding("Test Item Test description", It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(expectedEmbedding);

        var result = await _service.Index(listing, embedContent: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(IndexingAction.Embedded));
            Assert.That(result.Error, Is.Null);
        });

        _embeddingMock.Verify(
            x => x.GetEmbedding("Test Item Test description", It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()),
            Times.Once);

        _vectorIndexMock.Verify(
            x => x.Upsert("ABC123", expectedEmbedding),
            Times.Once);
    }

    [Test]
    public async Task Should_skip_when_embed_content_is_false()
    {
        var listing = CreateListing();

        var result = await _service.Index(listing, embedContent: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(IndexingAction.Skipped));
            Assert.That(result.Error, Is.Null);
        });

        _embeddingMock.Verify(
            x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()),
            Times.Never);
        _vectorIndexMock.Verify(
            x => x.Upsert(It.IsAny<string>(), It.IsAny<float[]>()),
            Times.Never);
    }

    private static IEnumerable<TestCaseData> SkipCases()
    {
        yield return new TestCaseData(null, null).SetDescription("Both null");
        yield return new TestCaseData("", "").SetDescription("Both empty");
        yield return new TestCaseData("   ", "   ").SetDescription("Both whitespace");
        yield return new TestCaseData(null, "").SetDescription("Null title, empty description");
        yield return new TestCaseData("", null).SetDescription("Empty title, null description");
    }

    [TestCaseSource(nameof(SkipCases))]
    public async Task Should_skip_when_no_title_or_description(string? title, string? description)
    {
        var listing = CreateListing(title: title, description: description);

        var result = await _service.Index(listing, embedContent: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(IndexingAction.Skipped));
            Assert.That(result.Error, Is.Null);
        });

        _embeddingMock.Verify(
            x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()),
            Times.Never);
        _vectorIndexMock.Verify(
            x => x.Upsert(It.IsAny<string>(), It.IsAny<float[]>()),
            Times.Never);
    }

    // ---------------------------------------------------------------
    // Batch indexing tests
    // ---------------------------------------------------------------

    [Test]
    public async Task Should_batch_embed_multiple_listings_in_single_api_call()
    {
        var listings = new[]
        {
            CreateListing(listingId: "B1", title: "Item 1", description: "Desc 1"),
            CreateListing(listingId: "B2", title: "Item 2", description: "Desc 2"),
            CreateListing(listingId: "B3", title: "Item 3", description: "Desc 3")
        };

        _embeddingMock
            .Setup(x => x.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(new[]
            {
                new float[] { 0.1f }, new float[] { 0.2f }, new float[] { 0.3f }
            });

        var results = (await _service.IndexBatch(listings, embedContent: true)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(results.All(r => r.Action == IndexingAction.Embedded), Is.True);
        });

        // Single batch API call, not 3 individual calls
        _embeddingMock.Verify(
            x => x.GetEmbeddings(It.Is<IEnumerable<string>>(t => t.Count() == 3), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()),
            Times.Once);
        _embeddingMock.Verify(
            x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()),
            Times.Never);

        _vectorIndexMock.Verify(x => x.Upsert("B1", It.IsAny<float[]>()), Times.Once);
        _vectorIndexMock.Verify(x => x.Upsert("B2", It.IsAny<float[]>()), Times.Once);
        _vectorIndexMock.Verify(x => x.Upsert("B3", It.IsAny<float[]>()), Times.Once);
    }

    [Test]
    public async Task Should_skip_all_listings_when_embed_content_false_for_batch()
    {
        var listings = new[]
        {
            CreateListing(listingId: "S1"),
            CreateListing(listingId: "S2")
        };

        var results = (await _service.IndexBatch(listings, embedContent: false)).ToList();

        Assert.That(results.All(r => r.Action == IndexingAction.Skipped), Is.True);

        _embeddingMock.Verify(
            x => x.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()),
            Times.Never);
    }

    [Test]
    public async Task Should_skip_listings_without_embeddable_text_in_batch()
    {
        var listings = new[]
        {
            CreateListing(listingId: "HAS_TEXT", title: "Real Title", description: "Real Desc"),
            CreateListing(listingId: "NO_TEXT", title: null, description: null),
            CreateListing(listingId: "EMPTY", title: "", description: "")
        };

        _embeddingMock
            .Setup(x => x.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(new[] { new float[] { 0.5f } });

        var results = (await _service.IndexBatch(listings, embedContent: true)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Action, Is.EqualTo(IndexingAction.Embedded));
            Assert.That(results[1].Action, Is.EqualTo(IndexingAction.Skipped));
            Assert.That(results[2].Action, Is.EqualTo(IndexingAction.Skipped));
        });

        // Only 1 text sent to batch API (the listing with real content)
        _embeddingMock.Verify(
            x => x.GetEmbeddings(It.Is<IEnumerable<string>>(t => t.Count() == 1), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()),
            Times.Once);
    }

    [Test]
    public async Task Should_fallback_to_individual_calls_when_batch_fails()
    {
        var listings = new[]
        {
            CreateListing(listingId: "FB1", title: "Item 1"),
            CreateListing(listingId: "FB2", title: "Item 2")
        };

        // Batch call fails
        _embeddingMock
            .Setup(x => x.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ThrowsAsync(new HttpRequestException("Rate limited"));

        // Individual calls succeed
        _embeddingMock
            .Setup(x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(new float[] { 0.1f });

        var results = (await _service.IndexBatch(listings, embedContent: true)).ToList();

        Assert.That(results.Count(r => r.Action == IndexingAction.Embedded), Is.EqualTo(2));

        // Should have used individual fallback
        _embeddingMock.Verify(
            x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()),
            Times.Exactly(2));
    }

    [Test]
    public async Task Should_return_empty_results_for_empty_batch()
    {
        var results = (await _service.IndexBatch(Enumerable.Empty<Listing>(), embedContent: true)).ToList();

        Assert.That(results, Is.Empty);

        _embeddingMock.Verify(
            x => x.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()),
            Times.Never);
    }

    [Test]
    public async Task Should_mark_failed_when_individual_fallback_also_fails()
    {
        var listings = new[] { CreateListing(listingId: "FAIL1", title: "Item") };

        _embeddingMock
            .Setup(x => x.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ThrowsAsync(new HttpRequestException("Batch failed"));
        _embeddingMock
            .Setup(x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ThrowsAsync(new HttpRequestException("Individual also failed"));

        var results = (await _service.IndexBatch(listings, embedContent: true)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Action, Is.EqualTo(IndexingAction.Failed));
            Assert.That(results[0].Error, Does.Contain("Individual also failed"));
        });
    }

    // ---------------------------------------------------------------
    // BuildEmbeddingText tests
    // ---------------------------------------------------------------

    [Test]
    public void Should_combine_title_and_description_for_embedding_text()
    {
        var listing = CreateListing(title: "PS5 Console", description: "Brand new PlayStation 5");
        var text = ListingIndexingService.BuildEmbeddingText(listing);
        Assert.That(text, Is.EqualTo("PS5 Console Brand new PlayStation 5"));
    }

    [Test]
    public void Should_use_title_only_when_description_is_null()
    {
        var listing = CreateListing(title: "PS5 Console", description: null);
        var text = ListingIndexingService.BuildEmbeddingText(listing);
        Assert.That(text, Is.EqualTo("PS5 Console"));
    }

    private static Listing CreateListing(
        string listingId = "ABC123", int scrapeJobId = 1,
        string? title = "Test Item", string? description = "Test description",
        decimal? price = 99.99m, decimal? shippingCost = 5m,
        string? condition = "NEW", string? listingStatus = "Active",
        string? purchaseFormat = "BuyItNow") =>
        new()
        {
            ListingId = listingId,
            ScrapeJobId = scrapeJobId,
            Title = title,
            Description = description,
            Price = price,
            ShippingCost = shippingCost,
            Condition = condition,
            ListingStatus = listingStatus,
            PurchaseFormat = purchaseFormat,
            CreatedUtc = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc)
        };
}
