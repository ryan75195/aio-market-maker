-- Migration: 039_CreateCategoriesAndJobCategories
-- Description: Create Categories table and JobCategories join table for many-to-many job tagging
-- Date: 2026-02-16

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Categories')
BEGIN
    CREATE TABLE Categories (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Name NVARCHAR(100) NOT NULL,
        IsEnabled BIT NOT NULL CONSTRAINT DF_Categories_IsEnabled DEFAULT 1,
        CreatedUtc DATETIME2 NOT NULL CONSTRAINT DF_Categories_CreatedUtc DEFAULT SYSUTCDATETIME()
    );
END

GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UQ_Categories_Name')
BEGIN
    CREATE UNIQUE INDEX UQ_Categories_Name ON Categories (Name);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'JobCategories')
BEGIN
    CREATE TABLE JobCategories (
        JobId INT NOT NULL,
        CategoryId INT NOT NULL,
        CONSTRAINT PK_JobCategories PRIMARY KEY (JobId, CategoryId),
        CONSTRAINT FK_JobCategories_ScrapeJobs FOREIGN KEY (JobId) REFERENCES ScrapeJobs(Id) ON DELETE CASCADE,
        CONSTRAINT FK_JobCategories_Categories FOREIGN KEY (CategoryId) REFERENCES Categories(Id) ON DELETE CASCADE
    );
END

GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_JobCategories_CategoryId')
BEGIN
    CREATE INDEX IX_JobCategories_CategoryId ON JobCategories (CategoryId);
END
