-- Migration: 019_CreateScrapeRunsTable
-- Description: Creates the ScrapeRuns table to track orchestration history
-- Date: 2026-01-26

CREATE TABLE IF NOT EXISTS ScrapeRuns (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    InstanceId TEXT,
    TriggerType TEXT NOT NULL DEFAULT 'Manual',
    StartedUtc TEXT NOT NULL DEFAULT (datetime('now')),
    CompletedUtc TEXT,
    Status TEXT NOT NULL DEFAULT 'Running',
    ListingsAdded INTEGER NOT NULL DEFAULT 0,
    ListingsSkipped INTEGER NOT NULL DEFAULT 0,
    ErrorMessage TEXT
);

CREATE INDEX IF NOT EXISTS IX_ScrapeRuns_StartedUtc ON ScrapeRuns (StartedUtc);
CREATE INDEX IF NOT EXISTS IX_ScrapeRuns_InstanceId ON ScrapeRuns (InstanceId);
