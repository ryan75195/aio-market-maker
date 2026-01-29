-- Migration: 026_AddListingsFailedToScrapeRuns (SQL Server)
-- Description: Adds ListingsFailed counter to track error pages and parse failures
-- Date: 2026-01-28

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRuns') AND name = 'ListingsFailed')
BEGIN
    ALTER TABLE ScrapeRuns ADD ListingsFailed INT NOT NULL DEFAULT 0;
END
GO
