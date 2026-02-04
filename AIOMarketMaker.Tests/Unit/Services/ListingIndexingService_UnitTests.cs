using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Pinecone;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ListingIndexingService_UnitTests
{
    private Mock<IEmbeddingService> _embeddingMock = null!;
    private Mock<IPineconeIndexClient> _pineconeMock = null!;
    private Mock<ILogger<ListingIndexingService>> _loggerMock = null!;
    private ListingIndexingService _service = null!;

    [SetUp]
    public void Setup()
    {
        _embeddingMock = new Mock<IEmbeddingService>();
        _pineconeMock = new Mock<IPineconeIndexClient>();
        _loggerMock = new Mock<ILogger<ListingIndexingService>>();
        _service = new ListingIndexingService(_embeddingMock.Object, _pineconeMock.Object, _loggerMock.Object);
    }

    [Test]
    public async Task Should_embed_and_upsert_when_new()
    {
        var listing = CreateListing();
        var expectedEmbedding = new float[] { 0.1f, 0.2f, 0.3f };

        _embeddingMock
            .Setup(x => x.GetEmbedding("Test Item Test description", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedEmbedding);

        UpsertRequest? capturedRequest = null;
        _pineconeMock
            .Setup(x => x.Upsert(It.IsAny<UpsertRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpsertRequest, CancellationToken>((req, _) => capturedRequest = req)
            .Returns(Task.CompletedTask);

        var result = await _service.Index(listing, isNew: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(IndexingAction.Embedded));
            Assert.That(result.Error, Is.Null);
        });

        _embeddingMock.Verify(
            x => x.GetEmbedding("Test Item Test description", It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.That(capturedRequest, Is.Not.Null);
        var vector = capturedRequest!.Vectors.First();
        Assert.Multiple(() =>
        {
            Assert.That(vector.Id, Is.EqualTo("ABC123"));
            Assert.That(vector.Values?.ToArray(), Is.EqualTo(expectedEmbedding));
        });
    }

    [Test]
    public async Task Should_update_metadata_only_when_not_new()
    {
        var listing = CreateListing();

        UpdateRequest? capturedRequest = null;
        _pineconeMock
            .Setup(x => x.Update(It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpdateRequest, CancellationToken>((req, _) => capturedRequest = req)
            .Returns(Task.CompletedTask);

        var result = await _service.Index(listing, isNew: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(IndexingAction.MetadataUpdated));
            Assert.That(result.Error, Is.Null);
        });

        _embeddingMock.Verify(
            x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        Assert.That(capturedRequest, Is.Not.Null);
        Assert.That(capturedRequest!.Id, Is.EqualTo("ABC123"));
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

        var result = await _service.Index(listing, isNew: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(IndexingAction.Skipped));
            Assert.That(result.Error, Is.Null);
        });

        _embeddingMock.Verify(
            x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _pineconeMock.Verify(
            x => x.Upsert(It.IsAny<UpsertRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _pineconeMock.Verify(
            x => x.Update(It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Should_include_all_metadata_fields_in_upsert()
    {
        var listing = CreateListing();
        var expectedEmbedding = new float[] { 0.1f };

        _embeddingMock
            .Setup(x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedEmbedding);

        Metadata? capturedMetadata = null;
        _pineconeMock
            .Setup(x => x.Upsert(It.IsAny<UpsertRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpsertRequest, CancellationToken>((req, _) =>
                capturedMetadata = req.Vectors.First().Metadata)
            .Returns(Task.CompletedTask);

        await _service.Index(listing, isNew: true);

        Assert.That(capturedMetadata, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(capturedMetadata!["listingId"].AsT0, Is.EqualTo("ABC123"));
            Assert.That(capturedMetadata["scrapeJobId"].AsT1, Is.EqualTo(1.0));
            Assert.That(capturedMetadata["condition"].AsT0, Is.EqualTo("NEW"));
            Assert.That(capturedMetadata["listingStatus"].AsT0, Is.EqualTo("Active"));
            Assert.That(capturedMetadata["purchaseFormat"].AsT0, Is.EqualTo("BuyItNow"));
            Assert.That(capturedMetadata["soldDateUtc"].AsT0, Is.EqualTo(""));
            Assert.That(capturedMetadata["createdUtc"].AsT0, Is.EqualTo("2026-01-15T10:00:00.0000000Z"));
            Assert.That(capturedMetadata["price"].AsT1, Is.EqualTo(99.99));
            Assert.That(capturedMetadata["shippingCost"].AsT1, Is.EqualTo(5.0));
        });
    }

    [Test]
    public async Task Should_omit_price_when_null()
    {
        var listing = CreateListing(price: null, shippingCost: null);

        _embeddingMock
            .Setup(x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 0.1f });

        Metadata? capturedMetadata = null;
        _pineconeMock
            .Setup(x => x.Upsert(It.IsAny<UpsertRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpsertRequest, CancellationToken>((req, _) =>
                capturedMetadata = req.Vectors.First().Metadata)
            .Returns(Task.CompletedTask);

        await _service.Index(listing, isNew: true);

        Assert.That(capturedMetadata, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(capturedMetadata!.ContainsKey("price"), Is.False);
            Assert.That(capturedMetadata.ContainsKey("shippingCost"), Is.False);
        });
    }

    [Test]
    public async Task Should_default_null_condition_to_empty_string_in_metadata()
    {
        var listing = CreateListing(condition: null, listingStatus: null, purchaseFormat: null);

        _embeddingMock
            .Setup(x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 0.1f });

        Metadata? capturedMetadata = null;
        _pineconeMock
            .Setup(x => x.Upsert(It.IsAny<UpsertRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpsertRequest, CancellationToken>((req, _) =>
                capturedMetadata = req.Vectors.First().Metadata)
            .Returns(Task.CompletedTask);

        await _service.Index(listing, isNew: true);

        Assert.That(capturedMetadata, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(capturedMetadata!["condition"].AsT0, Is.EqualTo(""));
            Assert.That(capturedMetadata["listingStatus"].AsT0, Is.EqualTo(""));
            Assert.That(capturedMetadata["purchaseFormat"].AsT0, Is.EqualTo(""));
        });
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
