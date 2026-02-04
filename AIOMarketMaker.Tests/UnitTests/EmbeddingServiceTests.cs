using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace AIOMarketMaker.Tests.Unit;

[TestFixture]
public class EmbeddingServiceTests
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

[TestFixture]
[Category("Integration")]
[Explicit("Requires valid OpenAI API key in local.settings.json")]
public class EmbeddingServiceIntegrationTests
{
    private IEmbeddingService _service = null!;

    [SetUp]
    public void Setup()
    {
        // Read API key from local.settings.json
        var configPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "AIOMarketMaker.Etl", "local.settings.json");

        if (!File.Exists(configPath))
        {
            Assert.Ignore($"local.settings.json not found at {Path.GetFullPath(configPath)}");
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: false)
            .Build();

        var apiKey = configuration.GetValue<string>("OpenAi:ApiKey");
        if (string.IsNullOrEmpty(apiKey))
        {
            Assert.Ignore("OpenAi:ApiKey not found in local.settings.json");
        }

        var model = configuration.GetValue<string>("Embedding:Model") ?? "text-embedding-3-small";
        var dimensions = configuration.GetValue<int>("Embedding:Dimensions", 1536);

        var config = new EmbeddingConfig(apiKey, model, dimensions);
        var logger = new Mock<ILogger<EmbeddingService>>();
        _service = new EmbeddingService(config, logger.Object);
    }

    [Test]
    public async Task GetEmbedding_WithValidText_ReturnsEmbedding()
    {
        // Act
        var result = await _service.GetEmbedding("PlayStation 5 disc edition console");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Length, Is.EqualTo(1536));
            Assert.That(result.Any(v => v != 0), Is.True, "Embedding should have non-zero values");
        });
    }

    [Test]
    public async Task GetEmbeddings_WithMultipleTexts_ReturnsCorrectCount()
    {
        // Arrange
        var texts = new[]
        {
            "PlayStation 5 disc edition",
            "Xbox Series X console",
            "Nintendo Switch OLED"
        };

        // Act
        var results = await _service.GetEmbeddings(texts);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(results.Length, Is.EqualTo(3));
            Assert.That(results.All(e => e.Length == 1536), Is.True);
        });
    }

    [Test]
    public async Task GetEmbeddings_SimilarTexts_HaveHighCosineSimilarity()
    {
        // Arrange
        var texts = new[]
        {
            "PlayStation 5 disc edition console",
            "PS5 disc version gaming console"
        };

        // Act
        var results = await _service.GetEmbeddings(texts);
        var similarity = CosineSimilarity(results[0], results[1]);

        // Assert - similar texts should have similarity > 0.8
        Assert.That(similarity, Is.GreaterThan(0.8),
            $"Similar texts should have high cosine similarity, got {similarity:F3}");
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}
