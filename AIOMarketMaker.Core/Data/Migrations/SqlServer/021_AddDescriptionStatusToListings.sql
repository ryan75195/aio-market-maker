-- Migration: 021_AddDescriptionStatusToListings (SQL Server)
-- Description: Adds DescriptionStatus column to track description fetch status
-- Date: 2026-01-27

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Listings') AND name = 'DescriptionStatus')
BEGIN
    ALTER TABLE Listings ADD DescriptionStatus NVARCHAR(50) DEFAULT 'pending';
END
GO

-- Update existing rows with descriptions to 'complete'
UPDATE Listings SET DescriptionStatus = 'complete' WHERE Description IS NOT NULL AND Description <> '';

-- Update existing rows without descriptions to 'missing'
UPDATE Listings SET DescriptionStatus = 'missing' WHERE Description IS NULL OR Description = '';
GO
