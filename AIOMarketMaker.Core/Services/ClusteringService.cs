using HdbscanSharp.Distance;
using HdbscanSharp.Runner;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services;

public record ClusteringConfig(
    int MinClusterSize = 5,
    int MinPoints = 3
);

public record EmbeddingWithId(int Id, float[] Embedding);

public record Cluster(
    int Label,
    List<EmbeddingWithId> Items
);

public record ClusteringResult(
    List<Cluster> Clusters,
    List<EmbeddingWithId> Noise
);

public interface IClusteringService
{
    ClusteringResult Cluster(IReadOnlyList<EmbeddingWithId> items);
}

public class ClusteringService : IClusteringService
{
    private readonly ClusteringConfig _config;
    private readonly ILogger<ClusteringService> _logger;

    public ClusteringService(ClusteringConfig config, ILogger<ClusteringService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public ClusteringResult Cluster(IReadOnlyList<EmbeddingWithId> items)
    {
        if (items.Count == 0)
        {
            return new ClusteringResult(new List<Cluster>(), new List<EmbeddingWithId>());
        }

        _logger.LogInformation("Clustering {Count} embeddings with MinClusterSize={MinClusterSize}, MinPoints={MinPoints}",
            items.Count, _config.MinClusterSize, _config.MinPoints);

        // Convert embeddings to float[][] for HDBSCAN
        var data = items.Select(e => e.Embedding).ToArray();

        // Get cosine similarity distance function
        var distanceFunc = GenericCosineSimilarity.GetFunc(data);

        // Run HDBSCAN
        var result = HdbscanRunner.Run(
            data.Length,
            _config.MinPoints,
            _config.MinClusterSize,
            distanceFunc);

        // Group by label
        var clusters = new List<Cluster>();
        var noise = new List<EmbeddingWithId>();

        var labelGroups = result.Labels
            .Select((label, index) => (Label: label, Index: index))
            .GroupBy(x => x.Label);

        foreach (var group in labelGroups)
        {
            var groupItems = group.Select(x => items[x.Index]).ToList();

            if (group.Key == -1)
            {
                noise.AddRange(groupItems);
            }
            else
            {
                clusters.Add(new Cluster(group.Key, groupItems));
            }
        }

        _logger.LogInformation("Clustering complete: {ClusterCount} clusters, {NoiseCount} noise points",
            clusters.Count, noise.Count);

        return new ClusteringResult(clusters, noise);
    }
}
