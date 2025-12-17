-- Migration: 007_DenormalizeProducts
-- Description: Adds denormalized fields from Listing to Products table
-- Date: 2025-12-02

ALTER TABLE Products ADD COLUMN EbayListingId TEXT NULL;
ALTER TABLE Products ADD COLUMN Title TEXT NULL;
ALTER TABLE Products ADD COLUMN Price REAL NULL;
ALTER TABLE Products ADD COLUMN Currency TEXT NULL;
ALTER TABLE Products ADD COLUMN ShippingCost REAL NULL;
ALTER TABLE Products ADD COLUMN Url TEXT NULL;
ALTER TABLE Products ADD COLUMN Condition TEXT NULL;
ALTER TABLE Products ADD COLUMN ListingStatus TEXT NULL;
ALTER TABLE Products ADD COLUMN PurchaseFormat TEXT NULL;
ALTER TABLE Products ADD COLUMN Location TEXT NULL;
ALTER TABLE Products ADD COLUMN EndDateUtc TEXT NULL;

CREATE INDEX IF NOT EXISTS IX_Products_EbayListingId ON Products (EbayListingId);
CREATE INDEX IF NOT EXISTS IX_Products_ListingStatus ON Products (ListingStatus);
CREATE INDEX IF NOT EXISTS IX_Products_Price ON Products (Price);
