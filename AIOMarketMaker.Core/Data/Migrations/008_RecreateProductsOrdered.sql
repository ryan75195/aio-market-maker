-- Migration: 008_RecreateProductsOrdered
-- Description: Recreate Products table with columns in logical order
-- Date: 2025-12-02
-- Note: Only safe to run when Products table is empty

DROP TABLE IF EXISTS Products;

CREATE TABLE Products (
    -- Primary Key
    Id INTEGER PRIMARY KEY AUTOINCREMENT,

    -- Core Identification
    EbayListingId TEXT NULL,
    ProductName TEXT NULL,
    Title TEXT NULL,
    Url TEXT NULL,

    -- Pricing
    Price REAL NULL,
    Currency TEXT NULL,
    ShippingCost REAL NULL,

    -- Classification
    Category TEXT NOT NULL,
    CategoryConfidence REAL NULL,
    Condition TEXT NULL,
    ListingStatus TEXT NULL,
    PurchaseFormat TEXT NULL,

    -- Product Attributes (LLM-normalized)
    Brand TEXT NULL,
    Model TEXT NULL,
    StorageCapacity TEXT NULL,
    Color TEXT NULL,
    Edition TEXT NULL,
    VariantType TEXT NULL,
    BundledItems TEXT NULL,

    -- Location
    Location TEXT NULL,

    -- Dates
    ListedDateUtc TEXT NULL,
    SoldDateUtc TEXT NULL,
    EndDateUtc TEXT NULL,
    ResolvedUtc TEXT NOT NULL DEFAULT (datetime('now')),

    -- Foreign Keys
    ListingId INTEGER NOT NULL UNIQUE,

    FOREIGN KEY (ListingId) REFERENCES Listings(Id) ON DELETE CASCADE
);

-- Indexes
CREATE INDEX IF NOT EXISTS IX_Products_ListingId ON Products (ListingId);
CREATE INDEX IF NOT EXISTS IX_Products_EbayListingId ON Products (EbayListingId);
CREATE INDEX IF NOT EXISTS IX_Products_ProductName ON Products (ProductName);
CREATE INDEX IF NOT EXISTS IX_Products_Category ON Products (Category);
CREATE INDEX IF NOT EXISTS IX_Products_ListingStatus ON Products (ListingStatus);
CREATE INDEX IF NOT EXISTS IX_Products_Price ON Products (Price);
CREATE INDEX IF NOT EXISTS IX_Products_Brand ON Products (Brand);
CREATE INDEX IF NOT EXISTS IX_Products_Model ON Products (Model);
