-- Migration: 002_CreateProductsTable
-- Description: Creates the Products table for storing scraped eBay listings
-- Date: 2025-11-26

CREATE TABLE IF NOT EXISTS Products (
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
    FOREIGN KEY (ScrapeJobId) REFERENCES ScrapeJobs(Id)
);

-- Unique constraint on ListingId to prevent duplicates
CREATE UNIQUE INDEX IF NOT EXISTS IX_Products_ListingId
ON Products (ListingId);

-- Index for querying by ScrapeJob
CREATE INDEX IF NOT EXISTS IX_Products_ScrapeJobId
ON Products (ScrapeJobId);

-- Index for querying by ListingStatus
CREATE INDEX IF NOT EXISTS IX_Products_ListingStatus
ON Products (ListingStatus);
