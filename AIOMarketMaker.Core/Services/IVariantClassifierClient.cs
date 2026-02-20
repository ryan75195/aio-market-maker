namespace AIOMarketMaker.Core.Services;

public interface IVariantClassifierClient
{
    Task<IReadOnlyList<PairResult>> Classify(
        IEnumerable<ClassifyPairRequest> pairs,
        CancellationToken ct = default);

    Task<bool> IsHealthy(CancellationToken ct = default);
}

public record ClassifyPairRequest(
    string TitleA,
    string DescriptionA,
    string TitleB,
    string DescriptionB);

public record PairResult(bool IsComparable, float Confidence, string? Reason = null);
