namespace AIOMarketMaker.Services.Dtos;

public record CreateJobRequest(
    string SearchTerm,
    string? FilterInstructions = null,
    bool IsEnabled = true
);

public record JobDto(
    int Id,
    string SearchTerm,
    string? FilterInstructions,
    bool IsEnabled,
    DateTime? LastRunUtc,
    DateTime CreatedUtc
);

public record DeleteJobResult(bool Success, int ListingsDeleted);
