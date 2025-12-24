-- Migration: 015_DropUnusedScrapeJobColumns
-- Description: Removes legacy columns from ScrapeJobs that are no longer used
-- Date: 2025-12-24

-- Drop unused columns if they exist
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
