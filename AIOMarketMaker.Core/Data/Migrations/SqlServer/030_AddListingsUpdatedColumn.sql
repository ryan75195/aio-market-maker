-- Migration: 030_AddListingsUpdatedColumn
-- Description: Adds ListingsUpdated column to track re-scraped existing listings
-- Date: 2026-02-01

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'ScrapeRuns') AND name = 'ListingsUpdated')
BEGIN
    ALTER TABLE ScrapeRuns ADD ListingsUpdated INT NOT NULL DEFAULT 0;
END
