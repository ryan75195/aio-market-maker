namespace AIOMarketMaker.Core.Services.Taxonomy;

public class TaxonomyService : ITaxonomyService
{
    private const double SignificanceThreshold = 0.03;
    private const int MinExclusivePairs = 3;
    private const double GraphEdgeSimilarityThreshold = 0.15;
    private const double ValueDedupOverlap = 0.85;
    private const double ExclusivityThreshold = 0.05;

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
        var total = titleList.Count;

        // Stage 1: Extract and dedup n-grams
        var rawNgrams = _extractor.Extract(titleList);
        var ngrams = (await _extractor.Deduplicate(rawNgrams, ct)).ToList();

        // Compute match sets and filter to significant
        var allMatchSets = _analyzer.ComputeMatchSets(titleList, ngrams).ToList();
        var minMatches = (int)(total * SignificanceThreshold);
        var significantSets = allMatchSets
            .Where(ms => ms.ListingIndices.Count >= minMatches)
            .ToList();

        // Find mutually exclusive pairs
        var exclusivePairs = _analyzer.FindExclusivePairs(significantSets).ToList();

        // Filter to candidates participating in enough ME pairs
        var pairCounts = new Dictionary<string, int>();
        foreach (var pair in exclusivePairs)
        {
            pairCounts[pair.A.Canonical] = pairCounts.GetValueOrDefault(pair.A.Canonical) + 1;
            pairCounts[pair.B.Canonical] = pairCounts.GetValueOrDefault(pair.B.Canonical) + 1;
        }

        var candidates = significantSets
            .Where(ms => pairCounts.GetValueOrDefault(ms.Ngram.Canonical) >= MinExclusivePairs)
            .ToList();

        if (candidates.Count < 2)
        {
            return new TaxonomyResult(
                Enumerable.Empty<Axis>(),
                titleList.Select((_, i) => new CellAssignment(
                    i, new Dictionary<string, string>(), false)),
                Enumerable.Empty<CellStats>(),
                0.0, 0.0);
        }

        // Stage 2: Build graph and detect communities
        var candidateNames = candidates.Select(c => c.Ngram.Canonical).ToList();
        var candidateEmbeddings = await _embeddingService.GetEmbeddings(candidateNames, ct, EmbeddingModel.Small);

        var nameToIndex = new Dictionary<string, int>();
        for (var i = 0; i < candidateNames.Count; i++)
        {
            nameToIndex[candidateNames[i]] = i;
        }

        // Normalize embeddings for cosine similarity
        var normed = candidateEmbeddings.Select(VectorMath.Normalize).ToArray();

        var graphEdges = new List<WeightedEdge>();
        foreach (var pair in exclusivePairs)
        {
            if (!nameToIndex.TryGetValue(pair.A.Canonical, out var idxA) ||
                !nameToIndex.TryGetValue(pair.B.Canonical, out var idxB))
            {
                continue;
            }

            var similarity = VectorMath.CosineSimilarity(normed[idxA], normed[idxB]);
            if (similarity > GraphEdgeSimilarityThreshold)
            {
                graphEdges.Add(new WeightedEdge(idxA, idxB, similarity));
            }
        }

        var rawCommunities = _detector.Detect(
            graphEdges, candidates.Count, resolution: 2.0).ToList();

        // Map communities from node indices to actual n-grams
        var axes = new List<Axis>();
        var communityIndex = 0;
        foreach (var community in rawCommunities)
        {
            var memberIndices = community.MemberIndices.ToList();

            if (memberIndices.Count < 2)
            {
                continue;
            }

            var memberNgrams = memberIndices
                .Select(i => candidates[i].Ngram)
                .ToList();

            var values = memberNgrams
                .Select(n => new AxisValue(n.Canonical, new[] { n }))
                .ToList();

            axes.Add(new Axis($"Axis {communityIndex}", values));
            communityIndex++;
        }

        // Stage 3: Post-processing
        var matchSetLookup = allMatchSets.ToDictionary(ms => ms.Ngram.Canonical);
        axes = DeduplicateAxisValues(axes, matchSetLookup);
        axes = PruneOverlappingValues(axes, matchSetLookup);
        axes = EnforceMutualExclusivityPerValue(axes, matchSetLookup);

        // Cell assignment
        var assignments = AssignListings(titleList, axes, matchSetLookup);
        var covered = assignments.Count(a => a.Cell.Count > 0);
        var conflicts = assignments.Count(a => a.HasConflict);

        return new TaxonomyResult(
            axes,
            assignments,
            Enumerable.Empty<CellStats>(), // Pricing stats require price data -- skipped for now
            total > 0 ? 100.0 * covered / total : 0,
            total > 0 ? 100.0 * conflicts / total : 0);
    }

    // --- Post-processing methods ---

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

                    if (setA.Count == 0 || setB.Count == 0)
                    {
                        continue;
                    }

                    var overlap = (double)setA.Count(idx => setB.Contains(idx))
                        / Math.Min(setA.Count, setB.Count);

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
            if (surviving.Count >= 2)
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
            var threshold = values.Count >= 5 ? 0.35 : 0.20;

            var valueSets = values.Select(v => GetValueMatchSet(v, matchSets)).ToList();
            var pruning = true;

            while (pruning && values.Count >= 2)
            {
                pruning = false;
                var worstIndex = -1;
                var worstPartners = 0;

                for (var i = 0; i < values.Count; i++)
                {
                    var partners = 0;
                    for (var j = 0; j < values.Count; j++)
                    {
                        if (i == j || valueSets[i].Count == 0 || valueSets[j].Count == 0)
                        {
                            continue;
                        }

                        var overlap = (double)valueSets[i].Count(idx => valueSets[j].Contains(idx))
                            / Math.Min(valueSets[i].Count, valueSets[j].Count);

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
                    threshold = values.Count >= 5 ? 0.35 : 0.20;
                    pruning = true;
                }
            }

            if (values.Count >= 2)
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

            while (changed && values.Count >= 2)
            {
                changed = false;
                var valueSets = values.Select(v => GetValueMatchSet(v, matchSets)).ToList();

                var violationCounts = new int[values.Count];
                for (var i = 0; i < values.Count; i++)
                {
                    for (var j = i + 1; j < values.Count; j++)
                    {
                        if (valueSets[i].Count == 0 || valueSets[j].Count == 0)
                        {
                            continue;
                        }

                        var overlap = (double)valueSets[i].Count(idx => valueSets[j].Contains(idx))
                            / Math.Min(valueSets[i].Count, valueSets[j].Count);

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

            if (values.Count >= 2)
            {
                result.Add(new Axis(axis.Name, values));
            }
        }
        return result;
    }

    // --- Cell assignment ---

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
                    // Conflict -- pick the longest match (most specific) but flag it
                    // Substring conflict resolution: if one value is a substring of another, keep the longer one
                    var resolved = ResolveSubstringConflict(matchedValues);
                    if (resolved != null)
                    {
                        cell[axis.Name] = resolved;
                    }
                    else
                    {
                        hasConflict = true;
                        cell[axis.Name] = matchedValues[0]; // pick first
                    }
                }
            }

            assignments.Add(new CellAssignment(i, cell, hasConflict));
        }

        return assignments;
    }

    private static string? ResolveSubstringConflict(List<string> values)
    {
        // If one value contains another (e.g., "slim" vs "ps5 slim"), keep the longer one
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
                // Check all other values are substrings of this one
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
        return null; // Can't resolve -- true conflict
    }

    // --- Helpers ---

    private static IReadOnlySet<int> GetValueMatchSet(
        AxisValue value, Dictionary<string, MatchSet> matchSets)
    {
        // Union all match sets for this value's ngrams
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
