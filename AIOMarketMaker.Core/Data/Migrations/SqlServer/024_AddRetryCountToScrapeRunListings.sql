-- Migration: 024_AddRetryCountToScrapeRunListings (SQL Server)
-- Description: Adds RetryCount column to track ETL retry attempts for failed scrapes
-- Date: 2026-01-28

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunListings') AND name = 'RetryCount')
BEGIN
    ALTER TABLE ScrapeRunListings ADD RetryCount INT NOT NULL DEFAULT 0;
END
GO
