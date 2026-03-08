using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services;

public record ClusteringConfig(
    int MinClusterSize = 5,
    int MinPoints = 3,
    double Threshold = 1.5
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
    ClusteringResult ClusterByText(IReadOnlyList<int> ids, IReadOnlyList<string> texts, double? threshold = null);
}

public class ClusteringService : IClusteringService
{
    private readonly ClusteringConfig _config;
    private readonly ITfIdfVectorizer _tfidfVectorizer;
    private readonly ILogger<ClusteringService> _logger;

    public ClusteringService(ClusteringConfig config, ITfIdfVectorizer tfidfVectorizer, ILogger<ClusteringService> logger)
    {
        _config = config;
        _tfidfVectorizer = tfidfVectorizer;
        _logger = logger;
    }

    public ClusteringResult Cluster(IReadOnlyList<EmbeddingWithId> items)
    {
        return ClusterWithThreshold(items, _config.Threshold);
    }

    public ClusteringResult ClusterByText(IReadOnlyList<int> ids, IReadOnlyList<string> texts, double? threshold = null)
    {
        if (ids.Count != texts.Count)
        {
            throw new ArgumentException("ids and texts must have the same length");
        }

        if (ids.Count == 0)
        {
            return new ClusteringResult(new List<Cluster>(), new List<EmbeddingWithId>());
        }

        _logger.LogInformation("TF-IDF vectorizing {Count} texts", texts.Count);

        var tfidfResult = _tfidfVectorizer.FitTransform(texts);

        _logger.LogInformation("TF-IDF produced {FeatureCount} features", tfidfResult.FeatureNames.Count);

        var items = ids
            .Select((id, i) => new EmbeddingWithId(id, tfidfResult.Vectors[i]))
            .ToList();

        return ClusterWithThreshold(items, threshold ?? _config.Threshold);
    }

    private ClusteringResult ClusterWithThreshold(IReadOnlyList<EmbeddingWithId> items, double threshold)
    {
        if (items.Count == 0)
        {
            return new ClusteringResult(new List<Cluster>(), new List<EmbeddingWithId>());
        }

        _logger.LogInformation("Ward clustering {Count} items with Threshold={Threshold}",
            items.Count, threshold);

        var data = items.Select(e => e.Embedding).ToArray();
        var labels = WardLinkage.Cluster(data, threshold);

        var clusters = new List<Cluster>();
        var groups = labels
            .Select((label, index) => (Label: label, Index: index))
            .GroupBy(x => x.Label);

        foreach (var group in groups)
        {
            var groupItems = group.Select(x => items[x.Index]).ToList();
            clusters.Add(new Cluster(group.Key, groupItems));
        }

        _logger.LogInformation("Ward clustering complete: {ClusterCount} clusters from {Count} items",
            clusters.Count, items.Count);

        return new ClusteringResult(clusters, new List<EmbeddingWithId>());
    }
}
