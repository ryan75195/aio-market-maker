-- Migration: 020_AddProgressFieldsToScrapeRuns
-- Description: Adds progress tracking fields to ScrapeRuns table
-- Date: 2026-01-26

ALTER TABLE ScrapeRuns ADD COLUMN TotalListingsFound INTEGER NOT NULL DEFAULT 0;
ALTER TABLE ScrapeRuns ADD COLUMN ListingsProcessed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE ScrapeRuns ADD COLUMN CurrentPhase TEXT;
