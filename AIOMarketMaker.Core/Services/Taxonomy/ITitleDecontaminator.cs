namespace AIOMarketMaker.Core.Services.Taxonomy;

public record DecontaminationResult(
    IEnumerable<string> FilteredTitles,
    IReadOnlyDictionary<int, int> FilteredToOriginalIndex,
    int ExcludedCount);

public interface ITitleDecontaminator
{
    Task<DecontaminationResult> Filter(
        IEnumerable<string> titles,
        string? productName,
        IEnumerable<string>? brandTokens = null,
        CancellationToken ct = default);
}
