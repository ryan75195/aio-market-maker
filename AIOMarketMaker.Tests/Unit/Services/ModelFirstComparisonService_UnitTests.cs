using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ModelFirstComparisonService_UnitTests
{
    private Mock<IVariantClassifierClient> _classifierMock = null!;
    private Mock<IListingComparisonService> _gptFallbackMock = null!;
    private ModelFirstComparisonService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _classifierMock = new Mock<IVariantClassifierClient>();
        _gptFallbackMock = new Mock<IListingComparisonService>();
        _service = new ModelFirstComparisonService(
            _classifierMock.Object,
            _gptFallbackMock.Object,
            Mock.Of<ILogger<ModelFirstComparisonService>>());
    }

    private static Listing CreateListing(int id, string title, string? desc = null) =>
        new()
        {
            Id = id, ListingId = id.ToString(), Title = title,
            Description = desc ?? $"Desc for {title}", ScrapeJobId = 1
        };

    [Test]
    public async Task Should_return_model_verdict_when_confident()
    {
        _classifierMock
            .Setup(c => c.Classify(It.IsAny<IEnumerable<ClassifyPairRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PairResult(true, 0.95f, false) });

        var result = await _service.Compare(
            CreateListing(1, "Dyson V15 Detect Absolute"),
            CreateListing(2, "Dyson V15 Detect Absolute New"));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.True);
            Assert.That(result.Explanation, Does.Contain("0.950"));
        });
        _gptFallbackMock.Verify(
            g => g.Compare(It.IsAny<Listing>(), It.IsAny<Listing>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Should_return_not_comparable_when_model_confident_different()
    {
        _classifierMock
            .Setup(c => c.Classify(It.IsAny<IEnumerable<ClassifyPairRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PairResult(false, 0.99f, false) });

        var result = await _service.Compare(
            CreateListing(1, "Dyson V15 Detect Absolute"),
            CreateListing(2, "Dyson V15 Detect Extra"));

        Assert.That(result.IsComparable, Is.False);
        _gptFallbackMock.Verify(
            g => g.Compare(It.IsAny<Listing>(), It.IsAny<Listing>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Should_fallback_to_gpt_when_model_uncertain()
    {
        _classifierMock
            .Setup(c => c.Classify(It.IsAny<IEnumerable<ClassifyPairRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PairResult(false, 0.62f, true) });

        _gptFallbackMock
            .Setup(g => g.Compare(It.IsAny<Listing>(), It.IsAny<Listing>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComparableVerdict(true, "GPT says same product"));

        var result = await _service.Compare(
            CreateListing(1, "Dyson V15 Detect"),
            CreateListing(2, "Dyson V15 Detect Iron"));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.True);
            Assert.That(result.Explanation, Is.EqualTo("GPT says same product"));
        });
        _gptFallbackMock.Verify(
            g => g.Compare(It.IsAny<Listing>(), It.IsAny<Listing>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Should_fallback_to_gpt_when_model_service_unavailable()
    {
        _classifierMock
            .Setup(c => c.Classify(It.IsAny<IEnumerable<ClassifyPairRequest>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        _gptFallbackMock
            .Setup(g => g.Compare(It.IsAny<Listing>(), It.IsAny<Listing>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComparableVerdict(false, "GPT fallback"));

        var result = await _service.Compare(
            CreateListing(1, "Item A"),
            CreateListing(2, "Item B"));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.False);
            Assert.That(result.Explanation, Is.EqualTo("GPT fallback"));
        });
    }
}
