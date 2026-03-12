using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace AIOMarketMaker.Core.Services.Taxonomy;

public interface ISkeletonGenerator
{
    Task<ExtractionSkeleton> Generate(
        string searchTerm,
        IEnumerable<string> sampleTitles,
        int totalCount,
        CancellationToken ct = default);
}

public class OpenAiSkeletonGenerator : ISkeletonGenerator
{
    private const string SkeletonPrompt = """
        You are a product taxonomy expert for eBay marketplace analysis.

        Given a product search term and a diverse sample of listing titles, define a taxonomy that classifies listings into comparable groups for pricing.

        ## Product: {0}

        ## Sample titles ({1} diverse titles from {2} total):
        {3}

        ## Instructions

        Define axes (dimensions) that distinguish PRODUCT VARIANTS — things that make two listings NOT directly comparable for pricing. Two listings with the same values on all axes should be the same product at potentially different prices.

        Key principles:

        WHAT TO INCLUDE:
        - Axes that define the product variant: model/reference, size, capacity, color, generation, configuration
        - Include a "reference" axis if the product has model numbers
        - Be EXHAUSTIVE with values — missing a value means listings won't be classified
        - Include values from your product knowledge, not just from the sample titles

        WHAT TO INCLUDE AS AXES (if relevant):
        - Bundle contents: included games, controllers, accessories — these significantly affect price
        - Box/packaging: "boxed", "no box" — affects resale value
        - Completeness: "console only", "with charger", "with all accessories"

        WHAT TO EXCLUDE:
        - Do NOT include listing condition (new/used/sealed/refurbished) — condition is tracked separately
        - Do NOT include seller-specific attributes (free shipping, warranty, returns)

        VALUE FORMAT:
        - Values must be lowercase tokens that can be found in eBay titles
        - Use simple, flat values — NOT compound values. Use separate axes instead.
        - Keep values short (1-3 words max)
        - Aim for 3-6 axes per product (rarely more than 8)

        Return JSON:
        {{"axes": [{{"name": "axis_name", "description": "what it measures", "values": ["val1", "val2"]}}]}}
        """;

    private readonly ChatClient _chatClient;
    private readonly ILogger<OpenAiSkeletonGenerator> _logger;

    public OpenAiSkeletonGenerator(ChatClient chatClient, ILogger<OpenAiSkeletonGenerator> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    public async Task<ExtractionSkeleton> Generate(
        string searchTerm,
        IEnumerable<string> sampleTitles,
        int totalCount,
        CancellationToken ct = default)
    {
        var titlesList = sampleTitles.ToList();
        var titlesText = string.Join("\n", titlesList.Select(t => $"- {t}"));
        var prompt = string.Format(SkeletonPrompt, searchTerm, titlesList.Count, totalCount, titlesText);

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
        };

        var response = await _chatClient.CompleteChatAsync(
            new ChatMessage[]
            {
                new SystemChatMessage("You are a product taxonomy expert. Return only valid JSON."),
                new UserChatMessage(prompt),
            },
            options,
            ct);

        var content = response.Value.Content[0].Text;
        _logger.LogInformation("Skeleton generated for '{SearchTerm}' — {Length} chars", searchTerm, content.Length);

        return ParseSkeletonResponse(content);
    }

    public static ExtractionSkeleton ParseSkeletonResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var axesArray = doc.RootElement.GetProperty("axes");

        var axes = new List<SkeletonAxis>();
        foreach (var axisElement in axesArray.EnumerateArray())
        {
            var name = axisElement.GetProperty("name").GetString()!;
            var description = axisElement.TryGetProperty("description", out var descProp)
                ? descProp.GetString() ?? ""
                : "";
            var values = axisElement.GetProperty("values")
                .EnumerateArray()
                .Select(v => v.GetString()!)
                .ToList();

            axes.Add(new SkeletonAxis(name, description, values));
        }

        return new ExtractionSkeleton(axes);
    }
}
