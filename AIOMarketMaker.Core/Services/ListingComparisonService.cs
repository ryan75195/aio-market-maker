using System.Text.Json;
using AIOMarketMaker.Core.Data.Models;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace AIOMarketMaker.Core.Services;

public interface IListingComparisonService
{
    Task<ComparableVerdict> Compare(Listing a, Listing b, CancellationToken ct = default);
}

public record ComparableVerdict(bool IsComparable, string Explanation);

public record ListingComparisonConfig(string ApiKey, string Model = "gpt-5-nano");

public class ListingComparisonService : IListingComparisonService
{
    private readonly ChatClient _client;
    private readonly ILogger<ListingComparisonService> _logger;

    public ListingComparisonService(ListingComparisonConfig config, ILogger<ListingComparisonService> logger)
    {
        _client = new ChatClient(config.Model, config.ApiKey);
        _logger = logger;
    }

    public async Task<ComparableVerdict> Compare(Listing a, Listing b, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(a, b);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You are a product comparison expert. Respond only with valid JSON."),
            new UserChatMessage(prompt)
        };

        var completion = await _client.CompleteChatAsync(messages, cancellationToken: ct);
        var responseText = completion.Value.Content[0].Text;

        _logger.LogDebug("LLM response for pair ({IdA}, {IdB}): {Response}", a.Id, b.Id, responseText);

        return ParseResponse(responseText);
    }

    public static string BuildPrompt(Listing a, Listing b)
    {
        return $$"""
            You are comparing two eBay listings to determine if they are the same product for pricing comparison purposes.
            Two listings are "comparable" if a buyer would consider them interchangeable — same product, same model, same key specs.
            Minor differences (color, seller, bundled accessories) are acceptable.

            Listing A:
            - Title: {{a.Title}}
            - Price: {{a.Price}}
            - Condition: {{a.Condition}}
            - Description: {{a.Description}}

            Listing B:
            - Title: {{b.Title}}
            - Price: {{b.Price}}
            - Condition: {{b.Condition}}
            - Description: {{b.Description}}

            Are these the same product (suitable for comparing prices)?
            Respond with JSON only: {"isComparable": true/false, "explanation": "brief reason"}
            """;
    }

    public static ComparableVerdict ParseResponse(string responseText)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;

            var isComparable = root.GetProperty("isComparable").GetBoolean();
            var explanation = root.GetProperty("explanation").GetString() ?? "";

            if (explanation.Length > 500)
            {
                explanation = explanation[..500];
            }

            return new ComparableVerdict(isComparable, explanation);
        }
        catch (Exception ex)
        {
            return new ComparableVerdict(false, $"Failed to parse LLM response: {ex.Message}");
        }
    }
}
