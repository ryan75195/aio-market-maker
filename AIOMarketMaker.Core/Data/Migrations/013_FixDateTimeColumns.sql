-- Migration: 013_FixDateTimeColumns
-- Description: Converts LastRunUtc and CreatedUtc from NVARCHAR to DATETIME2 for proper EF Core mapping
-- Date: 2025-12-24

-- Fix LastRunUtc column if it's NVARCHAR
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'ScrapeJobs' AND COLUMN_NAME = 'LastRunUtc' AND DATA_TYPE = 'nvarchar'
)
BEGIN
    -- Add temp column
    ALTER TABLE ScrapeJobs ADD LastRunUtc_New DATETIME2 NULL;

    -- Copy data, converting string to datetime
    UPDATE ScrapeJobs SET LastRunUtc_New = TRY_CAST(LastRunUtc AS DATETIME2);

    -- Drop old column
    ALTER TABLE ScrapeJobs DROP COLUMN LastRunUtc;

    -- Rename new column
    EXEC sp_rename 'ScrapeJobs.LastRunUtc_New', 'LastRunUtc', 'COLUMN';

    PRINT 'LastRunUtc converted from NVARCHAR to DATETIME2';
END

-- Fix CreatedUtc column if it's NVARCHAR
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'ScrapeJobs' AND COLUMN_NAME = 'CreatedUtc' AND DATA_TYPE = 'nvarchar'
)
BEGIN
    -- Add temp column with default
    ALTER TABLE ScrapeJobs ADD CreatedUtc_New DATETIME2 NOT NULL DEFAULT GETUTCDATE();

    -- Copy data, converting string to datetime
    UPDATE ScrapeJobs SET CreatedUtc_New = COALESCE(TRY_CAST(CreatedUtc AS DATETIME2), GETUTCDATE());

    -- Drop default constraint on old column if exists
    DECLARE @constraintName NVARCHAR(128);
    SELECT @constraintName = dc.name
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    WHERE dc.parent_object_id = OBJECT_ID('ScrapeJobs') AND c.name = 'CreatedUtc';

    IF @constraintName IS NOT NULL
        EXEC('ALTER TABLE ScrapeJobs DROP CONSTRAINT ' + @constraintName);

    -- Drop old column
    ALTER TABLE ScrapeJobs DROP COLUMN CreatedUtc;

    -- Rename new column
    EXEC sp_rename 'ScrapeJobs.CreatedUtc_New', 'CreatedUtc', 'COLUMN';

    PRINT 'CreatedUtc converted from NVARCHAR to DATETIME2';
END
