namespace AIOMarketMaker.Etl.Configuration;

public class OpenAiSettings
{
    public required string ApiKey { get; set; }
    public string Model { get; set; } = "gpt-5-mini";
    public int MaxRetries { get; set; } = 3;
    public int BatchSize { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 60;
}
