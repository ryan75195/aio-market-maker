-- Migration: 002_FixIsEnabledColumnType
-- Description: Converts IsEnabled column from INT to BIT for proper EF Core boolean mapping
-- Date: 2025-12-24

-- Only run if IsEnabled is currently INT type
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'ScrapeJobs'
    AND COLUMN_NAME = 'IsEnabled'
    AND DATA_TYPE = 'int'
)
BEGIN
    -- Clean up any leftover temp column from failed previous runs
    -- Must drop default constraint first before dropping column
    DECLARE @constraintName NVARCHAR(128);
    SELECT @constraintName = dc.name
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    WHERE dc.parent_object_id = OBJECT_ID('ScrapeJobs') AND c.name = 'IsEnabled_Temp';

    IF @constraintName IS NOT NULL
        EXEC('ALTER TABLE ScrapeJobs DROP CONSTRAINT ' + @constraintName);

    IF COL_LENGTH('ScrapeJobs', 'IsEnabled_Temp') IS NOT NULL
        ALTER TABLE ScrapeJobs DROP COLUMN IsEnabled_Temp;

    -- Add temp column with BIT type
    ALTER TABLE ScrapeJobs ADD IsEnabled_Temp BIT NOT NULL DEFAULT 1;

    -- Copy data, converting INT to BIT
    UPDATE ScrapeJobs SET IsEnabled_Temp = CAST(IsEnabled AS BIT);

    -- Drop any indexes on IsEnabled
    DECLARE @sql NVARCHAR(MAX) = '';
    SELECT @sql += 'DROP INDEX ' + i.name + ' ON ScrapeJobs;' + CHAR(13)
    FROM sys.indexes i
    INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
    WHERE i.object_id = OBJECT_ID('ScrapeJobs') AND c.name = 'IsEnabled';
    IF @sql <> '' EXEC sp_executesql @sql;

    -- Drop old column
    ALTER TABLE ScrapeJobs DROP COLUMN IsEnabled;

    -- Rename temp to IsEnabled
    EXEC sp_rename 'ScrapeJobs.IsEnabled_Temp', 'IsEnabled', 'COLUMN';

    PRINT 'IsEnabled column converted from INT to BIT';
END
ELSE
BEGIN
    PRINT 'IsEnabled column is already correct type, skipping';
END
