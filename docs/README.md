# AIOMarketMaker Documentation

eBay marketplace data pipeline with AI-powered analysis.

## Quick Links

- [Getting Started](getting-started.md) - Local development setup
- [Troubleshooting](troubleshooting.md) - Common issues and fixes
- [API Reference](api/endpoints.md) - HTTP endpoints

## Architecture

- [Pipeline Overview](architecture/2026-01-31-simplified-pipeline-architecture.md) - How the scraping pipeline works
- [ETL Flow](architecture/etl-flow.md) - Data flow diagrams

## System Components

| Component | Purpose | Port |
|-----------|---------|------|
| AIOMarketMaker.Api | ASP.NET API (endpoints, scraping, scheduling) | 5000 |
| AIOMarketMaker.Console | CLI tools (backfill, validation, reindex, etc.) | - |
| AIOWebScraper | HTML fetching with anti-detection | 7126 |
| Desktop UI | Electron app for monitoring | - |

## Data Flow

```
1. User triggers scrape via UI
2. API creates ScrapeRun, searches eBay
3. API fetches listing HTML inline via ScraperWorker
4. API parses HTML, stores in SQL database
5. UI shows progress and results
```

## Investigations

Past bug investigations and debugging notes:

- [Blob Trigger Not Firing](investigations/2026-01-28-blob-trigger-not-firing.md)
- [Premature Scrape Run Completion](investigations/2026-01-28-premature-scrape-run-completion.md)

## Plans

Current and recent implementation plans:

- [Restore Pipeline Features](plans/2026-02-01-restore-pipeline-features-design.md)

Older plans are archived in [plans/_archive](plans/_archive/).

## External Resources

- [CLAUDE.md](../CLAUDE.md) - AI assistant instructions and project context
- [GitHub Repository](https://github.com/ryan75195/AIOMarketMaker)
