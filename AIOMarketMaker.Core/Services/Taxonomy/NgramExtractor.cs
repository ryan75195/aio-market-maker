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

    public Task<IEnumerable<Ngram>> Deduplicate(
        IEnumerable<Ngram> ngrams, CancellationToken ct = default)
    {
        throw new NotImplementedException();
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
}
