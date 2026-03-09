using System.Text.RegularExpressions;

namespace AIOMarketMaker.Core.Services.Taxonomy;

public partial class NgramExtractor : INgramExtractor
{
    private const int MinUnigramFrequency = 20;
    private const int MinBigramFrequency = 10;
    private const int FrequencyScaleDivisor = 200;
    private const double SynonymSimilarityThreshold = 0.80;
    private const double SubsumptionSimilarityThreshold = 0.80;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "of", "for", "in", "on", "at", "to",
        "is", "it", "by", "as", "be", "no", "not", "so", "up", "if", "my",
        "this", "that", "from", "was", "are", "has"
    };

    private readonly IEmbeddingService _embeddingService;
    private readonly INlpToolkit _nlpToolkit;

    public NgramExtractor(IEmbeddingService embeddingService, INlpToolkit? nlpToolkit = null)
    {
        _embeddingService = embeddingService;
        _nlpToolkit = nlpToolkit ?? new NlpToolkit();
    }

    public IEnumerable<RawNgram> Extract(IEnumerable<string> titles, string? searchTerm = null)
    {
        var titleList = titles.ToList();
        var searchTokens = BuildSearchTokens(searchTerm);
        var frequencies = CountNgramFrequencies(titleList, searchTokens);
        return FilterByFrequency(frequencies, titleList.Count);
    }

    private HashSet<string> BuildSearchTokens(string? searchTerm)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (searchTerm == null)
        {
            return tokens;
        }

        foreach (var word in searchTerm.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var lower = word.ToLowerInvariant();
            if (lower.Length > 1 && !StopWords.Contains(lower))
            {
                tokens.Add(_nlpToolkit.Singularize(lower));
            }
        }

        return tokens;
    }

    public async Task<IEnumerable<Ngram>> MergeSynonyms(
        IEnumerable<RawNgram> rawNgrams, CancellationToken ct = default)
    {
        var ngramList = rawNgrams.ToList();
        if (ngramList.Count == 0)
        {
            return Enumerable.Empty<Ngram>();
        }

        var texts = ngramList.Select(n => n.Term).ToList();
        var clusters = await ClusterBySimilarity(texts, ct);
        return BuildMergedNgrams(ngramList, clusters);
    }

    public async Task<IEnumerable<Ngram>> SubsumeByTokenOverlap(
        IEnumerable<Ngram> ngrams, CancellationToken ct = default)
    {
        var ngramList = ngrams.ToList();
        if (ngramList.Count <= 1)
        {
            return ngramList;
        }

        var canonicals = ngramList.Select(n => n.Canonical).ToList();
        var vectors = await _embeddingService.GetEmbeddings(canonicals, ct, EmbeddingModel.Small);
        var normed = vectors.Select(VectorMath.Normalize).ToArray();

        var subsumed = new bool[ngramList.Count];
        var subsumptionTarget = new int[ngramList.Count];
        for (var i = 0; i < subsumptionTarget.Length; i++)
        {
            subsumptionTarget[i] = -1;
        }

        for (var i = 0; i < ngramList.Count; i++)
        {
            if (subsumed[i])
            {
                continue;
            }

            var tokensI = TokenizeCanonical(ngramList[i].Canonical);

            for (var j = 0; j < ngramList.Count; j++)
            {
                if (i == j || subsumed[j])
                {
                    continue;
                }

                var tokensJ = TokenizeCanonical(ngramList[j].Canonical);

                if (tokensI.Count >= tokensJ.Count)
                {
                    continue;
                }

                // Don't let single-word ngrams subsume multi-word ngrams.
                // Compound phrases like "gold plated" represent distinct
                // categories from their component unigrams like "gold".
                if (tokensI.Count == 1 && tokensJ.Count >= 2)
                {
                    continue;
                }

                if (!tokensI.All(t => tokensJ.Contains(t)))
                {
                    continue;
                }

                var similarity = VectorMath.CosineSimilarity(normed[i], normed[j]);
                if (similarity >= SubsumptionSimilarityThreshold)
                {
                    subsumed[j] = true;
                    subsumptionTarget[j] = i;
                }
            }
        }

        var result = new List<Ngram>();
        for (var i = 0; i < ngramList.Count; i++)
        {
            if (subsumed[i])
            {
                continue;
            }

            var allForms = ngramList[i].Forms.ToList();
            var totalFreq = ngramList[i].Frequency;

            for (var j = 0; j < ngramList.Count; j++)
            {
                if (subsumptionTarget[j] == i)
                {
                    allForms.AddRange(ngramList[j].Forms);
                    totalFreq += ngramList[j].Frequency;
                }
            }

            result.Add(new Ngram(ngramList[i].Canonical, allForms.Distinct(), totalFreq));
        }

        return result;
    }

    private static HashSet<string> TokenizeCanonical(string text)
    {
        return new HashSet<string>(
            text.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    public static bool AreNumericVariants(string a, string b)
    {
        var templateA = DigitPattern().Replace(a, "#");
        var templateB = DigitPattern().Replace(b, "#");

        if (templateA != templateB)
        {
            return false;
        }

        if (!templateA.Contains('#'))
        {
            return false;
        }

        var digitsA = DigitPattern().Matches(a).Select(m => m.Value).ToList();
        var digitsB = DigitPattern().Matches(b).Select(m => m.Value).ToList();

        return !digitsA.SequenceEqual(digitsB);
    }

    private Dictionary<string, int> CountNgramFrequencies(List<string> titles, HashSet<string> searchTokens)
    {
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var title in titles)
        {
            var words = Tokenize(title, searchTokens);
            if (words.Count == 0)
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            CountUnigrams(words, seen, frequencies);
            CountBigrams(words, seen, frequencies);
            CountTrigrams(words, seen, frequencies);
        }

        return frequencies;
    }

    private static void CountUnigrams(
        List<string> words, HashSet<string> seen, Dictionary<string, int> frequencies)
    {
        foreach (var word in words)
        {
            if (seen.Add(word))
            {
                frequencies[word] = frequencies.GetValueOrDefault(word) + 1;
            }
        }
    }

    private static void CountBigrams(
        List<string> words, HashSet<string> seen, Dictionary<string, int> frequencies)
    {
        for (var i = 0; i < words.Count - 1; i++)
        {
            var bigram = $"{words[i]} {words[i + 1]}";
            if (seen.Add(bigram))
            {
                frequencies[bigram] = frequencies.GetValueOrDefault(bigram) + 1;
            }
        }
    }

    private static void CountTrigrams(
        List<string> words, HashSet<string> seen, Dictionary<string, int> frequencies)
    {
        for (var i = 0; i < words.Count - 2; i++)
        {
            var trigram = $"{words[i]} {words[i + 1]} {words[i + 2]}";
            if (seen.Add(trigram))
            {
                frequencies[trigram] = frequencies.GetValueOrDefault(trigram) + 1;
            }
        }
    }

    private static IEnumerable<RawNgram> FilterByFrequency(
        Dictionary<string, int> frequencies, int titleCount)
    {
        var minUnigram = Math.Max(MinUnigramFrequency, titleCount / FrequencyScaleDivisor);
        var minBigram = Math.Max(MinBigramFrequency, titleCount / FrequencyScaleDivisor);

        return frequencies
            .Where(kvp =>
            {
                var wordCount = kvp.Key.Count(c => c == ' ') + 1;
                var threshold = wordCount == 1 ? minUnigram : minBigram;
                return kvp.Value >= threshold;
            })
            .Select(kvp => new RawNgram(kvp.Key, kvp.Value));
    }

    private async Task<IEnumerable<IReadOnlyList<int>>> ClusterBySimilarity(
        List<string> texts, CancellationToken ct)
    {
        var vectors = await _embeddingService.GetEmbeddings(texts, ct, EmbeddingModel.Small);
        var normed = vectors.Select(VectorMath.Normalize).ToArray();

        var uf = new UnionFind(texts.Count);

        for (var i = 0; i < texts.Count; i++)
        {
            for (var j = i + 1; j < texts.Count; j++)
            {
                var similarity = VectorMath.CosineSimilarity(normed[i], normed[j]);
                if (similarity >= SynonymSimilarityThreshold
                    && !AreNumericVariants(texts[i], texts[j]))
                {
                    uf.Union(i, j);
                }
            }
        }

        return uf.GetGroups();
    }

    private static IEnumerable<Ngram> BuildMergedNgrams(
        List<RawNgram> ngramList, IEnumerable<IReadOnlyList<int>> clusters)
    {
        return clusters.Select(indices =>
        {
            var sorted = indices.OrderByDescending(i => ngramList[i].Frequency).ToList();
            var canonical = ngramList[sorted[0]].Term;
            var totalFrequency = sorted.Sum(i => ngramList[i].Frequency);
            var allForms = sorted.Select(i => ngramList[i].Term).Distinct().ToList();
            return new Ngram(canonical, allForms, totalFrequency);
        });
    }

    private List<string> Tokenize(string title, HashSet<string>? searchTokens = null)
    {
        return WordPattern().Matches(title.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .Select(w => _nlpToolkit.Singularize(w))
            .Where(w => searchTokens == null || searchTokens.Count == 0 || !searchTokens.Contains(w))
            .ToList();
    }

    [GeneratedRegex(@"\b\w+\b")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitPattern();
}

internal class UnionFind
{
    private readonly int[] _parent;

    public UnionFind(int size)
    {
        _parent = Enumerable.Range(0, size).ToArray();
    }

    public int Find(int x)
    {
        while (_parent[x] != x)
        {
            _parent[x] = _parent[_parent[x]];
            x = _parent[x];
        }
        return x;
    }

    public void Union(int x, int y)
    {
        var rootX = Find(x);
        var rootY = Find(y);
        if (rootX != rootY)
        {
            _parent[rootX] = rootY;
        }
    }

    public IEnumerable<IReadOnlyList<int>> GetGroups()
    {
        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < _parent.Length; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<int>();
                groups[root] = list;
            }
            list.Add(i);
        }
        return groups.Values;
    }
}
