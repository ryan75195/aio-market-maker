-- Migration: 006_AddProductName
-- Description: Adds ProductName column to Products table for canonical product names
-- Date: 2025-12-02

ALTER TABLE Products ADD COLUMN ProductName TEXT NULL;

CREATE INDEX IF NOT EXISTS IX_Products_ProductName ON Products (ProductName);
