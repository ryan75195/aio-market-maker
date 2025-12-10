-- Migration: 012_DropProductTables
-- Description: Drops clustering/product tables - simplifying to listings-only
-- Date: 2025-12-10

-- Drop product assignment and clustering tables
DROP TABLE IF EXISTS ListingProductAssignments;
DROP TABLE IF EXISTS Products;
DROP TABLE IF EXISTS MetadataGroups;
