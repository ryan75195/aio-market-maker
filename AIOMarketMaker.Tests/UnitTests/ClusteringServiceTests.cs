using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace AIOMarketMaker.Tests.Unit;

[TestFixture]
public class ClusteringServiceTests
{
    private Mock<ILogger<ClusteringService>> _mockLogger = null!;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<ClusteringService>>();
    }

    [Test]
    public void Cluster_WithEmptyList_ReturnsEmptyResult()
    {
        // Arrange
        var config = new ClusteringConfig();
        var service = new ClusteringService(config, _mockLogger.Object);

        // Act
        var result = service.Cluster(Array.Empty<EmbeddingWithId>());

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Clusters, Is.Empty);
            Assert.That(result.Noise, Is.Empty);
        });
    }

    [Test]
    public void Cluster_WithSingleItem_PreservesItem()
    {
        // Arrange
        var config = new ClusteringConfig(MinClusterSize: 2);
        var service = new ClusteringService(config, _mockLogger.Object);
        var items = new[] { new EmbeddingWithId(1, CreateRandomEmbedding(10)) };

        // Act
        var result = service.Cluster(items);

        // Assert - item should be somewhere (cluster or noise)
        var allItems = result.Clusters.SelectMany(c => c.Items).Concat(result.Noise).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(allItems, Has.Count.EqualTo(1));
            Assert.That(allItems[0].Id, Is.EqualTo(1));
        });
    }

    [Test]
    public void Cluster_WithMultipleItems_PreservesAllItems()
    {
        // Arrange
        var config = new ClusteringConfig(MinClusterSize: 3, MinPoints: 2);
        var service = new ClusteringService(config, _mockLogger.Object);
        var items = Enumerable.Range(1, 10)
            .Select(i => new EmbeddingWithId(i, CreateRandomEmbedding(10)))
            .ToArray();

        // Act
        var result = service.Cluster(items);

        // Assert - all items should be preserved
        var allItems = result.Clusters.SelectMany(c => c.Items).Concat(result.Noise).ToList();
        var allIds = allItems.Select(i => i.Id).ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(allItems, Has.Count.EqualTo(10));
            for (int i = 1; i <= 10; i++)
            {
                Assert.That(allIds, Contains.Item(i), $"ID {i} should be preserved");
            }
        });
    }

    [Test]
    public void ClusteringConfig_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var config = new ClusteringConfig();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(config.MinClusterSize, Is.EqualTo(5));
            Assert.That(config.MinPoints, Is.EqualTo(3));
        });
    }

    [Test]
    public void ClusteringConfig_CustomValues_ArePreserved()
    {
        // Arrange & Act
        var config = new ClusteringConfig(MinClusterSize: 10, MinPoints: 5);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(config.MinClusterSize, Is.EqualTo(10));
            Assert.That(config.MinPoints, Is.EqualTo(5));
        });
    }

    [Test]
    public void Cluster_WithSimilarEmbeddings_ProducesValidResult()
    {
        // Arrange - create two distinct groups of similar embeddings
        var config = new ClusteringConfig(MinClusterSize: 3, MinPoints: 2);
        var service = new ClusteringService(config, _mockLogger.Object);

        // Group 1: embeddings pointing roughly in same direction
        var baseEmbedding1 = new float[] { 1f, 0f, 0f, 0f, 0f };
        var items = new List<EmbeddingWithId>
        {
            new(1, AddNoise(baseEmbedding1, 0.05f)),
            new(2, AddNoise(baseEmbedding1, 0.05f)),
            new(3, AddNoise(baseEmbedding1, 0.05f)),
            new(4, AddNoise(baseEmbedding1, 0.05f)),
            new(5, AddNoise(baseEmbedding1, 0.05f)),
        };

        // Group 2: embeddings pointing in different direction
        var baseEmbedding2 = new float[] { 0f, 0f, 1f, 0f, 0f };
        items.AddRange(new[]
        {
            new EmbeddingWithId(6, AddNoise(baseEmbedding2, 0.05f)),
            new EmbeddingWithId(7, AddNoise(baseEmbedding2, 0.05f)),
            new EmbeddingWithId(8, AddNoise(baseEmbedding2, 0.05f)),
            new EmbeddingWithId(9, AddNoise(baseEmbedding2, 0.05f)),
            new EmbeddingWithId(10, AddNoise(baseEmbedding2, 0.05f)),
        });

        // Act
        var result = service.Cluster(items);

        // Assert - all items should be preserved and cluster labels should be valid
        var allItems = result.Clusters.SelectMany(c => c.Items).Concat(result.Noise).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(allItems, Has.Count.EqualTo(10), "All items should be preserved");
            Assert.That(result.Clusters.All(c => c.Label >= 0), Is.True, "Cluster labels should be non-negative");
        });
    }

    [Test]
    public void ClusterResult_PreservesIds()
    {
        // Arrange
        var config = new ClusteringConfig(MinClusterSize: 2, MinPoints: 1);
        var service = new ClusteringService(config, _mockLogger.Object);

        var items = new[]
        {
            new EmbeddingWithId(42, new float[] { 1f, 0f, 0f }),
            new EmbeddingWithId(99, new float[] { 1f, 0.01f, 0f }),
        };

        // Act
        var result = service.Cluster(items);

        // Assert - IDs should be preserved in either clusters or noise
        var allIds = result.Clusters
            .SelectMany(c => c.Items)
            .Concat(result.Noise)
            .Select(e => e.Id)
            .ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(allIds, Contains.Item(42));
            Assert.That(allIds, Contains.Item(99));
        });
    }

    private static float[] CreateRandomEmbedding(int dimensions)
    {
        var random = new Random();
        var embedding = new float[dimensions];
        for (int i = 0; i < dimensions; i++)
        {
            embedding[i] = (float)random.NextDouble();
        }
        return Normalize(embedding);
    }

    private static float[] AddNoise(float[] baseEmbedding, float noiseLevel)
    {
        var random = new Random();
        var result = new float[baseEmbedding.Length];
        for (int i = 0; i < baseEmbedding.Length; i++)
        {
            result[i] = baseEmbedding[i] + (float)(random.NextDouble() - 0.5) * noiseLevel;
        }
        return Normalize(result);
    }

    private static float[] Normalize(float[] embedding)
    {
        var magnitude = MathF.Sqrt(embedding.Sum(x => x * x));
        if (magnitude == 0) return embedding;
        return embedding.Select(x => x / magnitude).ToArray();
    }
}
