-- Migration: 045_AddFilteredIndexForComparables
-- Description: Create a filtered index on ListingRelationships for IsComparable = 1
-- to speed up the prediction CTE which joins 4M+ rows but only needs the ~22% that are comparable.
-- Date: 2026-02-27

SET QUOTED_IDENTIFIER ON;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ListingRelationships_Comparable_Filtered' AND object_id = OBJECT_ID('ListingRelationships'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_ListingRelationships_Comparable_Filtered
    ON ListingRelationships (ListingIdA, ListingIdB)
    INCLUDE (ClassifierConfidence, SimilarityScore)
    WHERE IsComparable = 1;
END
