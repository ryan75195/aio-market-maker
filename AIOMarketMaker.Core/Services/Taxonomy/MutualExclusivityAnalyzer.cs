namespace AIOMarketMaker.Core.Services.Taxonomy;

public class MutualExclusivityAnalyzer : IMutualExclusivityAnalyzer
{
    public IEnumerable<MatchSet> ComputeMatchSets(
        IEnumerable<string> titles, IEnumerable<Ngram> ngrams)
    {
        var titleList = titles.Select(t => t.ToLowerInvariant()).ToList();
        var ngramList = ngrams.ToList();

        // Phase 1: Compute raw match sets
        var mutableSets = ngramList.Select(ngram =>
        {
            var indices = new HashSet<int>();
            for (var i = 0; i < titleList.Count; i++)
            {
                if (ngram.Forms.Any(form => PatternMatches(form, titleList[i])))
                {
                    indices.Add(i);
                }
            }
            return (ngram, indices);
        }).ToList();

        // Phase 2: Longest-match — remove title from shorter ngram's set
        // when it also matches a longer ngram whose tokens are a superset
        ApplyLongestMatch(mutableSets);

        return mutableSets.Select(ms => new MatchSet(ms.ngram, ms.indices));
    }

    private static void ApplyLongestMatch(
        List<(Ngram ngram, HashSet<int> indices)> matchSets)
    {
        var tokenSets = matchSets.Select(ms =>
            new HashSet<string>(
                ms.ngram.Canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase))
            .ToList();

        for (var shorter = 0; shorter < matchSets.Count; shorter++)
        {
            for (var longer = 0; longer < matchSets.Count; longer++)
            {
                if (shorter == longer)
                {
                    continue;
                }

                if (tokenSets[shorter].Count >= tokenSets[longer].Count)
                {
                    continue;
                }

                if (!tokenSets[shorter].IsSubsetOf(tokenSets[longer]))
                {
                    continue;
                }

                // shorter's tokens are a subset of longer's tokens
                // Remove any title that also appears in the longer match set
                matchSets[shorter].indices.ExceptWith(matchSets[longer].indices);
            }
        }
    }

    public IEnumerable<MutuallyExclusivePair> FindExclusivePairs(
        IEnumerable<MatchSet> matchSets, double threshold = 0.05)
    {
        var setList = matchSets.ToList();
        var result = new List<MutuallyExclusivePair>();

        for (var i = 0; i < setList.Count; i++)
        {
            if (setList[i].ListingIndices.Count == 0)
            {
                continue;
            }

            for (var j = i + 1; j < setList.Count; j++)
            {
                if (setList[j].ListingIndices.Count == 0)
                {
                    continue;
                }

                var intersection = setList[i].ListingIndices
                    .Count(idx => setList[j].ListingIndices.Contains(idx));
                var minSize = Math.Min(
                    setList[i].ListingIndices.Count,
                    setList[j].ListingIndices.Count);
                var overlap = (double)intersection / minSize;

                if (overlap < threshold)
                {
                    result.Add(new MutuallyExclusivePair(
                        setList[i].Ngram, setList[j].Ngram, overlap));
                }
            }
        }

        return result;
    }

    internal static bool PatternMatches(string pattern, string titleLower)
    {
        if (pattern.Contains(' '))
        {
            return titleLower.Contains(pattern, StringComparison.Ordinal);
        }

        // Word-boundary match without regex allocation
        var idx = titleLower.IndexOf(pattern, StringComparison.Ordinal);
        while (idx >= 0)
        {
            var before = idx == 0 || !char.IsLetterOrDigit(titleLower[idx - 1]);
            var end = idx + pattern.Length;
            var after = end >= titleLower.Length || !char.IsLetterOrDigit(titleLower[end]);

            if (before && after)
            {
                return true;
            }

            idx = titleLower.IndexOf(pattern, idx + 1, StringComparison.Ordinal);
        }

        return false;
    }
}
