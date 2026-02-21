-- Migration: 040_OptimizePredictionQueryPerformance
-- Description: Remove redundant ListingStatusHistory subquery from vw_ListingPredictions
--              and add covering indexes for relationship join performance.
--              The history price equals Listings.Price in 100% of sold records,
--              making the correlated subquery pure overhead (~10s per query).
-- Date: 2026-02-21

-- 1. Recreate the view without the SoldPrices CTE
IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_ListingPredictions')
BEGIN
    DROP VIEW vw_ListingPredictions;
END
GO

CREATE VIEW vw_ListingPredictions AS
WITH ComparableSoldNeighbors AS (
    SELECT
        r.ListingIdA AS ActiveListingId,
        r.ListingIdB AS SoldListingId
    FROM ListingRelationships r
    INNER JOIN Listings active ON active.Id = r.ListingIdA AND active.ListingStatus = 'Active'
    INNER JOIN Listings sold ON sold.Id = r.ListingIdB AND sold.ListingStatus = 'Sold'
    WHERE r.IsComparable = 1
      AND active.[Condition] = sold.[Condition]

    UNION ALL

    SELECT
        r.ListingIdB AS ActiveListingId,
        r.ListingIdA AS SoldListingId
    FROM ListingRelationships r
    INNER JOIN Listings active ON active.Id = r.ListingIdB AND active.ListingStatus = 'Active'
    INNER JOIN Listings sold ON sold.Id = r.ListingIdA AND sold.ListingStatus = 'Sold'
    WHERE r.IsComparable = 1
      AND active.[Condition] = sold.[Condition]
)
SELECT
    active.Id AS ListingId,
    COUNT(*) AS SimilarSoldCount,
    AVG(sold.Price) AS AverageSoldPrice,
    CAST(NULL AS int) AS EstimatedDaysToSell,
    AVG(sold.Price) - active.Price AS PotentialProfit,
    GETUTCDATE() AS ComputedUtc
FROM ComparableSoldNeighbors csn
INNER JOIN Listings sold ON sold.Id = csn.SoldListingId
INNER JOIN Listings active ON active.Id = csn.ActiveListingId
WHERE sold.Price > 0
GROUP BY active.Id, active.Price;
GO

-- 2. Filtered index on comparable relationships only (738K of 3.2M rows)
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ListingRelationships_Comparable')
BEGIN
    SET QUOTED_IDENTIFIER ON;
    CREATE NONCLUSTERED INDEX IX_ListingRelationships_Comparable
    ON ListingRelationships (ListingIdA, ListingIdB)
    WHERE IsComparable = 1;
END

-- 3. Covering index for status/condition lookups during relationship joins.
--    Includes all columns referenced in the CTE to avoid clustered index seeks
--    on wide Listings rows (Title, Description, Images, etc.).
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Listings_Status_Condition_Cover')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Listings_Status_Condition_Cover
    ON Listings (Id, ListingStatus, [Condition])
    INCLUDE (Price, ShippingCost, EndDateUtc, CreatedUtc, ScrapeJobId);
END
