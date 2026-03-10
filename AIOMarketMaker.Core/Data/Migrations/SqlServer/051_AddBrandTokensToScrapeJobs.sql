-- Migration: 051_AddBrandTokensToScrapeJobs
-- Description: Adds BrandTokens JSON column to ScrapeJobs for taxonomy decontamination
-- Date: 2026-03-10

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('ScrapeJobs') AND name = 'BrandTokens'
)
BEGIN
    ALTER TABLE ScrapeJobs ADD BrandTokens NVARCHAR(500) NULL;
END
