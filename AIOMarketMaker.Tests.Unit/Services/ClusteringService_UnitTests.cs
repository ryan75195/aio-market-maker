using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ClusteringService_UnitTests
{
    private Mock<ILogger<ClusteringService>> _mockLogger = null!;
    private Mock<ITfIdfVectorizer> _mockTfIdf = null!;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<ClusteringService>>();
        _mockTfIdf = new Mock<ITfIdfVectorizer>();
    }

    [Test]
    public void Cluster_WithEmptyList_ReturnsEmptyResult()
    {
        var config = new ClusteringConfig();
        var service = new ClusteringService(config, _mockTfIdf.Object, _mockLogger.Object);
        var result = service.Cluster(Array.Empty<EmbeddingWithId>());
        Assert.Multiple(() =>
        {
            Assert.That(result.Clusters, Is.Empty);
            Assert.That(result.Noise, Is.Empty);
        });
    }

    [Test]
    public void Cluster_WithSingleItem_PreservesItem()
    {
        var config = new ClusteringConfig();
        var service = new ClusteringService(config, _mockTfIdf.Object, _mockLogger.Object);
        var items = new[] { new EmbeddingWithId(1, CreateRandomEmbedding(10)) };
        var result = service.Cluster(items);
        var allItems = result.Clusters.SelectMany(c => c.Items).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(allItems, Has.Count.EqualTo(1));
            Assert.That(allItems[0].Id, Is.EqualTo(1));
            Assert.That(result.Noise, Is.Empty);
        });
    }

    [Test]
    public void Cluster_WithMultipleItems_PreservesAllItems()
    {
        var config = new ClusteringConfig();
        var service = new ClusteringService(config, _mockTfIdf.Object, _mockLogger.Object);
        var items = Enumerable.Range(1, 10)
            .Select(i => new EmbeddingWithId(i, CreateRandomEmbedding(10)))
            .ToArray();
        var result = service.Cluster(items);
        var allItems = result.Clusters.SelectMany(c => c.Items).ToList();
        var allIds = allItems.Select(i => i.Id).ToHashSet();
        Assert.Multiple(() =>
        {
            Assert.That(allItems, Has.Count.EqualTo(10));
            Assert.That(result.Noise, Is.Empty);
            for (int i = 1; i <= 10; i++)
            {
                Assert.That(allIds, Contains.Item(i), $"ID {i} should be preserved");
            }
        });
    }

    [Test]
    public void ClusteringConfig_DefaultValues_AreCorrect()
    {
        var config = new ClusteringConfig();
        Assert.Multiple(() =>
        {
            Assert.That(config.MinClusterSize, Is.EqualTo(5));
            Assert.That(config.MinPoints, Is.EqualTo(3));
            Assert.That(config.Threshold, Is.EqualTo(1.5));
        });
    }

    [Test]
    public void ClusteringConfig_CustomValues_ArePreserved()
    {
        var config = new ClusteringConfig(MinClusterSize: 10, MinPoints: 5, Threshold: 2.5);
        Assert.Multiple(() =>
        {
            Assert.That(config.MinClusterSize, Is.EqualTo(10));
            Assert.That(config.MinPoints, Is.EqualTo(5));
            Assert.That(config.Threshold, Is.EqualTo(2.5));
        });
    }

    [Test]
    public void Cluster_WithSimilarEmbeddings_ProducesTwoClusters()
    {
        var config = new ClusteringConfig();
        var service = new ClusteringService(config, _mockTfIdf.Object, _mockLogger.Object);

        var baseEmbedding1 = new float[] { 1f, 0f, 0f, 0f, 0f };
        var items = new List<EmbeddingWithId>
        {
            new(1, AddNoise(baseEmbedding1, 0.05f)),
            new(2, AddNoise(baseEmbedding1, 0.05f)),
            new(3, AddNoise(baseEmbedding1, 0.05f)),
            new(4, AddNoise(baseEmbedding1, 0.05f)),
            new(5, AddNoise(baseEmbedding1, 0.05f)),
        };

        var baseEmbedding2 = new float[] { 0f, 0f, 1f, 0f, 0f };
        items.AddRange(new[]
        {
            new EmbeddingWithId(6, AddNoise(baseEmbedding2, 0.05f)),
            new EmbeddingWithId(7, AddNoise(baseEmbedding2, 0.05f)),
            new EmbeddingWithId(8, AddNoise(baseEmbedding2, 0.05f)),
            new EmbeddingWithId(9, AddNoise(baseEmbedding2, 0.05f)),
            new EmbeddingWithId(10, AddNoise(baseEmbedding2, 0.05f)),
        });

        var result = service.Cluster(items);

        Assert.Multiple(() =>
        {
            Assert.That(result.Noise, Is.Empty, "Ward clustering produces no noise");
            Assert.That(result.Clusters, Has.Count.EqualTo(2), "Should find 2 distinct groups");
            Assert.That(result.Clusters.SelectMany(c => c.Items).ToList(), Has.Count.EqualTo(10), "All items should be in clusters");

            // Verify the two groups are separated correctly
            var group1Ids = new HashSet<int> { 1, 2, 3, 4, 5 };
            var group2Ids = new HashSet<int> { 6, 7, 8, 9, 10 };

            foreach (var cluster in result.Clusters)
            {
                var clusterIds = cluster.Items.Select(i => i.Id).ToHashSet();
                var isGroup1 = clusterIds.IsSubsetOf(group1Ids);
                var isGroup2 = clusterIds.IsSubsetOf(group2Ids);
                Assert.That(isGroup1 || isGroup2, Is.True,
                    $"Cluster should contain items from only one group, got IDs: {string.Join(",", clusterIds)}");
            }
        });
    }

    [Test]
    public void ClusterResult_PreservesIds()
    {
        var config = new ClusteringConfig();
        var service = new ClusteringService(config, _mockTfIdf.Object, _mockLogger.Object);

        var items = new[]
        {
            new EmbeddingWithId(42, new float[] { 1f, 0f, 0f }),
            new EmbeddingWithId(99, new float[] { 1f, 0.01f, 0f }),
        };

        var result = service.Cluster(items);
        var allIds = result.Clusters
            .SelectMany(c => c.Items)
            .Select(e => e.Id)
            .ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(allIds, Contains.Item(42));
            Assert.That(allIds, Contains.Item(99));
            Assert.That(result.Noise, Is.Empty);
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
        if (magnitude == 0)
        {
            return embedding;
        }
        return embedding.Select(x => x / magnitude).ToArray();
    }
}
