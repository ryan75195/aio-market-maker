-- Migration: 038_AddBatchIdToScrapeRuns
-- Description: Add BatchId column to ScrapeRuns for grouping runs triggered together
-- Date: 2026-02-16

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRuns') AND name = 'BatchId')
BEGIN
    ALTER TABLE ScrapeRuns ADD BatchId UNIQUEIDENTIFIER NULL;
END

GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScrapeRuns_BatchId')
BEGIN
    CREATE INDEX IX_ScrapeRuns_BatchId ON ScrapeRuns (BatchId) WHERE BatchId IS NOT NULL;
END
