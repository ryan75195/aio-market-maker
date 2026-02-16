namespace AIOMarketMaker.Core.Services;

public record VectorIndexConfig(
    string IndexPath,
    string IdMapPath,
    int TopK = 30,
    float SimilarityThreshold = 0.80f,
    int Dimensions = 3072,
    int Connectivity = 16,
    int ExpansionAdd = 128,
    int ExpansionSearch = 64,
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
