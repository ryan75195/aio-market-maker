namespace AIOMarketMaker.ML.Services;

public interface IChatClient
{
    Task<string> CompleteChat(string systemPrompt, string userPrompt, CancellationToken ct = default);
}
