-- Migration: 018_FixDecimalColumns
-- Description: Converts FLOAT columns to DECIMAL for proper EF Core mapping
-- Date: 2026-01-04

-- Fix Listings.Price (FLOAT -> DECIMAL(18,2))
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'Listings' AND COLUMN_NAME = 'Price' AND DATA_TYPE = 'float'
)
BEGIN
    IF COL_LENGTH('Listings', 'Price_New') IS NOT NULL
        EXEC('ALTER TABLE Listings DROP COLUMN Price_New');
    EXEC('ALTER TABLE Listings ADD Price_New DECIMAL(18,2) NULL');
    EXEC('UPDATE Listings SET Price_New = CAST(Price AS DECIMAL(18,2))');
    EXEC('ALTER TABLE Listings DROP COLUMN Price');
    EXEC sp_rename 'Listings.Price_New', 'Price', 'COLUMN';
    PRINT 'Listings.Price converted from FLOAT to DECIMAL(18,2)';
END

-- Fix Listings.ShippingCost (FLOAT -> DECIMAL(18,2))
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'Listings' AND COLUMN_NAME = 'ShippingCost' AND DATA_TYPE = 'float'
)
BEGIN
    IF COL_LENGTH('Listings', 'ShippingCost_New') IS NOT NULL
        EXEC('ALTER TABLE Listings DROP COLUMN ShippingCost_New');
    EXEC('ALTER TABLE Listings ADD ShippingCost_New DECIMAL(18,2) NULL');
    EXEC('UPDATE Listings SET ShippingCost_New = CAST(ShippingCost AS DECIMAL(18,2))');
    EXEC('ALTER TABLE Listings DROP COLUMN ShippingCost');
    EXEC sp_rename 'Listings.ShippingCost_New', 'ShippingCost', 'COLUMN';
    PRINT 'Listings.ShippingCost converted from FLOAT to DECIMAL(18,2)';
END

-- Fix ListingStatusHistory.Price (FLOAT -> DECIMAL(18,2))
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'ListingStatusHistory' AND COLUMN_NAME = 'Price' AND DATA_TYPE = 'float'
)
BEGIN
    IF COL_LENGTH('ListingStatusHistory', 'Price_New') IS NOT NULL
        EXEC('ALTER TABLE ListingStatusHistory DROP COLUMN Price_New');
    EXEC('ALTER TABLE ListingStatusHistory ADD Price_New DECIMAL(18,2) NULL');
    EXEC('UPDATE ListingStatusHistory SET Price_New = CAST(Price AS DECIMAL(18,2))');
    EXEC('ALTER TABLE ListingStatusHistory DROP COLUMN Price');
    EXEC sp_rename 'ListingStatusHistory.Price_New', 'Price', 'COLUMN';
    PRINT 'ListingStatusHistory.Price converted from FLOAT to DECIMAL(18,2)';
END
