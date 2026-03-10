using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace AIOMarketMaker.Core.Services.Taxonomy;

public class LlmBrandTokenExtractor : IBrandTokenExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly BinaryData ResponseSchema = BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "tokens": {
                "type": "array",
                "items": { "type": "string" }
            }
        },
        "required": ["tokens"],
        "additionalProperties": false
    }
    """);

    private readonly ChatClient _client;
    private readonly ILogger<LlmBrandTokenExtractor> _logger;

    public LlmBrandTokenExtractor(ChatClient client, ILogger<LlmBrandTokenExtractor> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IEnumerable<string>> Extract(string searchTerm, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return Array.Empty<string>();
        }

        var systemMessage = ChatMessage.CreateSystemMessage(
            """
            You are a product identification expert. Given an eBay search term, extract the minimal set of
            brand or product identifier tokens that distinguish THIS specific product from related but
            different products in search results.

            Rules:
            - Return lowercase tokens only
            - Omit generic words (console, watch, bag, bracelet, ssd, etc.)
            - Omit overly broad brand names if a more specific identifier exists
              (e.g., for "Nike Air Jordan 1" return ["jordan"] not ["nike"] — "nike" appears on all Nike products)
            - Include common abbreviations that eBay sellers use (e.g., "ps5" for PlayStation 5)
            - For electronics with model numbers, include the distinguishing model number tokens
            - Typically 1-3 tokens is enough
            """);

        var userMessage = ChatMessage.CreateUserMessage(
            $"Extract brand/product tokens from: \"{searchTerm}\"");

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                "brand_tokens", ResponseSchema, jsonSchemaIsStrict: true)
        };

        _logger.LogInformation("Extracting brand tokens for search term: {SearchTerm}", searchTerm);

        var completion = await _client.CompleteChatAsync(
            [systemMessage, userMessage], options, ct);

        var responseText = completion.Value.Content[0].Text;
        _logger.LogDebug("Brand token response: {Response}", responseText);

        var response = JsonSerializer.Deserialize<BrandTokenResponse>(responseText, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize brand token response");

        var tokens = response.Tokens
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToList();

        _logger.LogInformation(
            "Extracted brand tokens for '{SearchTerm}': [{Tokens}]",
            searchTerm, string.Join(", ", tokens));

        return tokens;
    }

    private record BrandTokenResponse(string[] Tokens);
}
