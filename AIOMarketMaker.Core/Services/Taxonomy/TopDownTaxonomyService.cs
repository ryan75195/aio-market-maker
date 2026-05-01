namespace AIOMarketMaker.Core.Services.Taxonomy;

public class TopDownTaxonomyService : ITaxonomyService
{
    private const int MaxSkeletonSamples = 250;

    private readonly ISkeletonGenerator _skeletonGenerator;
    private readonly IExtractionModelRunner _extractor;

    public TopDownTaxonomyService(
        ISkeletonGenerator skeletonGenerator,
        IExtractionModelRunner extractor)
    {
        _skeletonGenerator = skeletonGenerator;
        _extractor = extractor;
    }

    public async Task<TaxonomyResult> Generate(
        IEnumerable<string> titles,
        string? productName = null,
        IEnumerable<string>? brandTokens = null,
        CancellationToken ct = default)
    {
        var titlesList = titles.ToList();

        // Sample titles for skeleton generation
        var skeletonSample = titlesList.Count > MaxSkeletonSamples
            ? SampleEvenly(titlesList, MaxSkeletonSamples)
            : titlesList;

        var skeleton = await _skeletonGenerator.Generate(
            productName ?? "product",
            skeletonSample,
            titlesList.Count,
            ct);

        // Extract axes from each title
        var assignments = new List<CellAssignment>(titlesList.Count);
        var excludedCount = 0;

        for (var i = 0; i < titlesList.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var extraction = await _extractor.Extract(titlesList[i], skeleton);

            if (extraction == null)
            {
                // All-null: accessory/part/unmatched
                assignments.Add(new CellAssignment(i, new Dictionary<string, string>(), false));
                excludedCount++;
            }
            else
            {
                // Build cell from non-null values
                var cell = extraction
                    .Where(kv => kv.Value != null)
                    .ToDictionary(kv => kv.Key, kv => kv.Value!);
                assignments.Add(new CellAssignment(i, cell, false));
            }
        }

        // Convert skeleton axes to Taxonomy Axis records
        var axes = skeleton.Axes.Select((sa, idx) => new Axis(
            sa.Name,
            sa.Values.Select(v => new AxisValue(v, Enumerable.Empty<Ngram>())),
            idx)).ToList();

        // Build cell stats
        var coveredAssignments = assignments.Where(a => a.Cell.Count > 0).ToList();
        var coveragePercent = titlesList.Count > 0
            ? 100.0 * coveredAssignments.Count / titlesList.Count
            : 0.0;

        var cellGroups = coveredAssignments
            .GroupBy(a => string.Join("|", a.Cell.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")))
            .ToList();

        var cells = cellGroups.Select(g =>
        {
            var representative = g.First().Cell;
            return new CellStats(
                representative,
                g.Count(),
                Active: 0,
                Sold: 0,
                SellThroughPct: 0,
                MedianActivePrice: 0,
                MedianSoldPrice: 0);
        }).ToList();

        return new TaxonomyResult(
            axes,
            assignments,
            cells,
            coveragePercent,
            ConflictPercent: 0.0,
            excludedCount);
    }

    private static List<string> SampleEvenly(List<string> items, int n)
    {
        var step = (double)items.Count / n;
        return Enumerable.Range(0, n)
            .Select(i => items[(int)(i * step)])
            .ToList();
    }
}
