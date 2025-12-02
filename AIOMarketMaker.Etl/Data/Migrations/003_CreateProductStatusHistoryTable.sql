-- Migration: 003_CreateProductStatusHistoryTable
-- Description: Creates the ProductStatusHistory table for tracking listing status changes over time
-- Date: 2025-11-28

CREATE TABLE IF NOT EXISTS ProductStatusHistory (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ProductId INTEGER NOT NULL,
    ListingStatus TEXT NOT NULL,
    Price REAL NULL,
    SoldDateUtc TEXT NULL,
    RecordedUtc TEXT NOT NULL DEFAULT (datetime('now')),
    Source TEXT NULL,
    FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE CASCADE
);

-- Index for querying history by product
CREATE INDEX IF NOT EXISTS IX_ProductStatusHistory_ProductId
ON ProductStatusHistory (ProductId);

-- Index for querying by recorded time (for analytics/reporting)
CREATE INDEX IF NOT EXISTS IX_ProductStatusHistory_RecordedUtc
ON ProductStatusHistory (RecordedUtc);
