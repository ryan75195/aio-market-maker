-- Migration: 044_AddPhaseTimestamps
-- Description: Add SearchCompletedUtc and ProcessingStartedUtc to ScrapeRuns for phase timing
-- Date: 2026-02-26

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRuns') AND name = 'SearchCompletedUtc')
BEGIN
    ALTER TABLE ScrapeRuns ADD SearchCompletedUtc DATETIME2 NULL;
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRuns') AND name = 'ProcessingStartedUtc')
BEGIN
    ALTER TABLE ScrapeRuns ADD ProcessingStartedUtc DATETIME2 NULL;
END
