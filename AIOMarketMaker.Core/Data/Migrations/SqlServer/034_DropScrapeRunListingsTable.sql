-- Migration: 034_DropScrapeRunListingsTable
-- Description: Drop ScrapeRunListings table - no longer needed with inline ETL
-- Date: 2026-02-05

IF EXISTS (SELECT * FROM sys.tables WHERE name = 'ScrapeRunListings')
BEGIN
    DROP TABLE ScrapeRunListings;
END
