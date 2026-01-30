-- Migration: 027_CreateScrapeRunIssuesTable (SQL Server)
-- Description: Creates table to track partial failures during ETL processing (e.g., bot detection, fetch failures)
-- Date: 2026-01-30

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ScrapeRunIssues')
BEGIN
    CREATE TABLE ScrapeRunIssues (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ScrapeRunId INT NOT NULL,
        ListingId NVARCHAR(50) NOT NULL,
        IssueType NVARCHAR(50) NOT NULL,
        ErrorMessage NVARCHAR(500) NULL,
        CreatedUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        FOREIGN KEY (ScrapeRunId) REFERENCES ScrapeRuns(Id) ON DELETE CASCADE
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScrapeRunIssues_ScrapeRunId')
BEGIN
    CREATE INDEX IX_ScrapeRunIssues_ScrapeRunId ON ScrapeRunIssues (ScrapeRunId);
END
GO
