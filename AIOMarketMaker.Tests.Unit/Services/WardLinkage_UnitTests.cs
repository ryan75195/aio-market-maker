using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class WardLinkage_UnitTests
{
    [Test]
    public void Should_compute_condensed_distance_matrix()
    {
        // 3 vectors in 2D space
        var vectors = new[]
        {
            new float[] { 0f, 0f },
            new float[] { 1f, 0f },
            new float[] { 0f, 1f },
        };

        var distances = WardLinkage.ComputeDistanceMatrix(vectors);

        // Condensed matrix has n*(n-1)/2 = 3 entries
        Assert.That(distances, Has.Length.EqualTo(3));
        // d(0,1) = 1.0, d(0,2) = 1.0, d(1,2) = sqrt(2)
        Assert.That(distances[0], Is.EqualTo(1.0).Within(0.001)); // (0,1)
        Assert.That(distances[1], Is.EqualTo(1.0).Within(0.001)); // (0,2)
        Assert.That(distances[2], Is.EqualTo(Math.Sqrt(2)).Within(0.001)); // (1,2)
    }

    [Test]
    public void Should_build_linkage_matrix_with_correct_shape()
    {
        // 4 points
        var vectors = new[]
        {
            new float[] { 0f, 0f },
            new float[] { 0.1f, 0f },
            new float[] { 5f, 0f },
            new float[] { 5.1f, 0f },
        };

        var distances = WardLinkage.ComputeDistanceMatrix(vectors);
        var linkageMatrix = WardLinkage.BuildLinkage(distances, 4);

        // Linkage matrix has n-1 rows, 4 columns: [cluster_i, cluster_j, distance, size]
        Assert.Multiple(() =>
        {
            Assert.That(linkageMatrix, Has.Length.EqualTo(3)); // n-1 merges
            foreach (var row in linkageMatrix)
            {
                Assert.That(row, Has.Length.EqualTo(4));
            }
        });
    }

    [Test]
    public void Should_merge_closest_pairs_first()
    {
        // Two tight pairs: {0,1} near each other, {2,3} near each other, groups far apart
        var vectors = new[]
        {
            new float[] { 0f, 0f },
            new float[] { 0.1f, 0f },
            new float[] { 10f, 0f },
            new float[] { 10.1f, 0f },
        };

        var distances = WardLinkage.ComputeDistanceMatrix(vectors);
        var linkageMatrix = WardLinkage.BuildLinkage(distances, 4);

        // First two merges should be the tight pairs (distance ~0.1)
        // Third merge joins the two groups (distance ~10)
        Assert.Multiple(() =>
        {
            Assert.That(linkageMatrix[0][2], Is.LessThan(1.0), "First merge should be a tight pair");
            Assert.That(linkageMatrix[1][2], Is.LessThan(1.0), "Second merge should be a tight pair");
            Assert.That(linkageMatrix[2][2], Is.GreaterThan(5.0), "Third merge should be far apart");
        });
    }

    [Test]
    public void Should_cut_dendrogram_into_two_clusters()
    {
        // Two tight pairs far apart
        var vectors = new[]
        {
            new float[] { 0f, 0f },
            new float[] { 0.1f, 0f },
            new float[] { 10f, 0f },
            new float[] { 10.1f, 0f },
        };

        var distances = WardLinkage.ComputeDistanceMatrix(vectors);
        var linkageMatrix = WardLinkage.BuildLinkage(distances, 4);

        // Cut at threshold between tight-pair distance and inter-group distance
        var labels = WardLinkage.CutDendrogram(linkageMatrix, threshold: 2.0, n: 4);

        Assert.Multiple(() =>
        {
            Assert.That(labels, Has.Length.EqualTo(4));
            // Points 0 and 1 should be in the same cluster
            Assert.That(labels[0], Is.EqualTo(labels[1]));
            // Points 2 and 3 should be in the same cluster
            Assert.That(labels[2], Is.EqualTo(labels[3]));
            // The two groups should be in different clusters
            Assert.That(labels[0], Is.Not.EqualTo(labels[2]));
        });
    }

    [Test]
    public void Should_put_all_in_one_cluster_with_high_threshold()
    {
        var vectors = new[]
        {
            new float[] { 0f, 0f },
            new float[] { 1f, 0f },
            new float[] { 2f, 0f },
        };

        var distances = WardLinkage.ComputeDistanceMatrix(vectors);
        var linkageMatrix = WardLinkage.BuildLinkage(distances, 3);
        var labels = WardLinkage.CutDendrogram(linkageMatrix, threshold: 100.0, n: 3);

        // All in one cluster
        Assert.That(labels[0], Is.EqualTo(labels[1]));
        Assert.That(labels[1], Is.EqualTo(labels[2]));
    }

    [Test]
    public void Should_put_each_in_own_cluster_with_zero_threshold()
    {
        var vectors = new[]
        {
            new float[] { 0f, 0f },
            new float[] { 1f, 0f },
            new float[] { 2f, 0f },
        };

        var distances = WardLinkage.ComputeDistanceMatrix(vectors);
        var linkageMatrix = WardLinkage.BuildLinkage(distances, 3);
        var labels = WardLinkage.CutDendrogram(linkageMatrix, threshold: 0.0, n: 3);

        // Each in its own cluster
        var unique = labels.Distinct().Count();
        Assert.That(unique, Is.EqualTo(3));
    }

    [Test]
    public void Should_cluster_end_to_end()
    {
        // 6 points in 3 groups
        var vectors = new[]
        {
            new float[] { 0f, 0f },
            new float[] { 0.1f, 0.1f },
            new float[] { 5f, 5f },
            new float[] { 5.1f, 5.1f },
            new float[] { 10f, 0f },
            new float[] { 10.1f, 0.1f },
        };

        var labels = WardLinkage.Cluster(vectors, threshold: 2.0);

        Assert.Multiple(() =>
        {
            Assert.That(labels, Has.Length.EqualTo(6));
            // Group 1: 0, 1
            Assert.That(labels[0], Is.EqualTo(labels[1]));
            // Group 2: 2, 3
            Assert.That(labels[2], Is.EqualTo(labels[3]));
            // Group 3: 4, 5
            Assert.That(labels[4], Is.EqualTo(labels[5]));
            // All groups different
            Assert.That(labels[0], Is.Not.EqualTo(labels[2]));
            Assert.That(labels[0], Is.Not.EqualTo(labels[4]));
            Assert.That(labels[2], Is.Not.EqualTo(labels[4]));
        });
    }

    [Test]
    public void Should_handle_single_item()
    {
        var vectors = new[] { new float[] { 1f, 2f } };
        var labels = WardLinkage.Cluster(vectors, threshold: 1.0);

        Assert.That(labels, Is.EqualTo(new[] { 0 }));
    }

    [Test]
    public void Should_handle_two_items()
    {
        var vectors = new[]
        {
            new float[] { 0f, 0f },
            new float[] { 1f, 0f },
        };

        var labels = WardLinkage.Cluster(vectors, threshold: 2.0);

        // Should be in same cluster (distance 1.0 < threshold 2.0)
        Assert.That(labels[0], Is.EqualTo(labels[1]));
    }
}
