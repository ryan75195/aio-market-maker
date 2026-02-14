-- Migration: 036_ReplaceListingPredictionsWithView
-- Description: Replace the static ListingPredictions table with a live computed view
-- Date: 2026-02-14

-- Drop the table first (view needs the same logical role)
IF EXISTS (SELECT * FROM sys.tables WHERE name = 'ListingPredictions')
BEGIN
    DROP TABLE ListingPredictions;
END

-- Drop existing view if re-running
IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_ListingPredictions')
BEGIN
    DROP VIEW vw_ListingPredictions;
END
GO

CREATE VIEW vw_ListingPredictions AS
WITH ComparableSoldNeighbors AS (
    -- Side A: active listing in ListingIdA, sold neighbor in ListingIdB
    SELECT
        r.ListingIdA AS ActiveListingId,
        r.ListingIdB AS SoldListingId
    FROM ListingRelationships r
    INNER JOIN Listings active ON active.Id = r.ListingIdA AND active.ListingStatus = 'Active'
    INNER JOIN Listings sold ON sold.Id = r.ListingIdB AND sold.ListingStatus = 'Sold'
    WHERE r.IsComparable = 1

    UNION ALL

    -- Side B: active listing in ListingIdB, sold neighbor in ListingIdA
    SELECT
        r.ListingIdB AS ActiveListingId,
        r.ListingIdA AS SoldListingId
    FROM ListingRelationships r
    INNER JOIN Listings active ON active.Id = r.ListingIdB AND active.ListingStatus = 'Active'
    INNER JOIN Listings sold ON sold.Id = r.ListingIdA AND sold.ListingStatus = 'Sold'
    WHERE r.IsComparable = 1
),
SoldPrices AS (
    SELECT
        csn.ActiveListingId,
        csn.SoldListingId,
        COALESCE(
            (SELECT TOP 1 h.Price
             FROM ListingStatusHistory h
             WHERE h.ListingId = csn.SoldListingId AND h.ListingStatus = 'Sold'
             ORDER BY h.RecordedUtc DESC),
            sold.Price
        ) AS SoldPrice
    FROM ComparableSoldNeighbors csn
    INNER JOIN Listings sold ON sold.Id = csn.SoldListingId
)
SELECT
    active.Id AS ListingId,
    COUNT(*) AS SimilarSoldCount,
    AVG(sp.SoldPrice) AS AverageSoldPrice,
    CAST(NULL AS int) AS EstimatedDaysToSell,
    AVG(sp.SoldPrice) - active.Price AS PotentialProfit,
    GETUTCDATE() AS ComputedUtc
FROM SoldPrices sp
INNER JOIN Listings active ON active.Id = sp.ActiveListingId
WHERE sp.SoldPrice > 0
GROUP BY active.Id, active.Price;
GO
