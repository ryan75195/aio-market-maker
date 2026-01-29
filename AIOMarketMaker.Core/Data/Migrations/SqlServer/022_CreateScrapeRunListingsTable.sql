-- Migration: 022_CreateScrapeRunListingsTable (SQL Server)
-- Description: Creates junction table to link ScrapeRuns to Listings for progress tracking
-- Date: 2026-01-27

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ScrapeRunListings')
BEGIN
    CREATE TABLE ScrapeRunListings (
        ScrapeRunId INT NOT NULL,
        ScrapeJobId INT NOT NULL,
        ListingId NVARCHAR(20) NOT NULL,
        Status NVARCHAR(50) DEFAULT 'Pending',
        CreatedUtc DATETIME2 DEFAULT GETUTCDATE(),
        CompletedUtc DATETIME2 NULL,
        PRIMARY KEY (ScrapeRunId, ListingId),
        FOREIGN KEY (ScrapeRunId) REFERENCES ScrapeRuns(Id) ON DELETE CASCADE,
        FOREIGN KEY (ScrapeJobId) REFERENCES ScrapeJobs(Id) ON DELETE CASCADE
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScrapeRunListings_ListingId')
BEGIN
    CREATE INDEX IX_ScrapeRunListings_ListingId ON ScrapeRunListings (ListingId);
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScrapeRunListings_Status')
BEGIN
    CREATE INDEX IX_ScrapeRunListings_Status ON ScrapeRunListings (ScrapeRunId, Status);
END
GO
