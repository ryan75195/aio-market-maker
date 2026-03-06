namespace AIOMarketMaker.Core.Services.Taxonomy;

public record Ngram(string Canonical, IEnumerable<string> Forms, int Frequency);

public record MatchSet(Ngram Ngram, IReadOnlySet<int> ListingIndices);

public record MutuallyExclusivePair(
    Ngram A, Ngram B, double Overlap, double EmbeddingSimilarity);

public record WeightedEdge(int NodeA, int NodeB, double Weight);

public record Community(
    int Id, IEnumerable<Ngram> Members,
    double ExclusivityDensity, double Coherence, double Coverage);

public record Axis(string Name, IEnumerable<AxisValue> Values);

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
    Task<TaxonomyResult> Generate(IEnumerable<string> titles);
}
