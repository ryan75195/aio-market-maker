-- Migration: 001_InitialCreate
-- Description: Creates the initial ScrapeJobs table
-- Date: 2025-11-26

CREATE TABLE IF NOT EXISTS ScrapeJobs (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SearchTerm TEXT NOT NULL,
    BuyingFormat TEXT NOT NULL,
    Condition TEXT NOT NULL,
    SearchType TEXT NOT NULL,
    FrequencyMinutes INTEGER NOT NULL DEFAULT 60,
    LookbackDays INTEGER NULL,
    ItemLimit INTEGER NULL,
    IsEnabled INTEGER NOT NULL DEFAULT 1,
    LastRunUtc TEXT NULL,
    CreatedUtc TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Create index on SearchType and IsEnabled for common query patterns
CREATE INDEX IF NOT EXISTS IX_ScrapeJobs_SearchType_IsEnabled
ON ScrapeJobs (SearchType, IsEnabled);

-- Insert sample job for testing
INSERT INTO ScrapeJobs (SearchTerm, BuyingFormat, Condition, SearchType, FrequencyMinutes, LookbackDays, IsEnabled)
VALUES ('Playstation 5 Console', 'BUY_NOW', 'USED', 'SOLD', 360, 7, 1);
