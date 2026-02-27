-- Migration: 047_AddCurrentPostStageColumn
-- Description: Tracks which post-processing stage is currently running
-- Date: 2026-02-27

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRuns') AND name = 'CurrentPostStage')
BEGIN
    ALTER TABLE ScrapeRuns ADD CurrentPostStage NVARCHAR(100) NULL;
END
