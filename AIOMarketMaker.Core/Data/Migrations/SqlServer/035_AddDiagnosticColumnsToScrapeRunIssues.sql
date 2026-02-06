-- Migration: 035_AddDiagnosticColumnsToScrapeRunIssues
-- Description: Adds Phase, StackTrace, and HttpStatusCode columns for failure diagnostics. Widens ErrorMessage to 2000 chars.
-- Date: 2026-02-06

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunIssues') AND name = 'Phase')
BEGIN
    ALTER TABLE ScrapeRunIssues ADD Phase NVARCHAR(50) NULL;
END
GO

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunIssues') AND name = 'StackTrace')
BEGIN
    ALTER TABLE ScrapeRunIssues ADD StackTrace NVARCHAR(MAX) NULL;
END
GO

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunIssues') AND name = 'HttpStatusCode')
BEGIN
    ALTER TABLE ScrapeRunIssues ADD HttpStatusCode INT NULL;
END
GO

IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunIssues') AND name = 'ErrorMessage')
BEGIN
    ALTER TABLE ScrapeRunIssues ALTER COLUMN ErrorMessage NVARCHAR(2000) NULL;
END
GO
