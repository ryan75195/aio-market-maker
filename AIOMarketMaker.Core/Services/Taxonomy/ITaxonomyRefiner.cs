namespace AIOMarketMaker.Core.Services.Taxonomy;

public record RefinedAxis(
    string Original,
    string Name,
    int Importance,
    IEnumerable<string> RemoveValues,
    IEnumerable<string> AddValues);

public record AxisMerge(string Keep, string Absorb);

public record TaxonomyRefinement(
    IEnumerable<RefinedAxis> Axes,
    IEnumerable<AxisMerge> MergeAxes,
    IEnumerable<string> DropAxes);

public interface ITaxonomyRefiner
{
    Task<TaxonomyRefinement> Refine(
        IEnumerable<Axis> axes,
        string productName,
        IEnumerable<string> sampleTitles,
        CancellationToken ct = default);
}
