-- Migration: 009_AddFilterInstructionsToScrapeJobs
-- Description: Adds FilterInstructions column to ScrapeJobs table for custom filtering rules
-- Date: 2025-12-03

ALTER TABLE ScrapeJobs ADD COLUMN FilterInstructions TEXT NULL;
