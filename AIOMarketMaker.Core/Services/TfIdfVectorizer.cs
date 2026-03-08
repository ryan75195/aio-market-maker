namespace AIOMarketMaker.Core.Services;

public record TfIdfConfig(
    int MinNgramSize = 1,
    int MaxNgramSize = 3,
    int MinDocumentFrequency = 5,
    double MaxDocumentFrequencyRatio = 0.8,
    bool SublinearTf = true,
    IEnumerable<string>? StopTokens = null);

public record TfIdfResult(float[][] Vectors, IReadOnlyList<string> FeatureNames);

public interface ITfIdfVectorizer
{
    TfIdfResult FitTransform(IReadOnlyList<string> documents);
}

public class TfIdfVectorizer : ITfIdfVectorizer
{
    private static readonly HashSet<string> DefaultStopTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "free", "shipping", "postage", "delivery", "fast", "tracked", "dispatch", "express", "royal", "mail",
        "new", "brand", "sealed", "mint", "condition", "genuine", "official", "authentic", "factory",
        "the", "a", "an", "and", "or", "of", "for", "with", "in", "on", "to", "from", "by", "at", "is", "it", "this", "that"
    };

    private readonly TfIdfConfig _config;
    private readonly HashSet<string> _stopTokens;

    public TfIdfVectorizer(TfIdfConfig config)
    {
        _config = config;
        _stopTokens = config.StopTokens != null
            ? new HashSet<string>(config.StopTokens, StringComparer.OrdinalIgnoreCase)
            : DefaultStopTokens;
    }

    public TfIdfResult FitTransform(IReadOnlyList<string> documents)
    {
        if (documents.Count == 0)
        {
            return new TfIdfResult(Array.Empty<float[]>(), Array.Empty<string>());
        }

        var tokenizedDocs = documents.Select(Tokenize).ToList();
        var ngramDocs = tokenizedDocs.Select(GenerateNgrams).ToList();

        // Compute document frequencies
        var documentFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ngrams in ngramDocs)
        {
            foreach (var ngram in ngrams.Keys)
            {
                if (!documentFrequency.ContainsKey(ngram))
                {
                    documentFrequency[ngram] = 0;
                }
                documentFrequency[ngram]++;
            }
        }

        // Filter vocabulary by min_df and max_df
        var n = documents.Count;
        var maxDf = _config.MaxDocumentFrequencyRatio * n;
        var vocabulary = documentFrequency
            .Where(kvp => kvp.Value >= _config.MinDocumentFrequency && kvp.Value <= maxDf)
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => kvp.Key)
            .ToList();

        if (vocabulary.Count == 0)
        {
            var emptyVectors = new float[documents.Count][];
            for (var i = 0; i < documents.Count; i++)
            {
                emptyVectors[i] = Array.Empty<float>();
            }
            return new TfIdfResult(emptyVectors, Array.Empty<string>());
        }

        var vocabIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < vocabulary.Count; i++)
        {
            vocabIndex[vocabulary[i]] = i;
        }

        // Compute TF-IDF vectors
        var vectors = new float[documents.Count][];
        for (var docIdx = 0; docIdx < documents.Count; docIdx++)
        {
            var vector = new float[vocabulary.Count];
            var ngrams = ngramDocs[docIdx];

            foreach (var (ngram, count) in ngrams)
            {
                if (!vocabIndex.TryGetValue(ngram, out var idx))
                {
                    continue;
                }

                var tf = _config.SublinearTf
                    ? 1.0f + MathF.Log(count)
                    : (float)count;

                var df = documentFrequency[ngram];
                var idf = MathF.Log((1.0f + n) / (1.0f + df)) + 1.0f;

                vector[idx] = tf * idf;
            }

            L2Normalize(vector);
            vectors[docIdx] = vector;
        }

        return new TfIdfResult(vectors, vocabulary);
    }

    private IReadOnlyList<string> Tokenize(string document)
    {
        // Lowercase, replace non-alphanumeric with space, split, filter
        var normalized = new char[document.Length];
        for (var i = 0; i < document.Length; i++)
        {
            var c = char.ToLowerInvariant(document[i]);
            normalized[i] = char.IsLetterOrDigit(c) ? c : ' ';
        }

        return new string(normalized)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1 && !_stopTokens.Contains(t))
            .ToList();
    }

    private Dictionary<string, int> GenerateNgrams(IReadOnlyList<string> tokens)
    {
        var ngrams = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var n = _config.MinNgramSize; n <= _config.MaxNgramSize; n++)
        {
            for (var i = 0; i <= tokens.Count - n; i++)
            {
                var ngram = string.Join(" ", tokens.Skip(i).Take(n));
                if (!ngrams.ContainsKey(ngram))
                {
                    ngrams[ngram] = 0;
                }
                ngrams[ngram]++;
            }
        }

        return ngrams;
    }

    private static void L2Normalize(float[] vector)
    {
        var magnitude = 0f;
        for (var i = 0; i < vector.Length; i++)
        {
            magnitude += vector[i] * vector[i];
        }

        magnitude = MathF.Sqrt(magnitude);
        if (magnitude == 0)
        {
            return;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= magnitude;
        }
    }
}
