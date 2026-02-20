using System.Text.Json.Serialization;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Utils;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.ML.Services;

public record LlmClassifierConfig(
    int MaxConcurrency = 50,
    int MaxRetries = 3);

[JsonConverter(typeof(CamelCaseEnumConverter))]
public enum Verdict
{
    Same,
    Different,
    Uncertain
}

public record ClassifierResponse(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("verdict")] Verdict Verdict);

public partial class LlmVariantClassifier : IVariantClassifierClient
{
    private const int MaxDescriptionLength = 500;

    private readonly IChatClient _client;
    private readonly SemaphoreSlim _semaphore;
    private readonly int _maxRetries;
    private readonly ILogger<LlmVariantClassifier> _logger;

    public LlmVariantClassifier(IChatClient client, LlmClassifierConfig config, ILogger<LlmVariantClassifier> logger)
    {
        _client = client;
        _semaphore = new SemaphoreSlim(config.MaxConcurrency, config.MaxConcurrency);
        _maxRetries = config.MaxRetries;
        _logger = logger;
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

        _logger.LogInformation("LLM classifier starting {Count} pairs", pairList.Count);
        var tasks = pairList.Select(pair => ClassifyOne(pair, ct));
        var results = await Task.WhenAll(tasks);
        _logger.LogInformation("LLM classifier completed {Count} pairs", pairList.Count);
        return results;
    }

    public Task<bool> IsHealthy(CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    private async Task<PairResult> ClassifyOne(ClassifyPairRequest pair, CancellationToken ct)
    {
        var userPrompt = BuildUserPrompt(pair);
        var response = await WithRetry(() => _client.CompleteChat<ClassifierResponse>(SystemPromptText, userPrompt, ct), ct);

        if (response is null)
        {
            return new PairResult(false, 0.0f);
        }

        return response.Verdict switch
        {
            Verdict.Same => new PairResult(true, 1.0f, response.Reason),
            Verdict.Different => new PairResult(false, 1.0f, response.Reason),
            Verdict.Uncertain => new PairResult(false, 0.5f, response.Reason),
            _ => new PairResult(false, 0.0f)
        };
    }

    private async Task<T?> WithRetry<T>(Func<Task<T?>> action, CancellationToken ct) where T : class
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < _maxRetries; attempt++)
            {
                try
                {
                    return await action();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (attempt < _maxRetries - 1)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        _logger.LogWarning("LLM call failed (attempt {Attempt}/{MaxRetries}): {Error}. Retrying in {Delay}s",
                            attempt + 1, _maxRetries, ex.Message, delay.TotalSeconds);
                        await Task.Delay(delay, ct);
                    }
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }

        _logger.LogError("LLM classifier exhausted retries");
        return null;
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

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text ?? "";
        }

        return text[..maxLength];
    }
}
