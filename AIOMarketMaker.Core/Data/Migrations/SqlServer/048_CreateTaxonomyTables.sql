-- Migration: 048_CreateTaxonomyTables
-- Description: Creates tables for taxonomy pipeline results (axes, values, listing assignments)
-- Date: 2026-03-06

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'TaxonomyRuns')
BEGIN
    CREATE TABLE TaxonomyRuns (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ScrapeJobId INT NOT NULL,
        CoveragePercent FLOAT NOT NULL DEFAULT 0,
        ConflictPercent FLOAT NOT NULL DEFAULT 0,
        TotalListings INT NOT NULL DEFAULT 0,
        AssignedListings INT NOT NULL DEFAULT 0,
        AxisCount INT NOT NULL DEFAULT 0,
        DurationMs INT NOT NULL DEFAULT 0,
        CreatedUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_TaxonomyRuns_ScrapeJobs
            FOREIGN KEY (ScrapeJobId) REFERENCES ScrapeJobs(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_TaxonomyRuns_ScrapeJobId ON TaxonomyRuns (ScrapeJobId);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'TaxonomyAxes')
BEGIN
    CREATE TABLE TaxonomyAxes (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        TaxonomyRunId INT NOT NULL,
        Name NVARCHAR(100) NOT NULL,
        SortOrder INT NOT NULL DEFAULT 0,
        CONSTRAINT FK_TaxonomyAxes_TaxonomyRuns
            FOREIGN KEY (TaxonomyRunId) REFERENCES TaxonomyRuns(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_TaxonomyAxes_TaxonomyRunId ON TaxonomyAxes (TaxonomyRunId);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'TaxonomyAxisValues')
BEGIN
    CREATE TABLE TaxonomyAxisValues (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        TaxonomyAxisId INT NOT NULL,
        Label NVARCHAR(200) NOT NULL,
        NgramsJson NVARCHAR(MAX) NULL,
        SortOrder INT NOT NULL DEFAULT 0,
        CONSTRAINT FK_TaxonomyAxisValues_TaxonomyAxes
            FOREIGN KEY (TaxonomyAxisId) REFERENCES TaxonomyAxes(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_TaxonomyAxisValues_TaxonomyAxisId ON TaxonomyAxisValues (TaxonomyAxisId);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'TaxonomyListingAssignments')
BEGIN
    CREATE TABLE TaxonomyListingAssignments (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        TaxonomyRunId INT NOT NULL,
        ListingId INT NOT NULL,
        CellJson NVARCHAR(500) NOT NULL,
        HasConflict BIT NOT NULL DEFAULT 0,
        CONSTRAINT FK_TaxonomyListingAssignments_TaxonomyRuns
            FOREIGN KEY (TaxonomyRunId) REFERENCES TaxonomyRuns(Id) ON DELETE CASCADE,
        CONSTRAINT FK_TaxonomyListingAssignments_Listings
            FOREIGN KEY (ListingId) REFERENCES Listings(Id) ON DELETE NO ACTION
    );
    CREATE INDEX IX_TaxonomyListingAssignments_TaxonomyRunId
        ON TaxonomyListingAssignments (TaxonomyRunId) INCLUDE (ListingId, CellJson, HasConflict);
    CREATE INDEX IX_TaxonomyListingAssignments_ListingId
        ON TaxonomyListingAssignments (ListingId);
END
