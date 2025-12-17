-- Migration: 011_AddClusteringSchema
-- Description: Transform Products to cluster-level entities and add clustering tables
-- Date: 2025-12-09
-- BREAKING CHANGE: Products table is recreated - old data will be lost

-- ============================================================================
-- 1. Create MetadataGroups table
-- Groups listings by universal metadata fields for clustering
-- ============================================================================
CREATE TABLE IF NOT EXISTS MetadataGroups (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ScrapeJobId INTEGER NOT NULL,
    Condition TEXT,
    PurchaseFormat TEXT,
    CreatedUtc TEXT NOT NULL DEFAULT (datetime('now')),

    UNIQUE(ScrapeJobId, Condition, PurchaseFormat),
    FOREIGN KEY (ScrapeJobId) REFERENCES ScrapeJobs(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_MetadataGroups_ScrapeJobId ON MetadataGroups (ScrapeJobId);

-- ============================================================================
-- 2. Recreate Products table as cluster-level entities
-- Products now represent clusters of similar listings, not individual listings
-- ============================================================================
DROP TABLE IF EXISTS Products;

CREATE TABLE Products (
    -- Primary Key
    Id INTEGER PRIMARY KEY AUTOINCREMENT,

    -- Cluster Identification
    MetadataGroupId INTEGER NOT NULL,
    ClusterLabel INTEGER NOT NULL DEFAULT 0,  -- DBSCAN cluster ID (-1 = noise/outlier)

    -- Product Identity
    ProductName TEXT,                         -- Auto-generated or LLM-labeled name for cluster
    Category TEXT,                            -- Inherited from search term
    CentroidVectorId TEXT,                    -- Pinecone vector ID for cluster centroid

    -- Pricing Statistics (aggregated from all listings in cluster)
    ListingCount INTEGER NOT NULL DEFAULT 0,
    AvgPrice REAL,
    MedianPrice REAL,
    MinPrice REAL,
    MaxPrice REAL,
    StdDevPrice REAL,
    AvgPriceWithShipping REAL,
    MedianPriceWithShipping REAL,

    -- Denormalized metadata (from MetadataGroup for easier queries)
    Condition TEXT,
    PurchaseFormat TEXT,

    -- Timestamps
    CreatedUtc TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedUtc TEXT,

    -- Constraints
    UNIQUE(MetadataGroupId, ClusterLabel),
    FOREIGN KEY (MetadataGroupId) REFERENCES MetadataGroups(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_Products_MetadataGroupId ON Products (MetadataGroupId);
CREATE INDEX IF NOT EXISTS IX_Products_ClusterLabel ON Products (ClusterLabel);
CREATE INDEX IF NOT EXISTS IX_Products_ProductName ON Products (ProductName);
CREATE INDEX IF NOT EXISTS IX_Products_Category ON Products (Category);
CREATE INDEX IF NOT EXISTS IX_Products_Condition ON Products (Condition);
CREATE INDEX IF NOT EXISTS IX_Products_AvgPrice ON Products (AvgPrice);

-- ============================================================================
-- 3. Create ListingProductAssignments table
-- Maps individual listings to their cluster/product
-- ============================================================================
CREATE TABLE IF NOT EXISTS ListingProductAssignments (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ListingId INTEGER NOT NULL UNIQUE,
    ProductId INTEGER NOT NULL,
    DistanceToCenter REAL,                    -- Cosine distance to cluster centroid
    AssignedUtc TEXT NOT NULL DEFAULT (datetime('now')),

    FOREIGN KEY (ListingId) REFERENCES Listings(Id) ON DELETE CASCADE,
    FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_ListingProductAssignments_ListingId ON ListingProductAssignments (ListingId);
CREATE INDEX IF NOT EXISTS IX_ListingProductAssignments_ProductId ON ListingProductAssignments (ProductId);
