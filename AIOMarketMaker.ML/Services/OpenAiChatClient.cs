using System.Collections.Concurrent;
using System.Text.Json;
using AIOMarketMaker.ML.Utils;
using OpenAI.Chat;

namespace AIOMarketMaker.ML.Services;

public interface IChatClient
{
    Task<string> CompleteChat(string systemPrompt, string userPrompt, CancellationToken ct = default);

    /// <summary>
    /// Sends a chat completion request with structured output, returning a typed result.
    /// The default implementation calls CompleteChat and deserializes the JSON response.
    /// Implementations should override to use native structured output support (e.g., OpenAI json_schema).
    /// </summary>
    async Task<T?> CompleteChat<T>(string systemPrompt, string userPrompt, CancellationToken ct = default)
        where T : class
    {
        var response = await CompleteChat(systemPrompt, userPrompt, ct);
        try
        {
            return JsonSerializer.Deserialize<T>(response);
        }
        catch
        {
            return null;
        }
    }
}

public class OpenAiChatClient : IChatClient
{
    private readonly ChatClient _client;
    private static readonly ConcurrentDictionary<Type, ChatResponseFormat> SchemaCache = new();

    private readonly float? _temperature;

    public OpenAiChatClient(string model, string apiKey, float? temperature = 0f)
    {
        _client = new ChatClient(model, apiKey);
        _temperature = temperature;
    }

    public async Task<string> CompleteChat(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var messages = new ChatMessage[]
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        var completion = await _client.CompleteChatAsync(messages, cancellationToken: ct);
        return completion.Value.Content[0].Text;
    }

    public async Task<T?> CompleteChat<T>(string systemPrompt, string userPrompt, CancellationToken ct = default)
        where T : class
    {
        var responseFormat = SchemaCache.GetOrAdd(typeof(T), type =>
        {
            var schemaJson = JsonSchemaGenerator.Generate(type);
            var schemaName = type.Name;

            return ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: schemaName,
                jsonSchema: BinaryData.FromString(schemaJson),
                jsonSchemaIsStrict: true);
        });

        var options = new ChatCompletionOptions
        {
            ResponseFormat = responseFormat
        };

        if (_temperature.HasValue)
        {
            options.Temperature = _temperature.Value;
        }

        var messages = new ChatMessage[]
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        var completion = await _client.CompleteChatAsync(messages, options, ct);
        var json = completion.Value.Content[0].Text;

        return JsonSerializer.Deserialize<T>(json);
    }
}
