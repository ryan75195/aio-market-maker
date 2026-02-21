-- Migration: 041_DropListingPredictionsView
-- Description: Drop vw_ListingPredictions view — replaced by ListingPredictionService CTE queries
-- Date: 2026-02-21

IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_ListingPredictions')
BEGIN
    DROP VIEW vw_ListingPredictions;
END
