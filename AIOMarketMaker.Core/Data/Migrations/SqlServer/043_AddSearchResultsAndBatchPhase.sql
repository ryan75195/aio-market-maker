-- Migration: 043_AddSearchResultsAndBatchPhase
-- Description: Adds SearchResultsJson and BatchPhase columns to ScrapeRuns for phased batch pipeline
-- Date: 2026-02-26

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRuns') AND name = 'SearchResultsJson')
BEGIN
    ALTER TABLE ScrapeRuns ADD SearchResultsJson NVARCHAR(MAX) NULL;
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRuns') AND name = 'BatchPhase')
BEGIN
    ALTER TABLE ScrapeRuns ADD BatchPhase NVARCHAR(20) NULL;
END
