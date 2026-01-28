-- Migration: 025_AddErrorMessageToScrapeRunListings (SQL Server)
-- Description: Adds ErrorMessage column to track failure reasons for debugging
-- Date: 2026-01-28

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunListings') AND name = 'ErrorMessage')
BEGIN
    ALTER TABLE ScrapeRunListings ADD ErrorMessage NVARCHAR(500) NULL;
END
GO
