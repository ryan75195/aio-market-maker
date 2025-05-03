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
    NOT_SPECIFIED,
    ANY,
    NULL
}

public record SearchFilter
{
    public SoldRange? SoldFilter { get; set; }
    public BuyingFormat? BuyingFormat { get; init; }
    public Condition? Condition { get; init; }
}