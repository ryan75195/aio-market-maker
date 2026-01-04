-- Migration: 017_FixListingsDateTimeColumns
-- Description: Converts datetime columns in Listings and ListingStatusHistory from NVARCHAR to DATETIME2
-- Date: 2026-01-04

-- Fix Listings.EndDateUtc
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'Listings' AND COLUMN_NAME = 'EndDateUtc' AND DATA_TYPE = 'nvarchar'
)
BEGIN
    IF COL_LENGTH('Listings', 'EndDateUtc_New') IS NOT NULL
        EXEC('ALTER TABLE Listings DROP COLUMN EndDateUtc_New');
    EXEC('ALTER TABLE Listings ADD EndDateUtc_New DATETIME2 NULL');
    EXEC('UPDATE Listings SET EndDateUtc_New = TRY_CAST(EndDateUtc AS DATETIME2)');
    EXEC('ALTER TABLE Listings DROP COLUMN EndDateUtc');
    EXEC sp_rename 'Listings.EndDateUtc_New', 'EndDateUtc', 'COLUMN';
    PRINT 'Listings.EndDateUtc converted from NVARCHAR to DATETIME2';
END

-- Fix Listings.CreatedUtc
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'Listings' AND COLUMN_NAME = 'CreatedUtc' AND DATA_TYPE = 'nvarchar'
)
BEGIN
    IF COL_LENGTH('Listings', 'CreatedUtc_New') IS NOT NULL
        EXEC('ALTER TABLE Listings DROP COLUMN CreatedUtc_New');

    DECLARE @constraintName1 NVARCHAR(128);
    SELECT @constraintName1 = dc.name
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    WHERE dc.parent_object_id = OBJECT_ID('Listings') AND c.name = 'CreatedUtc';
    IF @constraintName1 IS NOT NULL
        EXEC('ALTER TABLE Listings DROP CONSTRAINT ' + @constraintName1);

    EXEC('ALTER TABLE Listings ADD CreatedUtc_New DATETIME2 NOT NULL DEFAULT GETUTCDATE()');
    EXEC('UPDATE Listings SET CreatedUtc_New = COALESCE(TRY_CAST(CreatedUtc AS DATETIME2), GETUTCDATE())');
    EXEC('ALTER TABLE Listings DROP COLUMN CreatedUtc');
    EXEC sp_rename 'Listings.CreatedUtc_New', 'CreatedUtc', 'COLUMN';
    PRINT 'Listings.CreatedUtc converted from NVARCHAR to DATETIME2';
END

-- Fix Listings.UpdatedUtc
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'Listings' AND COLUMN_NAME = 'UpdatedUtc' AND DATA_TYPE = 'nvarchar'
)
BEGIN
    IF COL_LENGTH('Listings', 'UpdatedUtc_New') IS NOT NULL
        EXEC('ALTER TABLE Listings DROP COLUMN UpdatedUtc_New');
    EXEC('ALTER TABLE Listings ADD UpdatedUtc_New DATETIME2 NULL');
    EXEC('UPDATE Listings SET UpdatedUtc_New = TRY_CAST(UpdatedUtc AS DATETIME2)');
    EXEC('ALTER TABLE Listings DROP COLUMN UpdatedUtc');
    EXEC sp_rename 'Listings.UpdatedUtc_New', 'UpdatedUtc', 'COLUMN';
    PRINT 'Listings.UpdatedUtc converted from NVARCHAR to DATETIME2';
END

-- Fix ListingStatusHistory.SoldDateUtc
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'ListingStatusHistory' AND COLUMN_NAME = 'SoldDateUtc' AND DATA_TYPE = 'nvarchar'
)
BEGIN
    IF COL_LENGTH('ListingStatusHistory', 'SoldDateUtc_New') IS NOT NULL
        EXEC('ALTER TABLE ListingStatusHistory DROP COLUMN SoldDateUtc_New');
    EXEC('ALTER TABLE ListingStatusHistory ADD SoldDateUtc_New DATETIME2 NULL');
    EXEC('UPDATE ListingStatusHistory SET SoldDateUtc_New = TRY_CAST(SoldDateUtc AS DATETIME2)');
    EXEC('ALTER TABLE ListingStatusHistory DROP COLUMN SoldDateUtc');
    EXEC sp_rename 'ListingStatusHistory.SoldDateUtc_New', 'SoldDateUtc', 'COLUMN';
    PRINT 'ListingStatusHistory.SoldDateUtc converted from NVARCHAR to DATETIME2';
END

-- Fix ListingStatusHistory.RecordedUtc
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'ListingStatusHistory' AND COLUMN_NAME = 'RecordedUtc' AND DATA_TYPE = 'nvarchar'
)
BEGIN
    IF COL_LENGTH('ListingStatusHistory', 'RecordedUtc_New') IS NOT NULL
        EXEC('ALTER TABLE ListingStatusHistory DROP COLUMN RecordedUtc_New');

    DECLARE @constraintName2 NVARCHAR(128);
    SELECT @constraintName2 = dc.name
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    WHERE dc.parent_object_id = OBJECT_ID('ListingStatusHistory') AND c.name = 'RecordedUtc';
    IF @constraintName2 IS NOT NULL
        EXEC('ALTER TABLE ListingStatusHistory DROP CONSTRAINT ' + @constraintName2);

    EXEC('ALTER TABLE ListingStatusHistory ADD RecordedUtc_New DATETIME2 NOT NULL DEFAULT GETUTCDATE()');
    EXEC('UPDATE ListingStatusHistory SET RecordedUtc_New = COALESCE(TRY_CAST(RecordedUtc AS DATETIME2), GETUTCDATE())');
    EXEC('ALTER TABLE ListingStatusHistory DROP COLUMN RecordedUtc');
    EXEC sp_rename 'ListingStatusHistory.RecordedUtc_New', 'RecordedUtc', 'COLUMN';
    PRINT 'ListingStatusHistory.RecordedUtc converted from NVARCHAR to DATETIME2';
END
