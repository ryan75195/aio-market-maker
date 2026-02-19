using OpenAI.Chat;

namespace AIOMarketMaker.ML.Services;

public class OpenAiChatClient : IChatClient
{
    private readonly ChatClient _client;

    public OpenAiChatClient(string model, string apiKey)
    {
        _client = new ChatClient(model, apiKey);
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
}
