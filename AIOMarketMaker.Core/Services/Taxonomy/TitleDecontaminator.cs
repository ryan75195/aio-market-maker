using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services.Taxonomy;

public partial class TitleDecontaminator : ITitleDecontaminator
{
    private const double SimilarityThreshold = 0.60;

    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<TitleDecontaminator> _logger;

    public TitleDecontaminator(IEmbeddingService embeddingService, ILogger<TitleDecontaminator> logger)
    {
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<DecontaminationResult> Filter(
        IEnumerable<string> titles,
        string? productName,
        IEnumerable<string>? brandTokens = null,
        CancellationToken ct = default)
    {
        var titleList = titles.ToList();
        var tokenList = brandTokens?.ToList();
        var hasBrandTokens = tokenList is { Count: > 0 };

        // Track which original indices survive each filter stage
        var surviving = Enumerable.Range(0, titleList.Count).ToList();

        // Stage 1: Brand token check
        if (hasBrandTokens)
        {
            var tokenSet = new HashSet<string>(tokenList!, StringComparer.OrdinalIgnoreCase);
            surviving = surviving
                .Where(i => TitleContainsAnyToken(titleList[i], tokenSet))
                .ToList();

            var brandExcluded = titleList.Count - surviving.Count;
            if (brandExcluded > 0)
            {
                _logger.LogInformation(
                    "Brand token check excluded {Count} of {Total} titles",
                    brandExcluded, titleList.Count);
            }
        }

        // Stage 2: Embedding similarity check
        if (productName != null && surviving.Count > 0)
        {
            var productEmbedding = VectorMath.Normalize(
                await _embeddingService.GetEmbedding(productName, ct, EmbeddingModel.Small));

            var survivingTitles = surviving.Select(i => titleList[i]).ToList();
            var titleEmbeddings = await _embeddingService.GetEmbeddings(
                survivingTitles, ct, EmbeddingModel.Small);

            var passingSimilarity = new List<int>();
            for (var j = 0; j < surviving.Count; j++)
            {
                var normalized = VectorMath.Normalize(titleEmbeddings[j]);
                var similarity = VectorMath.CosineSimilarity(productEmbedding, normalized);
                if (similarity >= SimilarityThreshold)
                {
                    passingSimilarity.Add(surviving[j]);
                }
                else
                {
                    _logger.LogDebug(
                        "Embedding similarity {Similarity:F3} below threshold for: {Title}",
                        similarity, titleList[surviving[j]]);
                }
            }

            var embeddingExcluded = surviving.Count - passingSimilarity.Count;
            if (embeddingExcluded > 0)
            {
                _logger.LogInformation(
                    "Embedding similarity check excluded {Count} of {Total} surviving titles",
                    embeddingExcluded, surviving.Count);
            }

            surviving = passingSimilarity;
        }

        // Build result with index mapping
        var filteredTitles = surviving.Select(i => titleList[i]).ToList();
        var indexMapping = new Dictionary<int, int>();
        for (var newIdx = 0; newIdx < surviving.Count; newIdx++)
        {
            indexMapping[newIdx] = surviving[newIdx];
        }

        var excludedCount = titleList.Count - surviving.Count;
        return new DecontaminationResult(filteredTitles, indexMapping, excludedCount);
    }

    private static bool TitleContainsAnyToken(string title, HashSet<string> tokens)
    {
        var titleTokens = TokenizeTitle(title);
        return titleTokens.Overlaps(tokens);
    }

    private static HashSet<string> TokenizeTitle(string title)
    {
        return new HashSet<string>(
            WordPattern().Matches(title.ToLowerInvariant()).Select(m => m.Value)
                .Where(w => w.Length > 1),
            StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\b\w+\b")]
    private static partial Regex WordPattern();
}
