-- Migration: 013_CleanupUnusedScrapeJobColumns
-- Description: Removes unused columns from ScrapeJobs table
-- Date: 2025-12-16

-- SQLite doesn't support DROP COLUMN directly, so we recreate the table

-- First, drop any tables that might have foreign keys to ScrapeJobs
-- (in case previous migrations didn't clean them up)
DROP TABLE IF EXISTS ListingProductAssignments;
DROP TABLE IF EXISTS Products;
DROP TABLE IF EXISTS MetadataGroups;

-- 1. Create new table with only used columns
CREATE TABLE ScrapeJobs_new (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SearchTerm TEXT NOT NULL,
    FilterInstructions TEXT NULL,
    IsEnabled INTEGER NOT NULL DEFAULT 1,
    LastRunUtc TEXT NULL,
    CreatedUtc TEXT NOT NULL DEFAULT (datetime('now'))
);

-- 2. Copy data from old table
INSERT INTO ScrapeJobs_new (Id, SearchTerm, FilterInstructions, IsEnabled, LastRunUtc, CreatedUtc)
SELECT Id, SearchTerm, FilterInstructions, IsEnabled, LastRunUtc, CreatedUtc
FROM ScrapeJobs;

-- 3. Drop old table
DROP TABLE ScrapeJobs;

-- 4. Rename new table
ALTER TABLE ScrapeJobs_new RENAME TO ScrapeJobs;

-- 5. Recreate index (only the useful one)
CREATE INDEX IF NOT EXISTS IX_ScrapeJobs_IsEnabled ON ScrapeJobs (IsEnabled);
