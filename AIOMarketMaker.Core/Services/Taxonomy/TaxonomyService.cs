namespace AIOMarketMaker.Core.Services.Taxonomy;

public class TaxonomyService : ITaxonomyService
{
    private const double SignificanceThreshold = 0.03;
    private const int MinExclusivePairs = 3;
    private const double GraphEdgeSimilarityThreshold = 0.15;
    private const double ValueDedupOverlap = 0.85;
    private const double ExclusivityThreshold = 0.05;
    private const double AxisMergeJaccardThreshold = 0.70;
    private const double AxisMergeContainmentThreshold = 0.85;
    private const int MinAxisValues = 2;
    private const double LouvainResolution = 2.0;
    private const double PruneLargeAxisThreshold = 0.35;
    private const double PruneSmallAxisThreshold = 0.20;
    private const int PruneLargeAxisMinValues = 5;
    private const double EmbeddingMergeThreshold = 0.75;
    private const double SemanticOutlierThreshold = 0.30;
    private const double EmbeddingDedupThreshold = 0.90;

    private static readonly HashSet<string> GenericStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "with", "free", "all", "day", "next", "del", "count", "brand"
    };

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
        var synonymsMerged = await _extractor.MergeSynonyms(rawNgrams, ct);
        var ngrams = (await _extractor.SubsumeByTokenOverlap(synonymsMerged, ct)).ToList();

        var candidates = FindCandidates(titleList, ngrams, out var allMatchSets, out var exclusivePairs);
        if (candidates.Count < MinAxisValues)
        {
            return EmptyResult(titleList);
        }

        var graphEdges = await BuildGraphEdges(candidates, exclusivePairs, ct);
        var communities = _detector.Detect(graphEdges, candidates.Count, resolution: LouvainResolution);
        var axes = MapCommunitiesToAxes(communities, candidates);

        var matchSetLookup = allMatchSets.ToDictionary(matchSet => matchSet.Ngram.Canonical);

        var valueLabels = axes
            .SelectMany(a => a.Values.Select(v => v.Label))
            .Distinct()
            .ToList();
        var valueEmbeddingVecs = valueLabels.Count > 0
            ? await _embeddingService.GetEmbeddings(valueLabels, ct, EmbeddingModel.Small)
            : Array.Empty<float[]>();
        var valueEmbeddings = new Dictionary<string, float[]>();
        for (var i = 0; i < valueLabels.Count; i++)
        {
            valueEmbeddings[valueLabels[i]] = VectorMath.Normalize(valueEmbeddingVecs[i]);
        }

        axes = PostProcess(axes, matchSetLookup, valueEmbeddings);

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
        List<Axis> axes, Dictionary<string, MatchSet> matchSetLookup,
        Dictionary<string, float[]> valueEmbeddings)
    {
        axes = DeduplicateAxisValues(axes, matchSetLookup);
        axes = DeduplicateByEmbedding(axes, valueEmbeddings);
        axes = PruneOverlappingValues(axes, matchSetLookup);
        axes = EnforceMutualExclusivityPerValue(axes, matchSetLookup);
        axes = PruneGenericValues(axes);
        axes = PruneSemanticOutliers(axes, valueEmbeddings);
        axes = UnifyRedundantAxes(axes, matchSetLookup, valueEmbeddings);
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

    public static List<Axis> DeduplicateByEmbedding(
        List<Axis> axes, Dictionary<string, float[]> valueEmbeddings)
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

                    var shouldMerge = AreStemVariants(values[i].Label, values[j].Label);

                    if (!shouldMerge &&
                        valueEmbeddings.TryGetValue(values[i].Label, out var embA) &&
                        valueEmbeddings.TryGetValue(values[j].Label, out var embB))
                    {
                        shouldMerge = VectorMath.CosineSimilarity(embA, embB) >= EmbeddingDedupThreshold;
                    }

                    if (shouldMerge && values.Count - toRemove.Count > MinAxisValues)
                    {
                        var removeIdx = values[i].Label.Length <= values[j].Label.Length ? j : i;
                        toRemove.Add(removeIdx);
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

    private static readonly string[] StemSuffixes = { "ed", "d", "ing", "er", "est", "ic", "s" };

    internal static bool AreStemVariants(string a, string b)
    {
        if (a.Length < 3 || b.Length < 3)
        {
            return false;
        }

        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);

        // Check if longer is shorter + a common suffix
        if (longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = longer[shorter.Length..];
            if (StemSuffixes.Contains(suffix.ToLowerInvariant()))
            {
                return true;
            }
        }

        // Check edit distance = 1 for same-length words (disk/disc)
        if (shorter.Length == longer.Length)
        {
            var diffs = 0;
            for (var i = 0; i < shorter.Length; i++)
            {
                if (char.ToLowerInvariant(shorter[i]) != char.ToLowerInvariant(longer[i]))
                {
                    diffs++;
                    if (diffs > 1)
                    {
                        return false;
                    }
                }
            }
            return diffs == 1;
        }

        return false;
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

                    if (IsWorseOverlapper(partners, worstPartners, i, worstIndex, valueSets))
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

    private static HashSet<int> GetAxisListingSet(Axis axis, Dictionary<string, MatchSet> matchSets)
    {
        var set = new HashSet<int>();
        foreach (var value in axis.Values)
        {
            var valueSet = GetValueMatchSet(value, matchSets);
            set.UnionWith(valueSet);
        }
        return set;
    }

    private static double JaccardSimilarity(IReadOnlySet<int> setA, IReadOnlySet<int> setB)
    {
        if (setA.Count == 0 && setB.Count == 0)
        {
            return 1.0;
        }

        var intersection = setA.Count(idx => setB.Contains(idx));
        var union = setA.Count + setB.Count - intersection;
        return union > 0 ? (double)intersection / union : 0.0;
    }

    private static bool AreAxesContained(
        Axis axisA, Axis axisB, Dictionary<string, MatchSet> matchSets)
    {
        // Determine which axis is smaller (the potential subset)
        var aSet = GetAxisListingSet(axisA, matchSets);
        var bSet = GetAxisListingSet(axisB, matchSets);
        var (smaller, larger) = aSet.Count <= bSet.Count
            ? (axisA, axisB) : (axisB, axisA);

        var smallerValues = smaller.Values.ToList();
        var largerValues = larger.Values.ToList();

        if (smallerValues.Count == 0 || largerValues.Count == 0)
        {
            return false;
        }

        // Each value in the smaller axis must be predominantly contained
        // within a distinct value of the larger axis
        var usedLargerValues = new HashSet<int>();

        foreach (var smallVal in smallerValues)
        {
            var smallSet = GetValueMatchSet(smallVal, matchSets);
            if (smallSet.Count == 0)
            {
                continue;
            }

            // Find the best matching value in the larger axis
            var bestIdx = -1;
            var bestContainment = 0.0;
            for (var k = 0; k < largerValues.Count; k++)
            {
                var largeSet = GetValueMatchSet(largerValues[k], matchSets);
                if (largeSet.Count == 0)
                {
                    continue;
                }

                var intersection = smallSet.Count(idx => largeSet.Contains(idx));
                var containment = (double)intersection / smallSet.Count;
                if (containment > bestContainment)
                {
                    bestContainment = containment;
                    bestIdx = k;
                }
            }

            if (bestContainment < AxisMergeContainmentThreshold)
            {
                return false;
            }

            if (bestIdx >= 0 && !usedLargerValues.Add(bestIdx))
            {
                // Two small values map to the same large value — not a clean containment
                return false;
            }
        }

        return true;
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

    private static bool IsWorseOverlapper(
        int partners, int worstPartners,
        int candidateIndex, int currentWorstIndex,
        List<IReadOnlySet<int>> valueSets)
    {
        if (partners > worstPartners)
        {
            return true;
        }

        // Tie-break: among equal violators, prefer removing the smaller set
        return partners == worstPartners
            && partners > 0
            && currentWorstIndex >= 0
            && valueSets[candidateIndex].Count < valueSets[currentWorstIndex].Count;
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

    public static List<Axis> PruneGenericValues(List<Axis> axes)
    {
        var result = new List<Axis>();
        foreach (var axis in axes)
        {
            var surviving = axis.Values
                .Where(v => !GenericStopWords.Contains(v.Label))
                .ToList();

            if (surviving.Count >= MinAxisValues)
            {
                result.Add(new Axis(axis.Name, surviving));
            }
        }
        return result;
    }

    public static List<Axis> PruneSemanticOutliers(
        List<Axis> axes, Dictionary<string, float[]> valueEmbeddings)
    {
        if (valueEmbeddings.Count == 0)
        {
            return axes;
        }

        var result = new List<Axis>();
        foreach (var axis in axes)
        {
            var values = axis.Values.ToList();

            // Skip axes where we don't have embeddings for all values
            var allHaveEmbeddings = values.All(v => valueEmbeddings.ContainsKey(v.Label));
            if (!allHaveEmbeddings)
            {
                result.Add(axis);
                continue;
            }

            var surviving = new List<AxisValue>();
            foreach (var value in values)
            {
                var embedding = valueEmbeddings[value.Label];
                var others = values.Where(v => v.Label != value.Label).ToList();

                var avgSimilarity = others
                    .Select(o => VectorMath.CosineSimilarity(embedding, valueEmbeddings[o.Label]))
                    .Average();

                if (avgSimilarity >= SemanticOutlierThreshold)
                {
                    surviving.Add(value);
                }
            }

            if (surviving.Count >= MinAxisValues)
            {
                result.Add(new Axis(axis.Name, surviving));
            }
        }

        return result;
    }

    public static List<Axis> UnifyRedundantAxes(
        List<Axis> axes,
        Dictionary<string, MatchSet> matchSets,
        Dictionary<string, float[]> valueEmbeddings)
    {
        if (axes.Count <= 1)
        {
            return axes;
        }

        // Phase 1: Value-level token pruning
        axes = PruneSubsumedValues(axes);
        if (axes.Count <= 1)
        {
            return axes;
        }

        // Phase 2: Axis-level grouping via Union-Find
        var axisSets = axes.Select(a => GetAxisListingSet(a, matchSets)).ToList();
        var parent = Enumerable.Range(0, axes.Count).ToArray();
        var rank = new int[axes.Count];

        for (var i = 0; i < axes.Count; i++)
        {
            for (var j = i + 1; j < axes.Count; j++)
            {
                if (Find(parent, i) == Find(parent, j))
                {
                    continue;
                }

                if (ShouldMergeAxes(axes[i], axes[j], axisSets[i], axisSets[j], matchSets, valueEmbeddings))
                {
                    Union(parent, rank, i, j);
                }
            }
        }

        // Phase 3: Select representative per group
        var groups = Enumerable.Range(0, axes.Count)
            .GroupBy(i => Find(parent, i));

        var result = new List<Axis>();
        foreach (var group in groups)
        {
            var indices = group.ToList();
            var best = indices
                .OrderBy(i => axes[i].Values.Average(v => v.Label.Split(' ').Length))
                .ThenByDescending(i => axisSets[i].Count)
                .First();
            result.Add(axes[best]);
        }

        return result;
    }

    private static List<Axis> PruneSubsumedValues(List<Axis> axes)
    {
        var sorted = axes
            .Select(a => new
            {
                Axis = a,
                AvgTokens = a.Values.Average(v => v.Label.Split(' ').Length)
            })
            .OrderBy(x => x.AvgTokens)
            .Select(x => x.Axis)
            .ToList();

        var dominantTokenSets = new List<HashSet<string>>();
        var result = new List<Axis>();

        foreach (var axis in sorted)
        {
            var surviving = new List<AxisValue>();

            foreach (var value in axis.Values)
            {
                var valueTokens = new HashSet<string>(value.Label.Split(' '));
                var subsumed = false;

                foreach (var domTokens in dominantTokenSets)
                {
                    if (domTokens.IsSubsetOf(valueTokens) && !domTokens.SetEquals(valueTokens))
                    {
                        subsumed = true;
                        break;
                    }
                }

                if (!subsumed)
                {
                    surviving.Add(value);
                }
            }

            if (surviving.Count >= MinAxisValues)
            {
                result.Add(new Axis(axis.Name, surviving));
                foreach (var v in surviving)
                {
                    dominantTokenSets.Add(new HashSet<string>(v.Label.Split(' ')));
                }
            }
        }

        return result;
    }

    private static bool ShouldMergeAxes(
        Axis a, Axis b,
        HashSet<int> setA, HashSet<int> setB,
        Dictionary<string, MatchSet> matchSets,
        Dictionary<string, float[]> embeddings)
    {
        if (JaccardSimilarity(setA, setB) >= AxisMergeJaccardThreshold)
        {
            return true;
        }

        if (AreAxesContained(a, b, matchSets))
        {
            return true;
        }

        if (embeddings.Count > 0 && AreAxesSemanticallyRedundant(a, b, embeddings))
        {
            return true;
        }

        return false;
    }

    private static bool AreAxesSemanticallyRedundant(
        Axis a, Axis b,
        Dictionary<string, float[]> embeddings)
    {
        var valsA = a.Values.ToList();
        var valsB = b.Values.ToList();
        var (smaller, larger) = valsA.Count <= valsB.Count
            ? (valsA, valsB) : (valsB, valsA);

        var totalBestMatch = 0.0;
        var matched = 0;

        foreach (var sv in smaller)
        {
            if (!embeddings.TryGetValue(sv.Label, out var embA))
            {
                continue;
            }

            var bestSim = 0.0;
            foreach (var lv in larger)
            {
                if (!embeddings.TryGetValue(lv.Label, out var embB))
                {
                    continue;
                }

                var sim = VectorMath.CosineSimilarity(embA, embB);
                if (sim > bestSim)
                {
                    bestSim = sim;
                }
            }

            totalBestMatch += bestSim;
            matched++;
        }

        if (matched == 0)
        {
            return false;
        }

        return totalBestMatch / matched >= EmbeddingMergeThreshold;
    }

    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }
        return x;
    }

    private static void Union(int[] parent, int[] rank, int x, int y)
    {
        var rootX = Find(parent, x);
        var rootY = Find(parent, y);
        if (rootX == rootY)
        {
            return;
        }

        if (rank[rootX] < rank[rootY])
        {
            parent[rootX] = rootY;
        }
        else if (rank[rootX] > rank[rootY])
        {
            parent[rootY] = rootX;
        }
        else
        {
            parent[rootY] = rootX;
            rank[rootX]++;
        }
    }

}
