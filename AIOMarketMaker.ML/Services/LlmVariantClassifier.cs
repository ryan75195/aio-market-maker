using System.Text.Json;
using System.Text.RegularExpressions;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.ML.Services;

public record LlmClassifierConfig(
    int MaxConcurrency = 50,
    int MaxRetries = 3);

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
        var responseText = await WithRetry(() => SendChat(userPrompt, ct), ct);
        return responseText is not null ? ParseResponse(responseText) : new PairResult(false, 0.0f);
    }

    private Task<string> SendChat(string userPrompt, CancellationToken ct)
    {
        return _client.CompleteChat(SystemPrompt, userPrompt, ct);
    }

    private async Task<string?> WithRetry(Func<Task<string>> action, CancellationToken ct)
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
