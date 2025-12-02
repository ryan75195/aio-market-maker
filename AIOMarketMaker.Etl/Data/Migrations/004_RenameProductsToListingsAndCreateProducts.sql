-- Migration: 004_RenameProductsToListingsAndCreateProducts
-- Description: Renames Products->Listings, ProductStatusHistory->ListingStatusHistory,
--              creates new Products table for LLM-normalized data
-- Date: 2025-12-02

-- Rename existing tables
ALTER TABLE Products RENAME TO Listings;
ALTER TABLE ProductStatusHistory RENAME TO ListingStatusHistory;

-- SQLite doesn't support RENAME COLUMN directly in older versions,
-- so we recreate the ListingStatusHistory table with correct column name
CREATE TABLE ListingStatusHistory_new (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ListingId INTEGER NOT NULL,
    ListingStatus TEXT NOT NULL,
    Price REAL NULL,
    SoldDateUtc TEXT NULL,
    RecordedUtc TEXT NOT NULL DEFAULT (datetime('now')),
    Source TEXT NULL,
    FOREIGN KEY (ListingId) REFERENCES Listings(Id) ON DELETE CASCADE
);

-- Copy data from old table (ProductId becomes ListingId)
INSERT INTO ListingStatusHistory_new (Id, ListingId, ListingStatus, Price, SoldDateUtc, RecordedUtc, Source)
SELECT Id, ProductId, ListingStatus, Price, SoldDateUtc, RecordedUtc, Source
FROM ListingStatusHistory;

-- Drop old table and rename new one
DROP TABLE ListingStatusHistory;
ALTER TABLE ListingStatusHistory_new RENAME TO ListingStatusHistory;

-- Create new Products table for LLM-normalized data
CREATE TABLE Products (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ListingId INTEGER NOT NULL UNIQUE,

    -- Classification (absolute categories)
    Category TEXT NOT NULL,
    CategoryConfidence REAL NULL,

    -- Normalized attributes
    Brand TEXT NULL,
    Model TEXT NULL,
    StorageCapacity TEXT NULL,
    Color TEXT NULL,
    Edition TEXT NULL,
    VariantType TEXT NULL,
    BundledItems TEXT NULL,

    -- Metadata
    ResolvedUtc TEXT NOT NULL DEFAULT (datetime('now')),

    FOREIGN KEY (ListingId) REFERENCES Listings(Id) ON DELETE CASCADE
);

-- Indexes for Products table
CREATE INDEX IF NOT EXISTS IX_Products_ListingId ON Products (ListingId);
CREATE INDEX IF NOT EXISTS IX_Products_Category ON Products (Category);
CREATE INDEX IF NOT EXISTS IX_Products_Brand ON Products (Brand);
CREATE INDEX IF NOT EXISTS IX_Products_Model ON Products (Model);

-- Index for ListingStatusHistory
CREATE INDEX IF NOT EXISTS IX_ListingStatusHistory_ListingId ON ListingStatusHistory (ListingId);
