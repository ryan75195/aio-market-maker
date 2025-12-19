namespace AIOMarketMaker.Core.Services;

public record PineconeConfig(
    string ApiKey,
    string IndexName,
    int TopK = 10,
    float SimilarityThreshold = 0.0f,
    int UpsertBatchSize = 100
);

public record SemanticSearchHit(
    string ListingId,
    float Score
);

public record SemanticSearchResult(
    IReadOnlyList<SemanticSearchHit> Hits
);

public record IndexResult(
    int UpsertedCount,
    int SkippedCount,
    IReadOnlyList<string> Errors
);
