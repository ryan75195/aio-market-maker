# 🛒 AIOMarketMaker

**AIOMarketMaker** is an intelligent eBay data pipeline that combines scalable web scraping with AI-powered data processing to create a clean, structured database of eBay marketplace data for analysis and insights.

---

## 🔍 Overview

AIOMarketMaker continuously monitors eBay by scraping search terms stored in Azure Tables, then uses Large Language Models (LLMs) to extract and structure meaningful data from raw eBay listings. The system runs on a scheduled basis to maintain an up-to-date, clean product database that can be used for market analysis, pricing research, and trend identification.

The goal: create a robust ETL pipeline that transforms unstructured eBay marketplace data into structured, analysis-ready datasets.

---

## ✨ Features

### 📦 Automated Data Collection
- Periodically scrapes eBay based on configurable search terms stored in Azure Tables
- Horizontally scalable infrastructure with support for proxies and bot detection bypassing
- Tracks both current listings and historical sold data
- Handles incremental updates and change detection

### 🧠 AI-Powered Data Processing
- Uses LLMs to extract structured data from unstructured eBay listing HTML
- Cleans and normalizes product information (titles, descriptions, specifications)
- Performs entity resolution and deduplication
- Transforms scraped data into a consistent schema for analysis

### 📊 Clean Data Pipeline
- ETL process that maintains data quality and consistency
- Stores processed data in a structured database optimized for queries
- Tracks data lineage and processing metadata
- Supports both batch and incremental processing modes

### 🔄 Scheduled Processing
- Runs on configurable schedules (cron-based)
- Manages search term priorities and rotation
- Monitors processing health and data quality metrics
- Provides logging and alerting for pipeline issues

---

## 🛠 Setup & Usage

Coming soon.

---

## 📄 License

Coming soon.

---

## 🤝 Contributing

Coming soon.
