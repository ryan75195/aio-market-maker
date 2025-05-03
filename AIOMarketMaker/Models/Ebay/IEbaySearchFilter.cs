public record SoldRange(DateTime startDate, DateTime endDate);

public enum BuyingFormat
{
    BUY_NOW,
    AUCTION,
    ALL,
    NULL
}

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

public record SearchFilter
{
    public SoldRange SoldFilter { get; set; }
    public BuyingFormat BuyingFormat { get; init; }
    public Condition Condition { get; init; }
}