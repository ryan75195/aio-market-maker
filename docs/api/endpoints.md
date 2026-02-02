# API Reference

Base URL: `http://localhost:7071/api`

## Jobs

### GET /jobs
List all scrape jobs.

**Response:**
```json
[
  {
    "id": 1003,
    "searchTerm": "PlayStation 5",
    "isEnabled": true,
    "createdUtc": "2026-01-15T10:00:00Z"
  }
]
```

### POST /jobs
Create a new scrape job.

**Request:**
```json
{
  "searchTerm": "Nintendo Switch"
}
```

### PUT /jobs/{id}
Update a job (enable/disable).

**Request:**
```json
{
  "isEnabled": false
}
```

### DELETE /jobs/{id}
Delete a scrape job.

---

## Scrape Runs

### GET /history
List recent scrape runs with status and progress.

**Response:**
```json
[
  {
    "id": 19180,
    "jobId": 1003,
    "jobSearchTerm": "PlayStation 5",
    "status": "Completed",
    "currentPhase": "Completed",
    "totalListingsFound": 1067,
    "listingsProcessed": 1067,
    "listingsAddedActive": 26,
    "listingsAddedSold": 8,
    "listingsUpdated": 1025,
    "listingsFailed": 8,
    "startedUtc": "2026-02-01T21:07:44Z",
    "completedUtc": "2026-02-01T21:16:30Z"
  }
]
```

### POST /scrape/start
Start a manual scrape for all enabled jobs.

**Response:**
```json
{
  "message": "Started 2 scrape runs",
  "runIds": [19181, 19182]
}
```

---

## Listings

### GET /listings/active
List active (buyable) listings. Limited to 100 most recent.

**Response:**
```json
[
  {
    "id": 33390,
    "listingId": "137004607046",
    "title": "PlayStation 5 Console",
    "price": 450.00,
    "currency": "GBP",
    "shippingCost": 0,
    "condition": "Used",
    "listingStatus": "Active",
    "url": "https://www.ebay.co.uk/itm/137004607046",
    "createdUtc": "2026-02-01T20:51:44Z"
  }
]
```

### GET /listings/issues
List listings with parse failures or retrying status.

---

## Settings

### GET /settings
Get current scraping settings.

**Response:**
```json
{
  "maxListingsToFetch": 100,
  "defaultLookbackDays": 180
}
```

### PUT /settings
Update scraping settings.

---

## Data Management

### DELETE /data/all
Clear all listings, runs, and history. **Destructive.**

### DELETE /listings/all
Clear all listings. **Destructive.**

### DELETE /history/all
Clear all scrape run history. **Destructive.**

---

## ETL Endpoint

Base URL: `http://localhost:7072/api`

### POST /process-listing
Process a scraped listing (called by workers).

**Request:**
```json
{
  "listingId": "137004607046",
  "scrapeRunId": 19180,
  "scrapeJobId": 1003,
  "htmlBlobPath": "19180/137004607046/listing.html"
}
```

**Response:**
```json
{
  "success": true,
  "status": "added",
  "error": null
}
```

Status values: `added`, `updated`, `skipped`, `failed`
