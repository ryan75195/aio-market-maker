-- Migration: 032_CreateListingPricingComparablesTable
-- Description: Junction table linking listings to similar sold listings for pricing
-- Date: 2026-02-04

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ListingPricingComparables')
BEGIN
    CREATE TABLE ListingPricingComparables (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ListingId INT NOT NULL,
        ComparableListingId INT NOT NULL,
        SimilarityScore FLOAT NOT NULL,
        CreatedUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_ListingPricingComparables_Listing FOREIGN KEY (ListingId) REFERENCES Listings(Id),
        CONSTRAINT FK_ListingPricingComparables_ComparableListing FOREIGN KEY (ComparableListingId) REFERENCES Listings(Id)
    );
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ListingPricingComparables_ListingId')
BEGIN
    CREATE INDEX IX_ListingPricingComparables_ListingId ON ListingPricingComparables (ListingId);
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ListingPricingComparables_ComparableListingId')
BEGIN
    CREATE INDEX IX_ListingPricingComparables_ComparableListingId ON ListingPricingComparables (ComparableListingId);
END
