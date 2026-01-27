-- Migration: 022_CreateScrapeRunListingsTable
-- Description: Creates junction table to link ScrapeRuns to Listings for progress tracking
-- Date: 2026-01-27

CREATE TABLE IF NOT EXISTS ScrapeRunListings (
    ScrapeRunId INTEGER NOT NULL,
    ScrapeJobId INTEGER NOT NULL,
    ListingId VARCHAR(20) NOT NULL,
    Status TEXT DEFAULT 'Pending',
    CreatedUtc TEXT DEFAULT (datetime('now')),
    CompletedUtc TEXT NULL,
    PRIMARY KEY (ScrapeRunId, ListingId),
    FOREIGN KEY (ScrapeRunId) REFERENCES ScrapeRuns(Id) ON DELETE CASCADE,
    FOREIGN KEY (ScrapeJobId) REFERENCES ScrapeJobs(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_ScrapeRunListings_ListingId ON ScrapeRunListings (ListingId);
CREATE INDEX IF NOT EXISTS IX_ScrapeRunListings_Status ON ScrapeRunListings (ScrapeRunId, Status);
