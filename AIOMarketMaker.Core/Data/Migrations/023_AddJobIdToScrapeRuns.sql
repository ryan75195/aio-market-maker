-- Migration: 023_AddJobIdToScrapeRuns
-- Description: Adds JobId column to ScrapeRuns for per-job tracking
-- Date: 2026-01-28

ALTER TABLE ScrapeRuns ADD COLUMN JobId INTEGER NULL;

CREATE INDEX IF NOT EXISTS IX_ScrapeRuns_JobId ON ScrapeRuns (JobId);
