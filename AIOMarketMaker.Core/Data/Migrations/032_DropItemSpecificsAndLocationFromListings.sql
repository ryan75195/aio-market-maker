-- Migration: 032_DropItemSpecificsAndLocationFromListings
-- Description: Remove unused ItemSpecifics and Location columns from Listings table
-- Date: 2026-02-04
-- Note: SQLite 3.35.0+ supports DROP COLUMN natively

ALTER TABLE Listings DROP COLUMN ItemSpecifics;
ALTER TABLE Listings DROP COLUMN Location;
