using System.Text.RegularExpressions;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Core.Services.Taxonomy;

public partial class NgramExtractor : INgramExtractor
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "of", "for", "in", "on", "at", "to",
        "is", "it", "by", "as", "be", "no", "not", "so", "up", "if", "my",
        "new", "free", "with", "this", "that", "from", "was", "are", "has"
    };

    private readonly IEmbeddingService _embeddingService;

    public NgramExtractor(IEmbeddingService embeddingService)
    {
        _embeddingService = embeddingService;
    }

    public IEnumerable<Ngram> Extract(IEnumerable<string> titles)
    {
        var titleList = titles.ToList();
        var count = titleList.Count;
        var minUnigramFrequency = Math.Max(20, count / 200);
        var minBigramFrequency = Math.Max(10, count / 200);

        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var title in titleList)
        {
            var words = ExtractWords(title);
            if (words.Count == 0)
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Unigrams
            foreach (var word in words)
            {
                if (seen.Add(word))
                {
                    frequencies[word] = frequencies.GetValueOrDefault(word) + 1;
                }
            }

            // Bigrams
            for (var i = 0; i < words.Count - 1; i++)
            {
                var bigram = $"{words[i]} {words[i + 1]}";
                if (seen.Add(bigram))
                {
                    frequencies[bigram] = frequencies.GetValueOrDefault(bigram) + 1;
                }
            }

            // Trigrams
            for (var i = 0; i < words.Count - 2; i++)
            {
                var trigram = $"{words[i]} {words[i + 1]} {words[i + 2]}";
                if (seen.Add(trigram))
                {
                    frequencies[trigram] = frequencies.GetValueOrDefault(trigram) + 1;
                }
            }
        }

        return frequencies
            .Where(kvp =>
            {
                var wordCount = kvp.Key.Count(c => c == ' ') + 1;
                var threshold = wordCount == 1 ? minUnigramFrequency : minBigramFrequency;
                return kvp.Value >= threshold;
            })
            .Select(kvp => new Ngram(kvp.Key, new[] { kvp.Key }, kvp.Value));
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

    public async Task<IEnumerable<Ngram>> Deduplicate(
        IEnumerable<Ngram> ngrams, CancellationToken ct = default)
    {
        var ngramList = ngrams.ToList();
        if (ngramList.Count == 0)
        {
            return Enumerable.Empty<Ngram>();
        }

        var texts = ngramList.Select(n => n.Canonical).ToList();
        var vectors = await _embeddingService.GetEmbeddings(texts, ct);

        // L2-normalize
        var normed = vectors.Select(Normalize).ToArray();

        // Union-Find
        var parent = Enumerable.Range(0, ngramList.Count).ToArray();

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(int x, int y)
        {
            var rootX = Find(x);
            var rootY = Find(y);
            if (rootX != rootY)
            {
                parent[rootX] = rootY;
            }
        }

        for (var i = 0; i < ngramList.Count; i++)
        {
            for (var j = i + 1; j < ngramList.Count; j++)
            {
                var similarity = CosineSimilarity(normed[i], normed[j]);
                if (similarity >= 0.95
                    && !AreNumericVariants(texts[i], texts[j]))
                {
                    Union(i, j);
                }
            }
        }

        // Group by root
        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < ngramList.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<int>();
                groups[root] = list;
            }
            list.Add(i);
        }

        return groups.Values.Select(indices =>
        {
            var sorted = indices.OrderByDescending(i => ngramList[i].Frequency).ToList();
            var canonical = ngramList[sorted[0]];
            var totalFrequency = sorted.Sum(i => ngramList[i].Frequency);
            var allForms = sorted.SelectMany(i => ngramList[i].Forms).Distinct().ToList();
            return new Ngram(canonical.Canonical, allForms, totalFrequency);
        });
    }

    private static float[] Normalize(float[] vector)
    {
        var magnitude = MathF.Sqrt(vector.Sum(v => v * v));
        if (magnitude == 0)
        {
            return vector;
        }
        return vector.Select(v => v / magnitude).ToArray();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }
        return dot; // Already normalized, so dot product = cosine similarity
    }

    private static List<string> ExtractWords(string title)
    {
        return WordPattern().Matches(title.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .ToList();
    }

    [GeneratedRegex(@"\b\w+\b")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitPattern();
}
