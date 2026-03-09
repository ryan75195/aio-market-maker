-- Migration: 050_CreateTaxonomyOpportunitiesTable
-- Description: Pre-computed taxonomy cell pricing opportunities for cross-job queries
-- Date: 2026-03-09

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'TaxonomyOpportunities')
BEGIN
    CREATE TABLE TaxonomyOpportunities (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ScrapeJobId INT NOT NULL,
        ListingId INT NOT NULL,
        CellKey NVARCHAR(500) NOT NULL,
        AskPrice DECIMAL(18,2) NOT NULL,
        MedianSoldPrice DECIMAL(18,2) NOT NULL,
        EstimatedProfit DECIMAL(18,2) NOT NULL,
        MarginPercent FLOAT NOT NULL,
        SoldComps INT NOT NULL,
        AvgDaysToSell INT NULL,
        ComputedUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_TaxonomyOpportunities_Jobs FOREIGN KEY (ScrapeJobId) REFERENCES ScrapeJobs(Id) ON DELETE CASCADE,
        CONSTRAINT FK_TaxonomyOpportunities_Listings FOREIGN KEY (ListingId) REFERENCES Listings(Id) ON DELETE CASCADE
    );
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_TaxonomyOpportunities_Profit')
BEGIN
    CREATE INDEX IX_TaxonomyOpportunities_Profit ON TaxonomyOpportunities (EstimatedProfit DESC);
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_TaxonomyOpportunities_Job')
BEGIN
    CREATE INDEX IX_TaxonomyOpportunities_Job ON TaxonomyOpportunities (ScrapeJobId);
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_TaxonomyOpportunities_Listing')
BEGIN
    CREATE UNIQUE INDEX IX_TaxonomyOpportunities_Listing ON TaxonomyOpportunities (ListingId);
END
