namespace AIOMarketMaker.Core.Services.Taxonomy;

public record ExtractionConfig(
    string ModelPath,
    int MaxTokens = 512,
    int ContextSize = 1024,
    int GpuLayers = -1);

public record SkeletonAxis(string Name, string Description, IEnumerable<string> Values);

public record ExtractionSkeleton(IEnumerable<SkeletonAxis> Axes);

public interface IExtractionModelRunner : IDisposable
{
    Task<Dictionary<string, string?>?> Extract(string title, ExtractionSkeleton skeleton);
    Task<bool> IsHealthy(CancellationToken ct = default);
}
