namespace AIOMarketMaker.Services.Dtos;

public record CreateJobRequest(
    string SearchTerm,
    string? BuyingFormat = null,
    string? Condition = null,
    string? SearchType = null,
    int FrequencyMinutes = 60,
    int? LookbackDays = null,
    int? ItemLimit = null,
    bool IsEnabled = true
);

public record JobDto(
    int Id,
    string SearchTerm,
    string BuyingFormat,
    string Condition,
    string SearchType,
    int FrequencyMinutes,
    int? LookbackDays,
    int? ItemLimit,
    bool IsEnabled,
    DateTime? LastRunUtc,
    DateTime CreatedUtc
);

public record DeleteJobResult(bool Success, int ListingsDeleted);
