using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace AIOMarketMaker.Tests.Integration;

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
            "..", "..", "..", "..", "AIOMarketMaker.Console", "local.settings.json");

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
