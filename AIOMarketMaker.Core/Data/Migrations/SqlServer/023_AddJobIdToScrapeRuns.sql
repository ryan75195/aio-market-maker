-- Migration: 023_AddJobIdToScrapeRuns
-- Description: Adds JobId column to ScrapeRuns for per-job tracking
-- Date: 2026-01-28

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRuns') AND name = 'JobId')
BEGIN
    ALTER TABLE ScrapeRuns ADD JobId INT NULL;
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScrapeRuns_JobId' AND object_id = OBJECT_ID('ScrapeRuns'))
BEGIN
    CREATE INDEX IX_ScrapeRuns_JobId ON ScrapeRuns (JobId);
END
