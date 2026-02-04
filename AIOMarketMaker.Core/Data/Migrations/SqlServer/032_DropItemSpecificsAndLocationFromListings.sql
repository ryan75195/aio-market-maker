-- Migration: 032_DropItemSpecificsAndLocationFromListings
-- Description: Remove unused ItemSpecifics and Location columns from Listings table
-- Date: 2026-02-04

IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Listings') AND name = 'ItemSpecifics')
BEGIN
    ALTER TABLE Listings DROP COLUMN ItemSpecifics;
END

IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Listings') AND name = 'Location')
BEGIN
    ALTER TABLE Listings DROP COLUMN Location;
END
