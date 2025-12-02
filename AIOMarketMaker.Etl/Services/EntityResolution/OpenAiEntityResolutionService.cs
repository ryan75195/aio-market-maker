using System.Collections.Concurrent;
using System.Text.Json;
using AIOMarketMaker.Etl.Configuration;
using AIOMarketMaker.Etl.Services.VectorSearch;
using AIOMarketMaker.Models.Ebay;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace AIOMarketMaker.Etl.Services.EntityResolution;

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

    public async Task<IReadOnlyList<EntityResolutionResult>> ResolveAsync(
        IReadOnlyList<EbayProduct> products,
        CancellationToken ct = default)
    {
        if (products.Count == 0)
            return [];

        var batches = products.Chunk(_settings.BatchSize).ToList();
        var batchResults = new ConcurrentDictionary<int, List<EntityResolutionResult>>();

        _logger.LogInformation(
            "Processing {Count} products in {Batches} batches (max {Concurrency} parallel)",
            products.Count, batches.Count, _settings.MaxConcurrency);

        // Process batches in parallel with concurrency limit
        var tasks = batches.Select((batch, index) =>
            ProcessBatchWithSemaphoreAsync(batch.ToList(), index, batchResults, ct)).ToList();

        await Task.WhenAll(tasks);

        // Flatten results in order
        var results = new List<EntityResolutionResult>();
        for (int i = 0; i < batches.Count; i++)
        {
            if (batchResults.TryGetValue(i, out var batch))
            {
                results.AddRange(batch);
            }
        }

        // Validate we got results for all products
        var resultIds = results.Select(r => r.ListingId).ToHashSet();
        var missingIds = products
            .Where(p => p.ListingId != null && !resultIds.Contains(p.ListingId))
            .Select(p => p.ListingId)
            .ToList();

        if (missingIds.Count > 0)
        {
            throw new EntityResolutionException(
                $"Entity resolution failed: missing results for {missingIds.Count} listings: {string.Join(", ", missingIds.Take(5))}");
        }

        return results;
    }

    private async Task ProcessBatchWithSemaphoreAsync(
        IReadOnlyList<EbayProduct> batch,
        int batchIndex,
        ConcurrentDictionary<int, List<EntityResolutionResult>> results,
        CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            _logger.LogInformation("Processing batch {Index} of {Count} products", batchIndex + 1, batch.Count);
            var batchResults = await ProcessBatchAsync(batch, ct);
            results[batchIndex] = batchResults;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<List<EntityResolutionResult>> ProcessBatchAsync(
        IReadOnlyList<EbayProduct> batch,
        CancellationToken ct)
    {
        // Find similar product names from Pinecone to provide context
        var listings = batch
            .Where(p => p.ListingId != null && !string.IsNullOrEmpty(p.Title))
            .Select(p => (p.ListingId!, p.Title!))
            .ToList();

        var similarNames = await _productNameIndexer.FindSimilarProductNamesAsync(listings, ct);

        if (similarNames.Count > 0)
        {
            _logger.LogInformation("Found similar product names for {Count}/{Total} listings",
                similarNames.Count, batch.Count);
        }

        var userPrompt = _promptBuilder.BuildUserPrompt(batch, similarNames);

        Exception? lastException = null;

        for (int attempt = 1; attempt <= _settings.MaxRetries; attempt++)
        {
            try
            {
                _logger.LogDebug("OpenAI request attempt {Attempt}/{MaxRetries}", attempt, _settings.MaxRetries);

                var chatClient = _client.GetChatClient(_settings.Model);

                var messages = new List<ChatMessage>
                {
                    new SystemChatMessage(_promptBuilder.SystemPrompt),
                    new UserChatMessage(userPrompt)
                };

                var options = new ChatCompletionOptions
                {
                    ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
                };

                var response = await chatClient.CompleteChatAsync(messages, options, ct);

                var content = response.Value.Content[0].Text;
                _logger.LogDebug("OpenAI response: {Content}", content?.Substring(0, Math.Min(content?.Length ?? 0, 500)));
                return ParseResponse(content, batch);
            }
            catch (Exception ex) when (attempt < _settings.MaxRetries)
            {
                lastException = ex;
                _logger.LogWarning(ex,
                    "OpenAI request failed (attempt {Attempt}/{MaxRetries}): {Message}",
                    attempt, _settings.MaxRetries, ex.Message);

                // Exponential backoff
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay, ct);
            }
        }

        throw new EntityResolutionException(
            $"OpenAI request failed after {_settings.MaxRetries} attempts",
            lastException!);
    }

    private List<EntityResolutionResult> ParseResponse(string content, IReadOnlyList<EbayProduct> batch)
    {
        try
        {
            _logger.LogInformation("Parsing OpenAI response ({Length} chars): {Preview}",
                content?.Length ?? 0, content?.Substring(0, Math.Min(content?.Length ?? 0, 300)));

            // The response might be wrapped in a root object or be a direct array
            var jsonDoc = JsonDocument.Parse(content);
            JsonElement resultsArray;

            if (jsonDoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                resultsArray = jsonDoc.RootElement;
            }
            else if (jsonDoc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Try common property names
                if (jsonDoc.RootElement.TryGetProperty("results", out var results) ||
                    jsonDoc.RootElement.TryGetProperty("products", out results) ||
                    jsonDoc.RootElement.TryGetProperty("classifications", out results) ||
                    jsonDoc.RootElement.TryGetProperty("items", out results) ||
                    jsonDoc.RootElement.TryGetProperty("data", out results))
                {
                    resultsArray = results;
                }
                else
                {
                    // Find the first array property in the object
                    resultsArray = default;
                    foreach (var prop in jsonDoc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            _logger.LogInformation("Found array in property '{Name}'", prop.Name);
                            resultsArray = prop.Value;
                            break;
                        }
                    }

                    if (resultsArray.ValueKind != JsonValueKind.Array)
                    {
                        _logger.LogError("Unexpected JSON structure. Root properties: {Props}",
                            string.Join(", ", jsonDoc.RootElement.EnumerateObject().Select(p => $"{p.Name}:{p.Value.ValueKind}")));
                        throw new EntityResolutionException(
                            $"Unexpected JSON structure in OpenAI response. Expected array or object with results/products/classifications property.");
                    }
                }
            }
            else
            {
                throw new EntityResolutionException(
                    $"Unexpected JSON root type: {jsonDoc.RootElement.ValueKind}");
            }

            var parsed = new List<EntityResolutionResult>();

            foreach (var item in resultsArray.EnumerateArray())
            {
                // Handle different property name variations (listingId, listing_id, id)
                var listingId = GetStringProperty(item, "listingId", "listing_id", "id");
                if (string.IsNullOrEmpty(listingId))
                {
                    _logger.LogWarning("Response item missing listingId. Item properties: {Props}",
                        string.Join(", ", item.EnumerateObject().Select(p => p.Name)));
                    throw new EntityResolutionException("Missing listingId in response item");
                }

                var category = GetStringProperty(item, "category", "classification", "type");
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
                if (item.TryGetProperty("confidence", out var confProp) && confProp.ValueKind == JsonValueKind.Number)
                {
                    confidence = confProp.GetDecimal();
                }

                var productName = GetStringProperty(item, "productName", "product_name", "name");
                var attributes = ParseAttributes(item);
                var bundledItems = ParseBundledItems(item);

                parsed.Add(new EntityResolutionResult(
                    listingId,
                    category,
                    confidence,
                    productName,
                    attributes,
                    bundledItems));
            }

            if (parsed.Count != batch.Count)
            {
                _logger.LogWarning(
                    "Response count mismatch: expected {Expected}, got {Actual}",
                    batch.Count, parsed.Count);
            }

            return parsed;
        }
        catch (JsonException ex)
        {
            throw new EntityResolutionException($"Failed to parse OpenAI response as JSON: {ex.Message}", ex);
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
