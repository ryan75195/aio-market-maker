namespace AIOMarketMaker.Etl.Services.VectorSearch;

public record SimilarProductName(
    string ProductName,
    string? Category,
    string? Brand,
    float Score
);

public record ProductNameVector(
    int ProductId,
    string ProductName,
    string? Category,
    string? Brand,
    float[] Embedding
);
