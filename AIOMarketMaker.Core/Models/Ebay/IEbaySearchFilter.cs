using System.Text.Json.Serialization;

public record SearchDateRange(DateTime startDate, DateTime endDate);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuyingFormat
{
    BUY_NOW,
    AUCTION,
    ALL,
    NULL
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Condition
{
    NEW,
    USED,
    FOR_PARTS_NOT_WORKING,
    VERY_GOOD_REFURBISHED,
    EXCELLENT_REFURBISHED,
    OPENED_NEVER_USED,
    NULL,
    GOOD_REFURBISHED,
}

public record SearchFilter(
    SearchDateRange SearchDateRange,
    BuyingFormat BuyingFormat,
    Condition Condition
);