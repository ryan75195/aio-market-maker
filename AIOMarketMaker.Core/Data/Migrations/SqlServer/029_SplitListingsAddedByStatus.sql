-- Migration: 029_SplitListingsAddedByStatus
-- Description: Splits ListingsAdded into ListingsAddedActive and ListingsAddedSold columns
-- Date: 2026-01-30

-- Add new columns
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'ScrapeRuns') AND name = 'ListingsAddedActive')
BEGIN
    ALTER TABLE ScrapeRuns ADD ListingsAddedActive INT NOT NULL DEFAULT 0;
END

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'ScrapeRuns') AND name = 'ListingsAddedSold')
BEGIN
    ALTER TABLE ScrapeRuns ADD ListingsAddedSold INT NOT NULL DEFAULT 0;
END

-- Migrate existing data: copy ListingsAdded to ListingsAddedActive (best guess for historical data)
IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'ScrapeRuns') AND name = 'ListingsAdded')
BEGIN
    UPDATE ScrapeRuns SET ListingsAddedActive = ListingsAdded WHERE ListingsAddedActive = 0;
END

-- Drop the old column
IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'ScrapeRuns') AND name = 'ListingsAdded')
BEGIN
    ALTER TABLE ScrapeRuns DROP COLUMN ListingsAdded;
END
