-- Migration: 001_InitialCreate
-- Description: Creates all database tables with final schema
-- Date: 2025-12-23

-- ScrapeJobs: Job configuration for eBay scraping
CREATE TABLE IF NOT EXISTS ScrapeJobs (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SearchTerm TEXT NOT NULL,
    FilterInstructions TEXT NULL,
    IsEnabled INTEGER NOT NULL DEFAULT 1,
    LastRunUtc TEXT NULL,
    CreatedUtc TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS IX_ScrapeJobs_IsEnabled ON ScrapeJobs (IsEnabled);

-- Listings: Raw scraped listing data
CREATE TABLE IF NOT EXISTS Listings (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ListingId TEXT NOT NULL,
    ScrapeJobId INTEGER NOT NULL,
    Title TEXT NULL,
    Price REAL NULL,
    Currency TEXT NULL,
    ShippingCost REAL NULL,
    Url TEXT NULL,
    Condition TEXT NULL,
    ListingStatus TEXT NULL,
    PurchaseFormat TEXT NULL,
    Description TEXT NULL,
    ItemSpecifics TEXT NULL,
    Images TEXT NULL,
    Location TEXT NULL,
    EndDateUtc TEXT NULL,
    CreatedUtc TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedUtc TEXT NULL,
    FOREIGN KEY (ScrapeJobId) REFERENCES ScrapeJobs(Id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS IX_Listings_ListingId ON Listings (ListingId);
CREATE INDEX IF NOT EXISTS IX_Listings_ScrapeJobId ON Listings (ScrapeJobId);
CREATE INDEX IF NOT EXISTS IX_Listings_ListingStatus ON Listings (ListingStatus);

-- ListingStatusHistory: Tracks status changes over time
CREATE TABLE IF NOT EXISTS ListingStatusHistory (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ListingId INTEGER NOT NULL,
    ListingStatus TEXT NOT NULL,
    Price REAL NULL,
    SoldDateUtc TEXT NULL,
    RecordedUtc TEXT NOT NULL DEFAULT (datetime('now')),
    Source TEXT NULL,
    FOREIGN KEY (ListingId) REFERENCES Listings(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_ListingStatusHistory_ListingId ON ListingStatusHistory (ListingId);
CREATE INDEX IF NOT EXISTS IX_ListingStatusHistory_RecordedUtc ON ListingStatusHistory (RecordedUtc);
