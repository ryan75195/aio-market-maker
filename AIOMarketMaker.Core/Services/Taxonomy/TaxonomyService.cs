namespace AIOMarketMaker.Core.Services.Taxonomy;

public class TaxonomyService : ITaxonomyService
{
    private const double SignificanceThreshold = 0.03;
    private const int MinExclusivePairs = 3;
    private const double GraphEdgeSimilarityThreshold = 0.15;
    private const double ValueDedupOverlap = 0.85;
    private const double ExclusivityThreshold = 0.05;
    private const int MinAxisValues = 2;
    private const double LouvainResolution = 2.0;
    private const double PruneLargeAxisThreshold = 0.35;
    private const double PruneSmallAxisThreshold = 0.20;
    private const int PruneLargeAxisMinValues = 5;

    private readonly INgramExtractor _extractor;
    private readonly IMutualExclusivityAnalyzer _analyzer;
    private readonly ICommunityDetector _detector;
    private readonly IEmbeddingService _embeddingService;

    public TaxonomyService(
        INgramExtractor extractor,
        IMutualExclusivityAnalyzer analyzer,
        ICommunityDetector detector,
        IEmbeddingService embeddingService)
    {
        _extractor = extractor;
        _analyzer = analyzer;
        _detector = detector;
        _embeddingService = embeddingService;
    }

    public async Task<TaxonomyResult> Generate(IEnumerable<string> titles, CancellationToken ct = default)
    {
        var titleList = titles.ToList();

        var rawNgrams = _extractor.Extract(titleList);
        var ngrams = (await _extractor.MergeSynonyms(rawNgrams, ct)).ToList();

        var candidates = FindCandidates(titleList, ngrams, out var allMatchSets, out var exclusivePairs);
        if (candidates.Count < MinAxisValues)
        {
            return EmptyResult(titleList);
        }

        var graphEdges = await BuildGraphEdges(candidates, exclusivePairs, ct);
        var communities = _detector.Detect(graphEdges, candidates.Count, resolution: LouvainResolution);
        var axes = MapCommunitiesToAxes(communities, candidates);

        var matchSetLookup = allMatchSets.ToDictionary(matchSet => matchSet.Ngram.Canonical);
        axes = PostProcess(axes, matchSetLookup);

        var assignments = AssignListings(titleList, axes, matchSetLookup);
        var covered = assignments.Count(a => a.Cell.Count > 0);
        var conflicts = assignments.Count(a => a.HasConflict);
        var total = titleList.Count;

        return new TaxonomyResult(
            axes, assignments, Enumerable.Empty<CellStats>(),
            total > 0 ? 100.0 * covered / total : 0,
            total > 0 ? 100.0 * conflicts / total : 0);
    }

    private static TaxonomyResult EmptyResult(List<string> titles)
    {
        return new TaxonomyResult(
            Enumerable.Empty<Axis>(),
            titles.Select((_, i) => new CellAssignment(i, new Dictionary<string, string>(), false)),
            Enumerable.Empty<CellStats>(),
            0.0, 0.0);
    }

    private List<MatchSet> FindCandidates(
        List<string> titles, List<Ngram> ngrams,
        out List<MatchSet> allMatchSets,
        out List<MutuallyExclusivePair> exclusivePairs)
    {
        allMatchSets = _analyzer.ComputeMatchSets(titles, ngrams).ToList();
        var minMatches = (int)(titles.Count * SignificanceThreshold);
        var significantSets = allMatchSets
            .Where(ms => ms.ListingIndices.Count >= minMatches)
            .ToList();

        exclusivePairs = _analyzer.FindExclusivePairs(significantSets).ToList();

        var pairCounts = new Dictionary<string, int>();
        foreach (var pair in exclusivePairs)
        {
            pairCounts[pair.A.Canonical] = pairCounts.GetValueOrDefault(pair.A.Canonical) + 1;
            pairCounts[pair.B.Canonical] = pairCounts.GetValueOrDefault(pair.B.Canonical) + 1;
        }

        return significantSets
            .Where(ms => pairCounts.GetValueOrDefault(ms.Ngram.Canonical) >= MinExclusivePairs)
            .ToList();
    }

    private async Task<List<WeightedEdge>> BuildGraphEdges(
        List<MatchSet> candidates,
        List<MutuallyExclusivePair> exclusivePairs,
        CancellationToken ct)
    {
        var candidateNames = candidates.Select(c => c.Ngram.Canonical).ToList();
        var embeddings = await _embeddingService.GetEmbeddings(candidateNames, ct, EmbeddingModel.Small);
        var normalizedEmbeddings = embeddings.Select(VectorMath.Normalize).ToArray();

        var nameToIndex = new Dictionary<string, int>();
        for (var i = 0; i < candidateNames.Count; i++)
        {
            nameToIndex[candidateNames[i]] = i;
        }

        var edges = new List<WeightedEdge>();
        foreach (var pair in exclusivePairs)
        {
            if (!nameToIndex.TryGetValue(pair.A.Canonical, out var indexA) ||
                !nameToIndex.TryGetValue(pair.B.Canonical, out var indexB))
            {
                continue;
            }

            var similarity = VectorMath.CosineSimilarity(normalizedEmbeddings[indexA], normalizedEmbeddings[indexB]);
            if (similarity > GraphEdgeSimilarityThreshold)
            {
                edges.Add(new WeightedEdge(indexA, indexB, similarity));
            }
        }

        return edges;
    }

    private static List<Axis> MapCommunitiesToAxes(
        IEnumerable<Community> communities, List<MatchSet> candidates)
    {
        var axes = new List<Axis>();
        var axisIndex = 0;

        foreach (var community in communities)
        {
            var memberIndices = community.MemberIndices.ToList();
            if (memberIndices.Count < MinAxisValues)
            {
                continue;
            }

            var values = memberIndices
                .Select(i => new AxisValue(candidates[i].Ngram.Canonical, new[] { candidates[i].Ngram }))
                .ToList();

            axes.Add(new Axis($"Axis {axisIndex}", values));
            axisIndex++;
        }

        return axes;
    }

    private static List<Axis> PostProcess(
        List<Axis> axes, Dictionary<string, MatchSet> matchSetLookup)
    {
        axes = DeduplicateAxisValues(axes, matchSetLookup);
        axes = PruneOverlappingValues(axes, matchSetLookup);
        axes = EnforceMutualExclusivityPerValue(axes, matchSetLookup);
        return axes;
    }

    private static List<Axis> DeduplicateAxisValues(
        List<Axis> axes, Dictionary<string, MatchSet> matchSets)
    {
        var result = new List<Axis>();
        foreach (var axis in axes)
        {
            var values = axis.Values.ToList();
            var toRemove = new HashSet<int>();

            for (var i = 0; i < values.Count && !toRemove.Contains(i); i++)
            {
                for (var j = i + 1; j < values.Count; j++)
                {
                    if (toRemove.Contains(j))
                    {
                        continue;
                    }

                    var setA = GetValueMatchSet(values[i], matchSets);
                    var setB = GetValueMatchSet(values[j], matchSets);
                    var overlap = CalculateOverlap(setA, setB);

                    if (overlap >= ValueDedupOverlap)
                    {
                        toRemove.Add(setA.Count >= setB.Count ? j : i);
                        if (toRemove.Contains(i))
                        {
                            break;
                        }
                    }
                }
            }

            var surviving = values.Where((_, idx) => !toRemove.Contains(idx)).ToList();
            if (surviving.Count >= MinAxisValues)
            {
                result.Add(new Axis(axis.Name, surviving));
            }
        }
        return result;
    }

    private static List<Axis> PruneOverlappingValues(
        List<Axis> axes, Dictionary<string, MatchSet> matchSets)
    {
        var result = new List<Axis>();
        foreach (var axis in axes)
        {
            var values = axis.Values.ToList();
            var threshold = values.Count >= PruneLargeAxisMinValues
                ? PruneLargeAxisThreshold
                : PruneSmallAxisThreshold;

            var valueSets = values.Select(v => GetValueMatchSet(v, matchSets)).ToList();
            var pruning = true;

            while (pruning && values.Count >= MinAxisValues)
            {
                pruning = false;
                var worstIndex = -1;
                var worstPartners = 0;

                for (var i = 0; i < values.Count; i++)
                {
                    var partners = 0;
                    for (var j = 0; j < values.Count; j++)
                    {
                        if (i == j)
                        {
                            continue;
                        }

                        var overlap = CalculateOverlap(valueSets[i], valueSets[j]);

                        if (overlap > threshold)
                        {
                            partners++;
                        }
                    }

                    if (partners > worstPartners ||
                        (partners == worstPartners && partners > 0
                         && worstIndex >= 0 && valueSets[i].Count < valueSets[worstIndex].Count))
                    {
                        worstPartners = partners;
                        worstIndex = i;
                    }
                }

                if (worstPartners > 0 && worstIndex >= 0)
                {
                    values.RemoveAt(worstIndex);
                    valueSets.RemoveAt(worstIndex);
                    threshold = values.Count >= PruneLargeAxisMinValues
                        ? PruneLargeAxisThreshold
                        : PruneSmallAxisThreshold;
                    pruning = true;
                }
            }

            if (values.Count >= MinAxisValues)
            {
                result.Add(new Axis(axis.Name, values));
            }
        }
        return result;
    }

    private static List<Axis> EnforceMutualExclusivityPerValue(
        List<Axis> axes, Dictionary<string, MatchSet> matchSets)
    {
        var result = new List<Axis>();
        foreach (var axis in axes)
        {
            var values = axis.Values.ToList();
            var changed = true;

            while (changed && values.Count >= MinAxisValues)
            {
                changed = false;
                var valueSets = values.Select(v => GetValueMatchSet(v, matchSets)).ToList();

                var violationCounts = new int[values.Count];
                for (var i = 0; i < values.Count; i++)
                {
                    for (var j = i + 1; j < values.Count; j++)
                    {
                        var overlap = CalculateOverlap(valueSets[i], valueSets[j]);

                        if (overlap >= ExclusivityThreshold)
                        {
                            violationCounts[i]++;
                            violationCounts[j]++;
                        }
                    }
                }

                var worstIndex = -1;
                var worstCount = 0;
                for (var i = 0; i < violationCounts.Length; i++)
                {
                    if (violationCounts[i] > worstCount)
                    {
                        worstCount = violationCounts[i];
                        worstIndex = i;
                    }
                }

                if (worstIndex >= 0)
                {
                    values.RemoveAt(worstIndex);
                    changed = true;
                }
            }

            if (values.Count >= MinAxisValues)
            {
                result.Add(new Axis(axis.Name, values));
            }
        }
        return result;
    }

    private static List<CellAssignment> AssignListings(
        List<string> titles, List<Axis> axes,
        Dictionary<string, MatchSet> matchSets)
    {
        var assignments = new List<CellAssignment>();

        for (var i = 0; i < titles.Count; i++)
        {
            var cell = new Dictionary<string, string>();
            var hasConflict = false;

            foreach (var axis in axes)
            {
                var matchedValues = new List<string>();
                foreach (var value in axis.Values)
                {
                    if (matchSets.TryGetValue(value.Label, out var ms) && ms.ListingIndices.Contains(i))
                    {
                        matchedValues.Add(value.Label);
                    }
                }

                if (matchedValues.Count == 1)
                {
                    cell[axis.Name] = matchedValues[0];
                }
                else if (matchedValues.Count > 1)
                {
                    var resolved = ResolveSubstringConflict(matchedValues);
                    if (resolved != null)
                    {
                        cell[axis.Name] = resolved;
                    }
                    else
                    {
                        hasConflict = true;
                        cell[axis.Name] = matchedValues[0];
                    }
                }
            }

            assignments.Add(new CellAssignment(i, cell, hasConflict));
        }

        return assignments;
    }

    private static string? ResolveSubstringConflict(List<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            var isSubstringOfAnother = false;
            for (var j = 0; j < values.Count; j++)
            {
                if (i != j && values[j].Contains(values[i], StringComparison.Ordinal)
                    && values[j].Length > values[i].Length)
                {
                    isSubstringOfAnother = true;
                    break;
                }
            }

            if (!isSubstringOfAnother)
            {
                var allOthersSubstring = true;
                for (var j = 0; j < values.Count; j++)
                {
                    if (i != j && !values[i].Contains(values[j], StringComparison.Ordinal))
                    {
                        allOthersSubstring = false;
                        break;
                    }
                }

                if (allOthersSubstring)
                {
                    return values[i];
                }
            }
        }
        return null;
    }

    private static double CalculateOverlap(IReadOnlySet<int> setA, IReadOnlySet<int> setB)
    {
        if (setA.Count == 0 || setB.Count == 0)
        {
            return 0.0;
        }

        var intersection = setA.Count(idx => setB.Contains(idx));
        return (double)intersection / Math.Min(setA.Count, setB.Count);
    }

    private static IReadOnlySet<int> GetValueMatchSet(
        AxisValue value, Dictionary<string, MatchSet> matchSets)
    {
        var result = new HashSet<int>();
        foreach (var ngram in value.Ngrams)
        {
            if (matchSets.TryGetValue(ngram.Canonical, out var ms))
            {
                result.UnionWith(ms.ListingIndices);
            }
        }
        return result;
    }

}
