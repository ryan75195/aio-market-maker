using System.Text.RegularExpressions;

namespace AIOMarketMaker.Core.Services.Taxonomy;

public class MutualExclusivityAnalyzer : IMutualExclusivityAnalyzer
{
    public IEnumerable<MatchSet> ComputeMatchSets(
        IEnumerable<string> titles, IEnumerable<Ngram> ngrams)
    {
        var titleList = titles.Select(t => t.ToLowerInvariant()).ToList();
        var ngramList = ngrams.ToList();

        return ngramList.Select(ngram =>
        {
            var indices = new HashSet<int>();
            for (var i = 0; i < titleList.Count; i++)
            {
                if (ngram.Forms.Any(form => PatternMatches(form, titleList[i])))
                {
                    indices.Add(i);
                }
            }
            return new MatchSet(ngram, indices);
        });
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
                        setList[i].Ngram, setList[j].Ngram, overlap, 0.0));
                }
            }
        }

        return result;
    }

    internal static bool PatternMatches(string pattern, string titleLower)
    {
        if (!pattern.Contains(' '))
        {
            return Regex.IsMatch(titleLower, $@"\b{Regex.Escape(pattern)}\b");
        }
        return titleLower.Contains(pattern, StringComparison.Ordinal);
    }
}
