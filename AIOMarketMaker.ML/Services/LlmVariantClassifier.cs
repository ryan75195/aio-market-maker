using System.Text.Json;
using System.Text.RegularExpressions;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace AIOMarketMaker.ML.Services;

public record LlmClassifierConfig(
    string ApiKey,
    string Model = "gpt-4o-mini",
    int MaxConcurrency = 50,
    int MaxRetries = 3);

public class LlmVariantClassifier : IVariantClassifierClient
{
    private const int MaxDescriptionLength = 500;

    private static readonly string SystemPrompt = """
        You are a product variant classifier for eBay listings. Given two listings, determine if they are the SAME VARIANT of a product — meaning a buyer would consider them interchangeable for pricing purposes.

        RULES:
        1. SAME VARIANT means: same product, same model, same key specs (storage, size, color when it affects price).
        2. CONDITION DOES NOT MATTER. A "Grade A" and a "Grade C" of the same product ARE the same variant. "New" vs "Used" does NOT make them different variants. Only "for parts/not working" vs "working" is a meaningful difference.
        3. BUNDLES are DIFFERENT. If one listing includes accessories (keyboard, case, controller, extra lenses) that the other does not, they are DIFFERENT variants — the bundle commands a higher price.
        4. SPECIAL EDITIONS are DIFFERENT. Limited editions, collaboration colorways (e.g., Pokemon Edition Switch vs standard Switch OLED), anniversary editions are different variants.
        5. STORAGE/RAM/CPU differences make them DIFFERENT (e.g., 128GB vs 256GB, i5 vs i7, M3 vs M3 Pro).
        6. SIZE differences make them DIFFERENT (e.g., 40mm vs 44mm watch, PM vs MM bag).
        7. ACCESSORIES vs FULL PRODUCTS are DIFFERENT (e.g., "PS5 Disc Drive" accessory vs "PS5 Console").
        8. TRIVIAL differences are OK: seller location, box condition, minor cosmetic wear, included cables, listing photos.

        Respond with JSON only: {"verdict": "same" or "different", "reason": "brief explanation (max 20 words)"}
        """;

    private readonly ChatClient _client;
    private readonly SemaphoreSlim _semaphore;
    private readonly int _maxRetries;
    private readonly ILogger<LlmVariantClassifier> _logger;

    public LlmVariantClassifier(LlmClassifierConfig config, ILogger<LlmVariantClassifier> logger)
    {
        _client = new ChatClient(config.Model, config.ApiKey);
        _semaphore = new SemaphoreSlim(config.MaxConcurrency, config.MaxConcurrency);
        _maxRetries = config.MaxRetries;
        _logger = logger;

        _logger.LogInformation("LLM variant classifier initialized with model {Model}, concurrency {MaxConcurrency}",
            config.Model, config.MaxConcurrency);
    }

    public async Task<IReadOnlyList<PairResult>> Classify(
        IEnumerable<ClassifyPairRequest> pairs,
        CancellationToken ct = default)
    {
        var pairList = pairs.ToList();
        if (pairList.Count == 0)
        {
            return Array.Empty<PairResult>();
        }

        var tasks = pairList.Select((pair, index) => ClassifyOne(pair, index, pairList.Count, ct));
        var results = await Task.WhenAll(tasks);
        return results;
    }

    public Task<bool> IsHealthy(CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    private async Task<PairResult> ClassifyOne(
        ClassifyPairRequest pair, int index, int total, CancellationToken ct)
    {
        var userPrompt = BuildUserPrompt(pair);

        await _semaphore.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < _maxRetries; attempt++)
            {
                try
                {
                    var messages = new List<ChatMessage>
                    {
                        new SystemChatMessage(SystemPrompt),
                        new UserChatMessage(userPrompt)
                    };

                    var completion = await _client.CompleteChatAsync(messages, cancellationToken: ct);
                    var responseText = completion.Value.Content[0].Text;
                    var result = ParseResponse(responseText);

                    if ((index + 1) % 100 == 0 || index == total - 1)
                    {
                        _logger.LogInformation("LLM classifier progress: {Current}/{Total}", index + 1, total);
                    }

                    return result;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < _maxRetries - 1)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    _logger.LogWarning("LLM call failed (attempt {Attempt}/{MaxRetries}): {Error}. Retrying in {Delay}s",
                        attempt + 1, _maxRetries, ex.Message, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }

        _logger.LogError("LLM classifier exhausted retries for pair at index {Index}", index);
        return new PairResult(false, 0.0f);
    }

    public static string BuildUserPrompt(ClassifyPairRequest pair)
    {
        var descA = Truncate(pair.DescriptionA, MaxDescriptionLength);
        var descB = Truncate(pair.DescriptionB, MaxDescriptionLength);

        return $"""
            Listing A:
            Title: {pair.TitleA}
            Description: {descA}

            Listing B:
            Title: {pair.TitleB}
            Description: {descB}
            """;
    }

    public static PairResult ParseResponse(string responseText)
    {
        try
        {
            var json = ExtractJson(responseText);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var verdict = root.GetProperty("verdict").GetString() ?? "";
            var isComparable = verdict.Equals("same", StringComparison.OrdinalIgnoreCase);

            return new PairResult(isComparable, 1.0f);
        }
        catch
        {
            return new PairResult(false, 0.0f);
        }
    }

    private static string ExtractJson(string text)
    {
        var trimmed = text.Trim();

        var match = MarkdownCodeBlock.Match(trimmed);
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        return trimmed;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text ?? "";
        }

        return text[..maxLength];
    }

    private static readonly Regex MarkdownCodeBlock = new(@"```(?:json)?\s*([\s\S]*?)```", RegexOptions.Compiled);
}
