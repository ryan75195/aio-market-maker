-- Migration: 033_DropComparablesCreateRelationshipsAndPredictions
-- Description: Drops ListingPricingComparables, creates ListingRelationships and ListingPredictions
-- Date: 2026-02-04

-- Drop the superseded table
IF EXISTS (SELECT * FROM sys.tables WHERE name = 'ListingPricingComparables')
BEGIN
    DROP TABLE ListingPricingComparables;
END

-- Create ListingRelationships (LLM verdict cache)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ListingRelationships')
BEGIN
    CREATE TABLE ListingRelationships (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ListingIdA INT NOT NULL,
        ListingIdB INT NOT NULL,
        IsComparable BIT NOT NULL,
        Explanation NVARCHAR(500) NOT NULL,
        SimilarityScore FLOAT NOT NULL,
        CreatedUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_ListingRelationships_ListingA FOREIGN KEY (ListingIdA) REFERENCES Listings(Id),
        CONSTRAINT FK_ListingRelationships_ListingB FOREIGN KEY (ListingIdB) REFERENCES Listings(Id),
        CONSTRAINT UQ_ListingRelationships_Pair UNIQUE (ListingIdA, ListingIdB),
        CONSTRAINT CK_ListingRelationships_Order CHECK (ListingIdA < ListingIdB)
    );
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ListingRelationships_ListingIdA')
BEGIN
    CREATE INDEX IX_ListingRelationships_ListingIdA ON ListingRelationships (ListingIdA);
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ListingRelationships_ListingIdB')
BEGIN
    CREATE INDEX IX_ListingRelationships_ListingIdB ON ListingRelationships (ListingIdB);
END

-- Create ListingPredictions (pre-computed pricing aggregates)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ListingPredictions')
BEGIN
    CREATE TABLE ListingPredictions (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ListingId INT NOT NULL,
        AverageSoldPrice DECIMAL(18,2) NOT NULL,
        SimilarSoldCount INT NOT NULL,
        EstimatedDaysToSell INT NULL,
        PotentialProfit DECIMAL(18,2) NULL,
        ComputedUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_ListingPredictions_Listing FOREIGN KEY (ListingId) REFERENCES Listings(Id),
        CONSTRAINT UQ_ListingPredictions_ListingId UNIQUE (ListingId)
    );
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ListingPredictions_ListingId')
BEGIN
    CREATE INDEX IX_ListingPredictions_ListingId ON ListingPredictions (ListingId);
END
