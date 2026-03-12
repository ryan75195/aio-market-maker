using System.Text.Json;
using System.Text.RegularExpressions;
using AIOMarketMaker.Core.Services.Taxonomy;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.ML.Services;

public partial class ExtractionModelRunner : IExtractionModelRunner
{
    private const string SystemPrompt =
        "Extract product variant attributes from eBay listing titles. " +
        "Match values from the provided lists against text in the title. " +
        "Return valid JSON with every axis name as a key, " +
        "using null for axes with no matching text in the title.";

    private const string UserTemplate =
        """
        Extract product variant attributes from this eBay listing title.

        Rules:
        - ONLY return values from the provided value lists below
        - A value MUST have supporting text in the title — do not infer or assume defaults
        - Match flexibly: "Gen 4" matches "gen 4", "MkII" matches "mk2", "2 Pack" matches "2-pack"
        - If the listing is for an accessory, part, or unrelated product, return null for ALL axes
        - Include ALL axis names in your JSON response (use null for unmatched)

        Axes:
        {0}

        Title: {1}

        JSON:
        """;

    private readonly LLamaWeights _weights;
    private readonly ModelParams _modelParams;
    private readonly ILogger<ExtractionModelRunner> _logger;
    private readonly ExtractionConfig _config;

    public ExtractionModelRunner(ExtractionConfig config, ILogger<ExtractionModelRunner> logger)
    {
        _config = config;
        _logger = logger;

        if (!File.Exists(config.ModelPath))
        {
            throw new FileNotFoundException(
                $"GGUF extraction model not found at '{config.ModelPath}'.",
                config.ModelPath);
        }

        _modelParams = new ModelParams(config.ModelPath)
        {
            ContextSize = (uint)config.ContextSize,
            GpuLayerCount = config.GpuLayers,
        };

        _weights = LLamaWeights.LoadFromFile(_modelParams);
        _logger.LogInformation("Extraction model loaded from {ModelPath}", config.ModelPath);
    }

    public async Task<Dictionary<string, string?>?> Extract(string title, ExtractionSkeleton skeleton)
    {
        var prompt = FormatChatPrompt(title, skeleton);
        var executor = new StatelessExecutor(_weights, _modelParams);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = _config.MaxTokens,
            AntiPrompts = new[] { "<|im_end|>" },
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.0f },
        };

        var tokens = new List<string>();
        await foreach (var token in executor.InferAsync(prompt, inferenceParams))
        {
            tokens.Add(token);
        }

        return ParseExtraction(string.Join("", tokens));
    }

    public Task<bool> IsHealthy(CancellationToken ct = default)
    {
        return Task.FromResult(_weights != null);
    }

    public static string FormatPrompt(string title, ExtractionSkeleton skeleton)
    {
        var axesDesc = FormatAxesDescription(skeleton);
        return string.Format(UserTemplate, axesDesc, title);
    }

    public static string FormatChatPrompt(string title, ExtractionSkeleton skeleton)
    {
        var userContent = FormatPrompt(title, skeleton);
        return $"<|im_start|>system\n{SystemPrompt}<|im_end|>\n" +
               $"<|im_start|>user\n{userContent}<|im_end|>\n" +
               $"<|im_start|>assistant\n";
    }

    public static Dictionary<string, string?>? ParseExtraction(string raw)
    {
        var text = raw.Trim();

        // Strip markdown code fences if present
        var fenceMatch = JsonFenceRegex().Match(text);
        if (fenceMatch.Success)
        {
            text = fenceMatch.Groups[1].Value.Trim();
        }

        // Find JSON object boundaries
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end < 0 || end <= start)
        {
            return null;
        }

        text = text[start..(end + 1)];

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
            if (parsed == null)
            {
                return null;
            }

            var result = new Dictionary<string, string?>();
            var hasAnyValue = false;

            foreach (var (key, value) in parsed)
            {
                if (value.ValueKind == JsonValueKind.Null)
                {
                    result[key] = null;
                }
                else
                {
                    result[key] = value.GetString();
                    hasAnyValue = true;
                }
            }

            // All-null means accessory/part/unmatched — return null
            return hasAnyValue ? result : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FormatAxesDescription(ExtractionSkeleton skeleton)
    {
        var lines = new List<string>();
        foreach (var axis in skeleton.Axes)
        {
            var values = string.Join(", ", axis.Values.Take(30));
            lines.Add($"- {axis.Name} ({axis.Description}): {values}");
        }
        return string.Join("\n", lines);
    }

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)```")]
    private static partial Regex JsonFenceRegex();

    public void Dispose()
    {
        _weights.Dispose();
    }
}
