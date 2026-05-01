using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace AIOMarketMaker.Core.Services.Taxonomy;

public class LlmTaxonomyRefiner : ITaxonomyRefiner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly BinaryData ResponseSchema = BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "axes": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "original": { "type": "string" },
                        "name": { "type": "string" },
                        "importance": { "type": "integer" },
                        "remove_values": { "type": "array", "items": { "type": "string" } },
                        "add_values": { "type": "array", "items": { "type": "string" } }
                    },
                    "required": ["original", "name", "importance", "remove_values", "add_values"],
                    "additionalProperties": false
                }
            },
            "merge_axes": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "keep": { "type": "string" },
                        "absorb": { "type": "string" }
                    },
                    "required": ["keep", "absorb"],
                    "additionalProperties": false
                }
            },
            "drop_axes": { "type": "array", "items": { "type": "string" } }
        },
        "required": ["axes", "merge_axes", "drop_axes"],
        "additionalProperties": false
    }
    """);

    private readonly ChatClient _client;
    private readonly ILogger<LlmTaxonomyRefiner> _logger;

    public LlmTaxonomyRefiner(ChatClient client, ILogger<LlmTaxonomyRefiner> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<TaxonomyRefinement> Refine(
        IEnumerable<Axis> axes,
        string productName,
        IEnumerable<string> sampleTitles,
        CancellationToken ct = default)
    {
        var axisList = axes.ToList();
        var titlesList = sampleTitles.Take(20).ToList();

        var systemMessage = ChatMessage.CreateSystemMessage(
            "You are a product taxonomy expert. You refine automatically-discovered product variant axes from eBay listing titles. Return only the JSON delta.");

        var userMessage = ChatMessage.CreateUserMessage(BuildPrompt(axisList, productName, titlesList));

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                "taxonomy_refinement",
                ResponseSchema,
                jsonSchemaIsStrict: true)
        };

        _logger.LogInformation(
            "Refining taxonomy for {ProductName} with {AxisCount} axes and {TitleCount} sample titles",
            productName, axisList.Count, titlesList.Count);

        var completion = await _client.CompleteChatAsync(
            [systemMessage, userMessage], options, ct);

        var responseText = completion.Value.Content[0].Text;
        _logger.LogDebug("LLM response: {Response}", responseText);

        var llmResponse = JsonSerializer.Deserialize<LlmRefinementResponse>(responseText, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize LLM response");

        return MapToRefinement(llmResponse);
    }

    private static string BuildPrompt(List<Axis> axes, string productName, List<string> titles)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"## Product: {productName}");
        sb.AppendLine();

        sb.AppendLine("## Current axes (auto-discovered from listing titles)");
        sb.AppendLine();
        foreach (var axis in axes)
        {
            var valueLabels = axis.Values.Select(v =>
            {
                var maxFreq = v.Ngrams.Any() ? v.Ngrams.Max(n => n.Frequency) : 0;
                return maxFreq > 0 ? $"{v.Label} (freq={maxFreq})" : v.Label;
            }).ToList();
            sb.AppendLine($"- **{axis.Name}**: [{string.Join(", ", valueLabels)}]");
        }
        sb.AppendLine();

        sb.AppendLine("## Sample listing titles");
        sb.AppendLine();
        foreach (var title in titles)
        {
            sb.AppendLine($"- {title}");
        }
        sb.AppendLine();

        sb.AppendLine("## Instructions");
        sb.AppendLine();
        sb.AppendLine("""
            Refine these automatically-discovered axes into a clean product taxonomy. Return a JSON delta with your changes.

            For each axis that should SURVIVE (not be dropped or merged away), you MUST include an entry in the "axes" array with:

            1. **original**: The exact original axis name (e.g. "Axis 0", "Axis 7") — must match one of the axes listed above.
            2. **name**: A short, descriptive human-readable name for this axis. Use lowercase. Good examples: "edition", "color", "storage", "condition", "bundle contents". Bad examples: "Axis 0", "variant", "type", "miscellaneous". The name should describe the single concept this axis represents.
            3. **importance**: An integer from 1 to 5 indicating how much this axis affects the product's market value and how important it is for distinguishing variants:
               - 5 = Critical differentiator (e.g. storage capacity for phones, edition/model for consoles)
               - 4 = Major differentiator (e.g. condition, color for fashion items)
               - 3 = Moderate differentiator (e.g. color for electronics, bundle contents)
               - 2 = Minor differentiator (e.g. seller region, minor accessories)
               - 1 = Negligible differentiator (e.g. listing format, shipping method)
            4. **remove_values**: Values that don't belong on this axis. Remove values that:
               - Represent a different concept than the axis name (e.g. "bundle" on a "color" axis)
               - Are too vague or generic (e.g. "edition" as a value on an "edition" axis)
               - Are duplicates of other values on the same axis
               - **Do NOT remove values with high frequency.** High-frequency values (shown as "freq=N") are statistically validated sub-product variants. Even if a value like "baby" seems vague on its own, it may represent a distinct product line (e.g. "Baby Love" bracelet). Keep these values.
            5. **add_values**: Additional values visible in the sample titles that are missing from this axis. Only add values you can see in the titles above. Use lowercase.

            **Dropping axes**: Add axis names to "drop_axes" if the axis:
            - Mixes multiple unrelated concepts (e.g. an axis containing both "bundle" and "new" mixes bundle-contents with condition)
            - Contains only noise or irrelevant values
            - Has values that don't help distinguish product variants

            **Merging axes**: Add entries to "merge_axes" if two axes represent the same concept. Specify which to "keep" (the better-named one) and which to "absorb".

            **Rules**:
            - Every surviving axis (not dropped, not the "absorb" side of a merge) MUST have an entry in the "axes" array.
            - Only add values you can actually see in the sample listing titles.
            - Keep values as lowercase strings that match tokens visible in the titles.
            - An axis that mixes unrelated concepts (e.g. condition + bundle type) should be dropped, not renamed.
            - Prefer fewer, cleaner axes over many noisy ones.
            """);

        return sb.ToString();
    }

    private static TaxonomyRefinement MapToRefinement(LlmRefinementResponse response)
    {
        var refinedAxes = response.Axes.Select(a => new RefinedAxis(
            a.Original,
            a.Name,
            a.Importance,
            a.RemoveValues,
            a.AddValues));

        var merges = response.MergeAxes.Select(m => new AxisMerge(m.Keep, m.Absorb));

        return new TaxonomyRefinement(refinedAxes, merges, response.DropAxes);
    }

    private record LlmRefinementResponse(
        [property: JsonPropertyName("axes")] LlmRefinedAxis[] Axes,
        [property: JsonPropertyName("merge_axes")] LlmAxisMerge[] MergeAxes,
        [property: JsonPropertyName("drop_axes")] string[] DropAxes);

    private record LlmRefinedAxis(
        [property: JsonPropertyName("original")] string Original,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("importance")] int Importance,
        [property: JsonPropertyName("remove_values")] string[] RemoveValues,
        [property: JsonPropertyName("add_values")] string[] AddValues);

    private record LlmAxisMerge(
        [property: JsonPropertyName("keep")] string Keep,
        [property: JsonPropertyName("absorb")] string Absorb);
}
