using System.Collections.Concurrent;
using System.Text.Json;
using AIOMarketMaker.Core.Configuration;
using AIOMarketMaker.Core.Services.VectorSearch;
using AIOMarketMaker.Models.Ebay;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace AIOMarketMaker.Core.Services.EntityResolution;

/// <summary>
/// Entity resolution service using OpenAI GPT models.
/// Throws exceptions on failure - no fallbacks.
/// </summary>
public class OpenAiEntityResolutionService : IEntityResolutionService
{
    private readonly OpenAIClient _client;
    private readonly OpenAiSettings _settings;
    private readonly PromptBuilder _promptBuilder;
    private readonly IProductNameIndexer _productNameIndexer;
    private readonly ILogger<OpenAiEntityResolutionService> _logger;
    private readonly SemaphoreSlim _semaphore;

    public OpenAiEntityResolutionService(
        OpenAIClient client,
        OpenAiSettings settings,
        PromptBuilder promptBuilder,
        IProductNameIndexer productNameIndexer,
        ILogger<OpenAiEntityResolutionService> logger)
    {
        _client = client;
        _settings = settings;
        _promptBuilder = promptBuilder;
        _productNameIndexer = productNameIndexer;
        _logger = logger;
        _semaphore = new SemaphoreSlim(settings.MaxConcurrency);
    }

    public async Task<IReadOnlyList<EntityResolutionResult>> Resolve(
        IReadOnlyList<EbayProduct> products,
        CancellationToken ct = default)
    {
        if (products.Count == 0)
            return [];

        var results = new ConcurrentDictionary<string, EntityResolutionResult>();

        _logger.LogInformation(
            "Processing {Count} products individually (max {Concurrency} parallel)",
            products.Count, _settings.MaxConcurrency);

        // Find similar product names for all products in one batch call
        var listings = products
            .Where(p => p.ListingId != null && !string.IsNullOrEmpty(p.Title))
            .Select(p => (p.ListingId!, p.Title!))
            .ToList();

        var similarNames = await _productNameIndexer.FindSimilarProductNamesAsync(listings, ct);

        if (similarNames.Count > 0)
        {
            _logger.LogInformation("Found similar product names for {Count}/{Total} listings",
                similarNames.Count, products.Count);
        }

        // Process each product in parallel with concurrency limit
        var tasks = products.Select(product =>
            ProcessSingleProductAsync(product, similarNames, results, ct)).ToList();

        await Task.WhenAll(tasks);

        // Validate we got results for all products
        var missingIds = products
            .Where(p => p.ListingId != null && !results.ContainsKey(p.ListingId))
            .Select(p => p.ListingId)
            .ToList();

        if (missingIds.Count > 0)
        {
            throw new EntityResolutionException(
                $"Entity resolution failed: missing results for {missingIds.Count} listings: {string.Join(", ", missingIds.Take(5))}");
        }

        // Return results in original order
        return products
            .Where(p => p.ListingId != null && results.ContainsKey(p.ListingId))
            .Select(p => results[p.ListingId!])
            .ToList();
    }

    private async Task ProcessSingleProductAsync(
        EbayProduct product,
        IReadOnlyDictionary<string, IReadOnlyList<SimilarProductName>> similarNames,
        ConcurrentDictionary<string, EntityResolutionResult> results,
        CancellationToken ct)
    {
        if (product.ListingId == null)
            return;

        await _semaphore.WaitAsync(ct);
        try
        {
            var result = await CallLlmForSingleProductAsync(product, similarNames, ct);
            if (result != null)
            {
                results[product.ListingId] = result;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<EntityResolutionResult?> CallLlmForSingleProductAsync(
        EbayProduct product,
        IReadOnlyDictionary<string, IReadOnlyList<SimilarProductName>> similarNames,
        CancellationToken ct)
    {
        // Get similar names for this specific product
        IReadOnlyList<SimilarProductName>? productSimilarNames = null;
        if (product.ListingId != null && similarNames.TryGetValue(product.ListingId, out var similar))
        {
            productSimilarNames = similar;
        }

        var userPrompt = _promptBuilder.BuildSingleProductPrompt(product, productSimilarNames);

        Exception? lastException = null;

        for (int attempt = 1; attempt <= _settings.MaxRetries; attempt++)
        {
            try
            {
                _logger.LogDebug("OpenAI request for {ListingId} attempt {Attempt}/{MaxRetries}",
                    product.ListingId, attempt, _settings.MaxRetries);

                var chatClient = _client.GetChatClient(_settings.Model);

                var messages = new List<ChatMessage>
                {
                    new SystemChatMessage(_promptBuilder.SingleProductSystemPrompt),
                    new UserChatMessage(userPrompt)
                };

                var options = new ChatCompletionOptions
                {
                    ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
                };

                var response = await chatClient.CompleteChatAsync(messages, options, ct);

                var content = response.Value.Content[0].Text;
                _logger.LogDebug("OpenAI response for {ListingId}: {Content}",
                    product.ListingId, content?.Substring(0, Math.Min(content?.Length ?? 0, 300)));

                return ParseSingleProductResponse(content, product.ListingId!);
            }
            catch (Exception ex) when (attempt < _settings.MaxRetries)
            {
                lastException = ex;
                _logger.LogWarning(ex,
                    "OpenAI request failed for {ListingId} (attempt {Attempt}/{MaxRetries}): {Message}",
                    product.ListingId, attempt, _settings.MaxRetries, ex.Message);

                // Exponential backoff
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay, ct);
            }
        }

        throw new EntityResolutionException(
            $"OpenAI request failed for {product.ListingId} after {_settings.MaxRetries} attempts",
            lastException!);
    }

    private EntityResolutionResult ParseSingleProductResponse(string content, string listingId)
    {
        try
        {
            var jsonDoc = JsonDocument.Parse(content);
            var root = jsonDoc.RootElement;

            var category = GetStringProperty(root, "category", "classification", "type");
            if (string.IsNullOrEmpty(category))
            {
                throw new EntityResolutionException($"Missing category for listing {listingId}");
            }

            // Validate category is one of our known categories
            if (!ProductCategory.All.Contains(category))
            {
                _logger.LogWarning("Unknown category '{Category}' for listing {ListingId}, mapping to 'other'",
                    category, listingId);
                category = ProductCategory.Other;
            }

            decimal? confidence = null;
            if (root.TryGetProperty("confidence", out var confProp) && confProp.ValueKind == JsonValueKind.Number)
            {
                confidence = confProp.GetDecimal();
            }

            var productName = GetStringProperty(root, "productName", "product_name", "name");
            var attributes = ParseAttributes(root);
            var bundledItems = ParseBundledItems(root);

            return new EntityResolutionResult(
                listingId,
                category,
                confidence,
                productName,
                attributes,
                bundledItems);
        }
        catch (JsonException ex)
        {
            throw new EntityResolutionException($"Failed to parse OpenAI response as JSON for {listingId}: {ex.Message}", ex);
        }
    }

    private static NormalizedAttributes ParseAttributes(JsonElement item)
    {
        if (!item.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
        {
            return new NormalizedAttributes(null, null, null, null, null, null);
        }

        return new NormalizedAttributes(
            GetStringOrNull(attrs, "brand"),
            GetStringOrNull(attrs, "model"),
            GetStringOrNull(attrs, "storageCapacity"),
            GetStringOrNull(attrs, "color"),
            GetStringOrNull(attrs, "edition"),
            GetStringOrNull(attrs, "variantType")
        );
    }

    private static string[]? ParseBundledItems(JsonElement item)
    {
        if (!item.TryGetProperty("bundledItems", out var bundled) ||
            bundled.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return bundled.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();
    }

    private static string? GetStringOrNull(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    /// <summary>
    /// Tries to get a string property from multiple possible property names.
    /// </summary>
    private static string? GetStringProperty(JsonElement obj, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (obj.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String)
                    return prop.GetString();
                if (prop.ValueKind == JsonValueKind.Number)
                    return prop.ToString();
            }
        }
        return null;
    }
}
