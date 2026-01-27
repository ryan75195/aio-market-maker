-- Migration: 021_AddDescriptionStatusToListings
-- Description: Adds DescriptionStatus column to track description fetch status
-- Date: 2026-01-27

ALTER TABLE Listings ADD COLUMN DescriptionStatus TEXT DEFAULT 'pending';

-- Update existing rows with descriptions to 'complete'
UPDATE Listings SET DescriptionStatus = 'complete' WHERE Description IS NOT NULL AND Description != '';

-- Update existing rows without descriptions to 'missing' (they were processed before this feature)
UPDATE Listings SET DescriptionStatus = 'missing' WHERE Description IS NULL OR Description = '';
