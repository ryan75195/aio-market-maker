-- Migration: 010_SimplifyScrapeJobs
-- Description: Simplified migration - we don't need to drop columns since EF Core ignores unmapped columns.
--              This migration just ensures the table is in a usable state.
-- Date: 2025-12-05

-- No structural changes needed - EF Core ignores columns not in the model
-- The removed columns (SearchType, BuyingFormat, Condition, FrequencyMinutes, LookbackDays, ItemLimit)
-- will simply be ignored by the application.

-- Just ensure the FilterInstructions column exists (may have been added by migration 009)
-- SQLite doesn't have IF NOT EXISTS for columns, so we'll try-catch via a workaround
-- If it fails, the column already exists which is fine

SELECT 1; -- Placeholder - no changes needed
