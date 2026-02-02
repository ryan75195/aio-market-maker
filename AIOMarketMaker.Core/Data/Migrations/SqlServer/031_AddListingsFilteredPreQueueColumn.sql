-- Migration: 031_AddListingsFilteredPreQueueColumn
-- Description: Adds ListingsFilteredPreQueue column to track terminal status listings
--              filtered before queueing (separate from runtime ListingsSkipped)
-- Date: 2026-02-02

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRuns') AND name = 'ListingsFilteredPreQueue')
BEGIN
    ALTER TABLE ScrapeRuns ADD ListingsFilteredPreQueue INT NOT NULL DEFAULT 0;
END
