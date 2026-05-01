-- Migration: 049_AddTaxonomyAxisImportance
-- Description: Adds Importance column to TaxonomyAxes for LLM-ranked price relevance
-- Date: 2026-03-08

IF NOT EXISTS (
    SELECT * FROM sys.columns
    WHERE object_id = OBJECT_ID('TaxonomyAxes') AND name = 'Importance'
)
BEGIN
    ALTER TABLE TaxonomyAxes ADD Importance INT NULL;
END
