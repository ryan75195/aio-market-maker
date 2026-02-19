using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
public class EmbeddingService_UnitTests
{
    private Mock<ILogger<EmbeddingService>> _mockLogger = null!;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<EmbeddingService>>();
    }

    [Test]
    public void GetEmbedding_WithNullText_ThrowsArgumentException()
    {
        // Arrange
        var config = new EmbeddingConfig("fake-api-key");
        var service = new EmbeddingService(config, _mockLogger.Object);

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GetEmbedding(null!));
    }

    [Test]
    public void GetEmbedding_WithEmptyText_ThrowsArgumentException()
    {
        // Arrange
        var config = new EmbeddingConfig("fake-api-key");
        var service = new EmbeddingService(config, _mockLogger.Object);

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GetEmbedding(""));
    }

    [Test]
    public void GetEmbedding_WithWhitespaceText_ThrowsArgumentException()
    {
        // Arrange
        var config = new EmbeddingConfig("fake-api-key");
        var service = new EmbeddingService(config, _mockLogger.Object);

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.GetEmbedding("   "));
    }

    [Test]
    public async Task GetEmbeddings_WithEmptyList_ReturnsEmptyArray()
    {
        // Arrange
        var config = new EmbeddingConfig("fake-api-key");
        var service = new EmbeddingService(config, _mockLogger.Object);

        // Act
        var result = await service.GetEmbeddings(Array.Empty<string>());

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetEmbeddings_WithAllEmptyStrings_ReturnsEmptyArray()
    {
        // Arrange
        var config = new EmbeddingConfig("fake-api-key");
        var service = new EmbeddingService(config, _mockLogger.Object);

        // Act
        var result = await service.GetEmbeddings(new[] { "", "  ", null! });

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void EmbeddingConfig_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var config = new EmbeddingConfig("test-key");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(config.ApiKey, Is.EqualTo("test-key"));
            Assert.That(config.Model, Is.EqualTo("text-embedding-3-small"));
            Assert.That(config.Dimensions, Is.EqualTo(1536));
        });
    }

    [Test]
    public void EmbeddingConfig_CustomValues_ArePreserved()
    {
        // Arrange & Act
        var config = new EmbeddingConfig("test-key", "text-embedding-3-large", 3072);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(config.ApiKey, Is.EqualTo("test-key"));
            Assert.That(config.Model, Is.EqualTo("text-embedding-3-large"));
            Assert.That(config.Dimensions, Is.EqualTo(3072));
        });
    }
}
