-- Migration: 014_FixScrapeJobsForSqlServer
-- Description: Fixes ScrapeJobs table for SQL Server - makes unused columns nullable
-- Date: 2025-12-23
-- Note: This is a SQL Server-specific migration to fix schema issues

-- Make the old columns nullable so they don't block inserts
-- These columns are no longer used by the application
IF COL_LENGTH('ScrapeJobs', 'BuyingFormat') IS NOT NULL
BEGIN
    ALTER TABLE ScrapeJobs ALTER COLUMN BuyingFormat NVARCHAR(MAX) NULL;
END

IF COL_LENGTH('ScrapeJobs', 'Condition') IS NOT NULL
BEGIN
    ALTER TABLE ScrapeJobs ALTER COLUMN Condition NVARCHAR(MAX) NULL;
END

IF COL_LENGTH('ScrapeJobs', 'SearchType') IS NOT NULL
BEGIN
    ALTER TABLE ScrapeJobs ALTER COLUMN SearchType NVARCHAR(MAX) NULL;
END

-- Add FilterInstructions column if it doesn't exist
IF COL_LENGTH('ScrapeJobs', 'FilterInstructions') IS NULL
BEGIN
    ALTER TABLE ScrapeJobs ADD FilterInstructions NVARCHAR(MAX) NULL;
END
