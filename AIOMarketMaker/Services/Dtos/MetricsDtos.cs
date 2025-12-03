namespace AIOMarketMaker.Services.Dtos;

public record DashboardMetrics(
    SummaryStats Summary,
    IReadOnlyList<CategoryStats> CategoryBreakdown,
    IReadOnlyList<BrandStats> BrandBreakdown,
    IReadOnlyList<ProductNameStats> ProductNameBreakdown,
    IReadOnlyList<ArbitrageStats> ArbitrageByJob,
    IReadOnlyList<PriceDistributionBucket> PriceDistribution,
    IReadOnlyList<DailySales> SalesByDay,
    IReadOnlyList<Deal> BestDeals
);

public record SummaryStats(
    int TotalProducts,
    int SoldProducts,
    int ActiveProducts,
    double TotalMarketValue,
    double AvgSoldPrice,
    double MedianSoldPrice,
    double SellThroughRate7d,
    int ActiveJobs
);

public record CategoryStats(
    string? Category,
    int Count,
    int SoldCount,
    int ActiveCount,
    double AvgPrice,
    double AvgSoldPrice
);

public record BrandStats(
    string Brand,
    int Count,
    int SoldCount,
    double AvgPrice,
    IReadOnlyList<ModelCount> Models
);

public record ModelCount(string Model, int Count);

public record ProductNameStats(
    string ProductName,
    int Count,
    int SoldCount,
    int ActiveCount,
    double AvgPrice,
    double AvgSoldPrice
);

public record ArbitrageStats(
    int JobId,
    string SearchTerm,
    int TotalCount,
    int BaseProductCount,
    int SoldCount,
    int ActiveCount,
    double AvgSoldPrice,
    double MedianSoldPrice,
    double MinSoldPrice,
    double MaxSoldPrice,
    double AvgActivePrice,
    double MinActivePrice,
    double PriceSpread,
    double SpreadPercent,
    int DealsCount
);

public record PriceDistributionBucket(
    string Range,
    int Sold,
    int Active
);

public record DailySales(
    string Date,
    int Count,
    double AvgPrice,
    double Volume
);

public record Deal(
    string EbayListingId,
    string ProductName,
    string? Edition,
    string? Title,
    decimal Price,
    string? Brand,
    string? Model,
    string Category,
    double MedianSoldPrice,
    double PotentialProfit,
    double ProfitPercent,
    string? Url,
    int ComparableCount
);
