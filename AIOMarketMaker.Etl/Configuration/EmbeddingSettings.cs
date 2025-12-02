namespace AIOMarketMaker.Etl.Configuration;

public class EmbeddingSettings
{
    public string Model { get; set; } = "text-embedding-3-small";
    public int MaxBatchSize { get; set; } = 100;
    public int Dimensions { get; set; } = 1536;
    public int MaxConcurrency { get; set; } = 5;
}
