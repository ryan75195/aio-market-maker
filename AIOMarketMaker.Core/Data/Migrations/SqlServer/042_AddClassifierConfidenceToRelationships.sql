-- Migration: 042_AddClassifierConfidenceToRelationships
-- Description: Adds ClassifierConfidence column to store ensemble classifier confidence as a proper column
-- Date: 2026-02-23

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ListingRelationships') AND name = 'ClassifierConfidence')
BEGIN
    ALTER TABLE ListingRelationships ADD ClassifierConfidence FLOAT NULL;
END
