-- Migration: 005_AddProductDateColumns
-- Description: Adds ListedDateUtc and SoldDateUtc columns to Products table
-- Date: 2025-12-02

-- Add ListedDateUtc column (when the item was listed/first scraped for active listings)
ALTER TABLE Products ADD COLUMN ListedDateUtc TEXT NULL;

-- Add SoldDateUtc column (when the item sold for sold listings)
ALTER TABLE Products ADD COLUMN SoldDateUtc TEXT NULL;

-- Create index for date queries
CREATE INDEX IF NOT EXISTS IX_Products_SoldDateUtc ON Products (SoldDateUtc);
CREATE INDEX IF NOT EXISTS IX_Products_ListedDateUtc ON Products (ListedDateUtc);
