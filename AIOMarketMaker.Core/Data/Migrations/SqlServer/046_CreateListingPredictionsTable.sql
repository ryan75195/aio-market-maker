-- Migration: 046_CreateListingPredictionsTable
-- Description: Persistent table for materialized prediction CTE results.
-- Populated by PredictionBatchStage after each scrape batch.
-- Date: 2026-02-27

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ListingPredictions')
BEGIN
    CREATE TABLE ListingPredictions (
        ListingId INT NOT NULL PRIMARY KEY,
        SimilarSoldCount INT NOT NULL DEFAULT 0,
        AverageSoldPrice DECIMAL(18,2) NOT NULL DEFAULT 0,
        MedianSoldPrice DECIMAL(18,2) NULL,
        PotentialProfit DECIMAL(18,2) NOT NULL DEFAULT 0,
        EstimatedDaysToSell INT NULL,
        Confidence FLOAT NOT NULL DEFAULT 0,
        OutliersRemoved INT NOT NULL DEFAULT 0,
        ComputedUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT FK_ListingPredictions_Listings
            FOREIGN KEY (ListingId) REFERENCES Listings(Id) ON DELETE CASCADE
    );
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ListingPredictions_PotentialProfit')
BEGIN
    CREATE NONCLUSTERED INDEX IX_ListingPredictions_PotentialProfit
    ON ListingPredictions (PotentialProfit DESC)
    INCLUDE (SimilarSoldCount, AverageSoldPrice, MedianSoldPrice, EstimatedDaysToSell, Confidence);
END
