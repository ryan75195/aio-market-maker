-- Migration: 016_DropUnusedScrapeJobColumns
-- Description: Removes legacy columns from ScrapeJobs that are no longer used
-- Date: 2025-12-24

-- Helper: Drop default constraint on a column before dropping the column
DECLARE @sql NVARCHAR(MAX);

-- Drop all default constraints on columns we want to remove
SET @sql = '';
SELECT @sql = @sql + 'ALTER TABLE ScrapeJobs DROP CONSTRAINT ' + dc.name + ';' + CHAR(13)
FROM sys.default_constraints dc
INNER JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE dc.parent_object_id = OBJECT_ID('ScrapeJobs')
AND c.name IN ('BuyingFormat', 'Condition', 'SearchType', 'FrequencyMinutes', 'LookbackDays', 'ItemLimit');

IF @sql <> ''
    EXEC sp_executesql @sql;

-- Now drop unused columns if they exist
IF COL_LENGTH('ScrapeJobs', 'BuyingFormat') IS NOT NULL
    ALTER TABLE ScrapeJobs DROP COLUMN BuyingFormat;

IF COL_LENGTH('ScrapeJobs', 'Condition') IS NOT NULL
    ALTER TABLE ScrapeJobs DROP COLUMN [Condition];

IF COL_LENGTH('ScrapeJobs', 'SearchType') IS NOT NULL
    ALTER TABLE ScrapeJobs DROP COLUMN SearchType;

IF COL_LENGTH('ScrapeJobs', 'FrequencyMinutes') IS NOT NULL
    ALTER TABLE ScrapeJobs DROP COLUMN FrequencyMinutes;

IF COL_LENGTH('ScrapeJobs', 'LookbackDays') IS NOT NULL
    ALTER TABLE ScrapeJobs DROP COLUMN LookbackDays;

IF COL_LENGTH('ScrapeJobs', 'ItemLimit') IS NOT NULL
    ALTER TABLE ScrapeJobs DROP COLUMN ItemLimit;

PRINT 'Dropped unused ScrapeJobs columns';
