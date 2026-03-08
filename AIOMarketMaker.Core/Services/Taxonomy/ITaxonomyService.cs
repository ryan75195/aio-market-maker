namespace AIOMarketMaker.Core.Services.Taxonomy;

public record RawNgram(string Term, int Frequency);

public record Ngram(string Canonical, IEnumerable<string> Forms, int Frequency);

public record MatchSet(Ngram Ngram, IReadOnlySet<int> ListingIndices);

public record MutuallyExclusivePair(Ngram A, Ngram B, double Overlap);

public record WeightedEdge(int NodeA, int NodeB, double Weight);

public record Community(int Id, IEnumerable<int> MemberIndices);

public record Axis(string Name, IEnumerable<AxisValue> Values, int? Importance = null);

public record AxisValue(string Label, IEnumerable<Ngram> Ngrams);

public record CellAssignment(
    int ListingIndex, IReadOnlyDictionary<string, string> Cell, bool HasConflict);

public record CellStats(
    IReadOnlyDictionary<string, string> Cell,
    int Count, int Active, int Sold, int SellThroughPct,
    decimal MedianActivePrice, decimal MedianSoldPrice);

public record TaxonomyResult(
    IEnumerable<Axis> Axes,
    IEnumerable<CellAssignment> Assignments,
    IEnumerable<CellStats> Cells,
    double CoveragePercent, double ConflictPercent);

public interface ITaxonomyService
{
    Task<TaxonomyResult> Generate(
        IEnumerable<string> titles,
        string? productName = null,
        CancellationToken ct = default);
}
