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
            .Setup(x => x.GetEmbedding("Test Item Test description", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedEmbedding);

        var result = await _service.Index(listing, embedContent: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(IndexingAction.Embedded));
            Assert.That(result.Error, Is.Null);
        });

        _embeddingMock.Verify(
            x => x.GetEmbedding("Test Item Test description", It.IsAny<CancellationToken>()),
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
            x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>()),
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
            x => x.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _vectorIndexMock.Verify(
            x => x.Upsert(It.IsAny<string>(), It.IsAny<float[]>()),
            Times.Never);
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
