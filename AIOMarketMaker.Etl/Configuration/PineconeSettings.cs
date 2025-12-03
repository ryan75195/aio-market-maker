namespace AIOMarketMaker.Etl.Configuration;

public class PineconeSettings
{
    public required string ApiKey { get; set; }
    /// <summary>
    /// The index name (e.g., "arbitrage-products")
    /// </summary>
    public required string IndexName { get; set; }
    public int TopK { get; set; } = 5;
    public float SimilarityThreshold { get; set; } = 0.8f;
    /// <summary>
    /// Max concurrent Pinecone search queries.
    /// </summary>
    public int MaxSearchConcurrency { get; set; } = 10;
}
