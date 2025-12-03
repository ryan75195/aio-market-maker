using System.Data.Common;
using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Services.Dtos;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Services;

public class MetricsService : IMetricsService
{
    private readonly EtlDbContext _dbContext;

    public MetricsService(EtlDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<DashboardMetrics> GetDashboardMetricsAsync(CancellationToken ct = default)
    {
        var conn = _dbContext.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        try
        {
            var summary = await GetSummaryStatsAsync(conn, ct);
            var categoryBreakdown = await GetCategoryBreakdownAsync(conn, ct);
            var brandBreakdown = await GetBrandBreakdownAsync(conn, ct);
            var productNameBreakdown = await GetProductNameBreakdownAsync(conn, ct);
            var arbitrageByJob = await GetArbitrageByJobAsync(conn, ct);
            var priceDistribution = await GetPriceDistributionAsync(conn, ct);
            var salesByDay = await GetSalesByDayAsync(conn, ct);
            var bestDeals = await GetBestDealsAsync(conn, ct);

            return new DashboardMetrics(
                summary,
                categoryBreakdown,
                brandBreakdown,
                productNameBreakdown,
                arbitrageByJob,
                priceDistribution,
                salesByDay,
                bestDeals
            );
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private async Task<SummaryStats> GetSummaryStatsAsync(DbConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                COUNT(*) as totalProducts,
                SUM(CASE WHEN ListingStatus = 'Sold' AND Price IS NOT NULL THEN 1 ELSE 0 END) as soldProducts,
                SUM(CASE WHEN ListingStatus != 'Sold' AND Price IS NOT NULL THEN 1 ELSE 0 END) as activeProducts,
                COALESCE(SUM(CASE WHEN ListingStatus = 'Sold' THEN Price ELSE 0 END), 0) as totalMarketValue,
                COALESCE(AVG(CASE WHEN ListingStatus = 'Sold' AND Price IS NOT NULL THEN Price END), 0) as avgSoldPrice,
                SUM(CASE WHEN ListingStatus = 'Sold' AND EndDateUtc >= datetime('now', '-7 days') THEN 1 ELSE 0 END) as recentSold
            FROM Products";

        using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        var totalProducts = reader.GetInt32(0);
        var soldProducts = reader.GetInt32(1);
        var activeProducts = reader.GetInt32(2);
        var totalMarketValue = reader.GetDouble(3);
        var avgSoldPrice = reader.GetDouble(4);
        var recentSold = reader.GetInt32(5);
        await reader.CloseAsync();

        var medianSoldPrice = await GetMedianAsync(conn, "SELECT Price FROM Products WHERE ListingStatus = 'Sold' AND Price IS NOT NULL ORDER BY Price", ct);

        using var jobsCmd = conn.CreateCommand();
        jobsCmd.CommandText = "SELECT COUNT(*) FROM ScrapeJobs WHERE IsEnabled = 1";
        var activeJobs = Convert.ToInt32(await jobsCmd.ExecuteScalarAsync(ct));

        var sellThroughRate = activeProducts + recentSold > 0
            ? Math.Round((double)recentSold / (activeProducts + recentSold) * 100, 1)
            : 0;

        return new SummaryStats(
            totalProducts,
            soldProducts,
            activeProducts,
            Math.Round(totalMarketValue, 2),
            Math.Round(avgSoldPrice, 2),
            Math.Round(medianSoldPrice, 2),
            sellThroughRate,
            activeJobs
        );
    }

    private async Task<List<CategoryStats>> GetCategoryBreakdownAsync(DbConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                Category as category,
                COUNT(*) as count,
                SUM(CASE WHEN ListingStatus = 'Sold' THEN 1 ELSE 0 END) as soldCount,
                SUM(CASE WHEN ListingStatus != 'Sold' THEN 1 ELSE 0 END) as activeCount,
                COALESCE(AVG(Price), 0) as avgPrice,
                COALESCE(AVG(CASE WHEN ListingStatus = 'Sold' THEN Price END), 0) as avgSoldPrice
            FROM Products
            GROUP BY Category
            ORDER BY count DESC";

        var results = new List<CategoryStats>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new CategoryStats(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                Math.Round(reader.GetDouble(4), 2),
                Math.Round(reader.GetDouble(5), 2)
            ));
        }
        return results;
    }

    private async Task<List<BrandStats>> GetBrandBreakdownAsync(DbConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                Brand as brand,
                COUNT(*) as count,
                SUM(CASE WHEN ListingStatus = 'Sold' THEN 1 ELSE 0 END) as soldCount,
                COALESCE(AVG(Price), 0) as avgPrice
            FROM Products
            WHERE Brand IS NOT NULL AND Brand != ''
            GROUP BY Brand
            ORDER BY count DESC
            LIMIT 10";

        var results = new List<BrandStats>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var brand = reader.GetString(0);
            results.Add(new BrandStats(
                brand,
                reader.GetInt32(1),
                reader.GetInt32(2),
                Math.Round(reader.GetDouble(3), 2),
                await GetTopModelsForBrandAsync(conn, brand, ct)
            ));
        }
        return results;
    }

    private async Task<List<ModelCount>> GetTopModelsForBrandAsync(DbConnection conn, string brand, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Model, COUNT(*) as count
            FROM Products
            WHERE Brand = @brand AND Model IS NOT NULL AND Model != ''
            GROUP BY Model
            ORDER BY count DESC
            LIMIT 5";
        var param = cmd.CreateParameter();
        param.ParameterName = "@brand";
        param.Value = brand;
        cmd.Parameters.Add(param);

        var models = new List<ModelCount>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            models.Add(new ModelCount(reader.GetString(0), reader.GetInt32(1)));
        }
        return models;
    }

    private async Task<List<ProductNameStats>> GetProductNameBreakdownAsync(DbConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                ProductName as productName,
                COUNT(*) as count,
                SUM(CASE WHEN ListingStatus = 'Sold' THEN 1 ELSE 0 END) as soldCount,
                SUM(CASE WHEN ListingStatus != 'Sold' THEN 1 ELSE 0 END) as activeCount,
                COALESCE(AVG(Price), 0) as avgPrice,
                COALESCE(AVG(CASE WHEN ListingStatus = 'Sold' THEN Price END), 0) as avgSoldPrice
            FROM Products
            WHERE ProductName IS NOT NULL AND ProductName != ''
            GROUP BY ProductName
            ORDER BY count DESC
            LIMIT 20";

        var results = new List<ProductNameStats>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ProductNameStats(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                Math.Round(reader.GetDouble(4), 2),
                Math.Round(reader.GetDouble(5), 2)
            ));
        }
        return results;
    }

    private async Task<List<ArbitrageStats>> GetArbitrageByJobAsync(DbConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                j.Id as jobId,
                j.SearchTerm as searchTerm,
                COUNT(DISTINCT p.Id) as totalCount,
                SUM(CASE WHEN p.Category = 'base_product' THEN 1 ELSE 0 END) as baseProductCount,
                SUM(CASE WHEN p.ListingStatus = 'Sold' AND p.Category = 'base_product' THEN 1 ELSE 0 END) as soldCount,
                SUM(CASE WHEN p.ListingStatus != 'Sold' AND p.Category = 'base_product' THEN 1 ELSE 0 END) as activeCount,
                COALESCE(AVG(CASE WHEN p.ListingStatus = 'Sold' AND p.Category = 'base_product' THEN p.Price END), 0) as avgSoldPrice,
                COALESCE(MIN(CASE WHEN p.ListingStatus = 'Sold' AND p.Category = 'base_product' THEN p.Price END), 0) as minSoldPrice,
                COALESCE(MAX(CASE WHEN p.ListingStatus = 'Sold' AND p.Category = 'base_product' THEN p.Price END), 0) as maxSoldPrice,
                COALESCE(AVG(CASE WHEN p.ListingStatus != 'Sold' AND p.Category = 'base_product' THEN p.Price END), 0) as avgActivePrice,
                COALESCE(MIN(CASE WHEN p.ListingStatus != 'Sold' AND p.Category = 'base_product' THEN p.Price END), 0) as minActivePrice
            FROM ScrapeJobs j
            LEFT JOIN Listings l ON l.ScrapeJobId = j.Id
            LEFT JOIN Products p ON p.ListingId = l.Id
            GROUP BY j.Id, j.SearchTerm
            HAVING totalCount > 0";

        var results = new List<ArbitrageStats>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var jobId = reader.GetInt32(0);
            var avgSoldPrice = reader.GetDouble(6);
            var minActivePrice = reader.GetDouble(10);
            var priceSpread = avgSoldPrice - minActivePrice;
            var spreadPercent = minActivePrice > 0 ? (priceSpread / minActivePrice) * 100 : 0;

            var medianSoldPrice = await GetMedianAsync(conn, $@"
                SELECT p.Price FROM Products p
                JOIN Listings l ON l.Id = p.ListingId
                WHERE l.ScrapeJobId = {jobId}
                AND p.ListingStatus = 'Sold'
                AND p.Category = 'base_product'
                AND p.Price IS NOT NULL
                ORDER BY p.Price", ct);

            using var dealsCmd = conn.CreateCommand();
            dealsCmd.CommandText = $@"
                SELECT COUNT(*) FROM Products p
                JOIN Listings l ON l.Id = p.ListingId
                WHERE l.ScrapeJobId = {jobId}
                AND p.ListingStatus != 'Sold'
                AND p.Category = 'base_product'
                AND p.Price < {medianSoldPrice * 0.8}";
            var dealsCount = Convert.ToInt32(await dealsCmd.ExecuteScalarAsync(ct));

            results.Add(new ArbitrageStats(
                jobId,
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                Math.Round(avgSoldPrice, 2),
                Math.Round(medianSoldPrice, 2),
                Math.Round(reader.GetDouble(7), 2),
                Math.Round(reader.GetDouble(8), 2),
                Math.Round(reader.GetDouble(9), 2),
                Math.Round(minActivePrice, 2),
                Math.Round(priceSpread, 2),
                Math.Round(spreadPercent, 1),
                dealsCount
            ));
        }
        return results;
    }

    private async Task<List<PriceDistributionBucket>> GetPriceDistributionAsync(DbConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                CASE
                    WHEN Price < 25 THEN '0-25'
                    WHEN Price < 50 THEN '25-50'
                    WHEN Price < 100 THEN '50-100'
                    WHEN Price < 200 THEN '100-200'
                    WHEN Price < 500 THEN '200-500'
                    WHEN Price < 1000 THEN '500-1000'
                    ELSE '1000+'
                END as range,
                SUM(CASE WHEN ListingStatus = 'Sold' THEN 1 ELSE 0 END) as sold,
                SUM(CASE WHEN ListingStatus != 'Sold' THEN 1 ELSE 0 END) as active
            FROM Products
            WHERE Price IS NOT NULL
            GROUP BY range
            ORDER BY MIN(Price)";

        var results = new List<PriceDistributionBucket>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new PriceDistributionBucket(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2)
            ));
        }
        return results;
    }

    private async Task<List<DailySales>> GetSalesByDayAsync(DbConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                DATE(EndDateUtc) as date,
                COUNT(*) as count,
                ROUND(AVG(Price), 2) as avgPrice,
                ROUND(SUM(Price), 2) as volume
            FROM Products
            WHERE ListingStatus = 'Sold'
            AND EndDateUtc >= datetime('now', '-30 days')
            AND Price IS NOT NULL
            GROUP BY DATE(EndDateUtc)
            ORDER BY date";

        var results = new List<DailySales>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new DailySales(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetDouble(2),
                reader.GetDouble(3)
            ));
        }
        return results;
    }

    private async Task<List<Deal>> GetBestDealsAsync(DbConnection conn, CancellationToken ct)
    {
        using var medianCmd = conn.CreateCommand();
        medianCmd.CommandText = @"
            WITH RankedPrices AS (
                SELECT
                    ProductName,
                    Category,
                    Price,
                    ROW_NUMBER() OVER (PARTITION BY ProductName, Category ORDER BY Price) as rn,
                    COUNT(*) OVER (PARTITION BY ProductName, Category) as cnt
                FROM Products
                WHERE ListingStatus = 'Sold' AND Price IS NOT NULL
                AND ProductName IS NOT NULL AND Category IS NOT NULL
            )
            SELECT ProductName, Category, AVG(Price) as MedianPrice, MAX(cnt) as ComparableCount
            FROM RankedPrices
            WHERE rn IN ((cnt + 1) / 2, (cnt + 2) / 2)
            GROUP BY ProductName, Category";

        var medians = new Dictionary<(string, string), (double median, int count)>();
        using (var reader = await medianCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                medians[key] = (reader.GetDouble(2), reader.GetInt32(3));
            }
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT EbayListingId, ProductName, Edition, Title, Price, Brand, Model, Category, Url
            FROM Products
            WHERE ListingStatus != 'Sold'
            AND Price IS NOT NULL
            AND ProductName IS NOT NULL AND ProductName != ''
            AND Category IS NOT NULL AND Category != ''";

        var deals = new List<(string ebayListingId, string productName, string? edition, string? title,
            decimal price, string? brand, string? model, string category, string? url,
            double medianSoldPrice, double potentialProfit, double profitPercent, int comparableCount)>();

        using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var productName = reader.GetString(1);
                var category = reader.GetString(7);
                var price = reader.GetDecimal(4);

                if (medians.TryGetValue((productName, category), out var medianData))
                {
                    var potentialProfit = medianData.median - (double)price;
                    var profitPercent = price > 0 ? (potentialProfit / (double)price) * 100 : 0;

                    if (medianData.count > 0 && potentialProfit > 0 && profitPercent > 20)
                    {
                        deals.Add((
                            reader.GetString(0),
                            productName,
                            reader.IsDBNull(2) ? null : reader.GetString(2),
                            reader.IsDBNull(3) ? null : reader.GetString(3),
                            price,
                            reader.IsDBNull(5) ? null : reader.GetString(5),
                            reader.IsDBNull(6) ? null : reader.GetString(6),
                            category,
                            reader.IsDBNull(8) ? null : reader.GetString(8),
                            medianData.median,
                            potentialProfit,
                            profitPercent,
                            medianData.count
                        ));
                    }
                }
            }
        }

        return deals
            .OrderByDescending(d => d.profitPercent)
            .Take(10)
            .Select(d => new Deal(
                d.ebayListingId,
                d.productName,
                d.edition,
                d.title,
                d.price,
                d.brand,
                d.model,
                d.category,
                Math.Round(d.medianSoldPrice, 2),
                Math.Round(d.potentialProfit, 2),
                Math.Round(d.profitPercent, 1),
                d.url,
                d.comparableCount
            ))
            .ToList();
    }

    private async Task<double> GetMedianAsync(DbConnection conn, string query, CancellationToken ct)
    {
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM ({query})";
        var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        if (count == 0) return 0;

        var offset = (count - 1) / 2;
        var limit = count % 2 == 0 ? 2 : 1;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{query} LIMIT {limit} OFFSET {offset}";

        var values = new List<double>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.GetDouble(0));
        }

        return values.Any() ? values.Average() : 0;
    }
}
