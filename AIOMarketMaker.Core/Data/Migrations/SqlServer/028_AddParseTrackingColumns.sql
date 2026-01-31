-- Migration: 028_AddParseTrackingColumns
-- Description: Adds columns to ScrapeRunListings for tracking parse attempts and failure details
-- Date: 2026-01-30

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunListings') AND name = 'ParseAttempts')
BEGIN
    ALTER TABLE ScrapeRunListings ADD ParseAttempts INT NOT NULL DEFAULT 0;
END

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunListings') AND name = 'FailureReason')
BEGIN
    ALTER TABLE ScrapeRunListings ADD FailureReason NVARCHAR(100) NULL;
END

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunListings') AND name = 'FailureDetails')
BEGIN
    ALTER TABLE ScrapeRunListings ADD FailureDetails NVARCHAR(500) NULL;
END
