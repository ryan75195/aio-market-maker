# Real-Time Progress - UI Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Update Electron Desktop UI to call ETL for scrape start and use faster polling for real-time progress updates.

**Architecture:** Add ETL API URL to config, create `etlApiCall()` method, update `startScrape()` to call ETL, change polling interval from 5s to 2s.

**Tech Stack:** Electron 28, Vue.js 3, JavaScript

---

## Phase 1: Add ETL API Configuration

### Task 1.1: Update config.json schema

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/config.json` (template/example)

**Step 1: Add etlApi section**

Add the ETL API configuration alongside existing marketMakerApi:

```json
{
  "marketMakerApi": {
    "baseUrl": "http://localhost:7071/api",
    "functionKey": ""
  },
  "etlApi": {
    "baseUrl": "http://localhost:7072/api"
  },
  "scraperApi": {
    "baseUrl": "http://localhost:7126"
  },
  ...
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/config.json
git commit -m "config: add etlApi URL to config schema"
```

---

### Task 1.2: Update settings data model in app.js

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js`

**Step 1: Add etlApi to settings data**

Find the `settings` object in `data()` (around line 23) and add:

```javascript
settings: {
  marketMakerApi: { baseUrl: '', functionKey: '' },
  etlApi: { baseUrl: '' },  // ADD THIS LINE
  scraperApi: { baseUrl: '' },
  scraping: { maxListingsToFetch: null, defaultLookbackDays: 180 },
  storage: { connectionString: '' },
  openAi: { apiKey: '', model: '' },
  pinecone: { apiKey: '', indexName: '' }
},
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/app.js
git commit -m "feat: add etlApi to settings data model"
```

---

## Phase 2: Add ETL API Call Method

### Task 2.1: Create etlApiCall method

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js`

**Step 1: Add etlApiCall method after apiCall method (around line 104)**

```javascript
async etlApiCall(endpoint, options = {}) {
  const baseUrl = this.config.etlApi?.baseUrl || 'http://localhost:7072/api';

  const url = new URL(`${baseUrl}${endpoint}`);

  const response = await fetch(url.toString(), {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      ...options.headers
    }
  });

  if (!response.ok) {
    const error = await response.json().catch(() => ({ error: response.statusText }));
    throw new Error(error.error || `HTTP ${response.status}`);
  }

  return response.json();
},
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/app.js
git commit -m "feat: add etlApiCall method for ETL API calls"
```

---

## Phase 3: Update startScrape to Call ETL

### Task 3.1: Modify startScrape method

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js`

**Step 1: Update startScrape to use etlApiCall**

Find the `startScrape` method (around line 234) and change from:

```javascript
const data = await this.apiCall('/scrape/start', {
```

to:

```javascript
const data = await this.etlApiCall('/scrape/start', {
```

**Step 2: Update result handling**

The ETL returns `{ runId, instanceId }` instead of just `{ instanceId }`. Update accordingly:

```javascript
async startScrape() {
  this.loading = true;
  try {
    const body = {};
    if (this.settings.scraping?.maxListingsToFetch) {
      body.maxListingsToFetch = this.settings.scraping.maxListingsToFetch;
    }
    if (this.settings.scraping?.defaultLookbackDays) {
      body.lookbackDays = this.settings.scraping.defaultLookbackDays;
    }

    const data = await this.etlApiCall('/scrape/start', {
      method: 'POST',
      body: Object.keys(body).length > 0 ? JSON.stringify(body) : undefined
    });
    const result = this.toCamelCase(data);
    this.lastInstanceId = result.instanceId;
    localStorage.setItem('lastInstanceId', result.instanceId);
    this.showToast(`Scrape started (Run #${result.runId})`, 'success');

    // Switch to history view to see progress
    this.currentView = 'history';
    await this.loadHistory();
  } catch (err) {
    this.showToast(`Failed to start scrape: ${err.message}`, 'error');
  } finally {
    this.loading = false;
  }
},
```

**Step 3: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/app.js
git commit -m "feat: update startScrape to call ETL API"
```

---

## Phase 4: Faster Polling Interval

### Task 4.1: Change polling from 5s to 2s

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js`

**Step 1: Update startAutoRefresh method**

Find the `startAutoRefresh` method (around line 335) and change from:

```javascript
startAutoRefresh() {
  this.refreshInterval = setInterval(() => {
    if (this.currentView === 'history' && this.history.some(r => r.status === 'Running')) {
      this.loadHistory();
    }
  }, 5000);
},
```

to:

```javascript
startAutoRefresh() {
  this.refreshInterval = setInterval(() => {
    if (this.currentView === 'history' && this.history.some(r => r.status === 'Running')) {
      this.loadHistory();
    }
  }, 2000);  // Changed from 5000 to 2000 for faster updates
},
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/app.js
git commit -m "feat: reduce polling interval to 2 seconds for real-time progress"
```

---

## Phase 5: Add ETL Settings UI (Optional)

### Task 5.1: Add ETL API URL to Settings view

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/index.html`

**Step 1: Find the API Connection settings section**

Look for the settings section with `marketMakerApi` inputs.

**Step 2: Add ETL API URL input after Scraper API**

```html
<!-- ETL API -->
<div class="mb-3">
  <label class="form-label small text-muted">ETL API Base URL</label>
  <input type="text" class="form-control form-control-sm"
         v-model="settings.etlApi.baseUrl"
         placeholder="http://localhost:7072/api">
  <div class="form-text">URL for the ETL Functions (scrape orchestration)</div>
</div>
```

**Step 3: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/index.html
git commit -m "feat: add ETL API URL to settings UI"
```

---

## Phase 6: Verification

### Task 6.1: Test the UI changes

**Step 1: Start all services**

```bash
# Terminal 1: Azurite
npx azurite --blobPort 10000 --queuePort 10001 --tablePort 10002

# Terminal 2: API (for history endpoint)
cd AIOMarketMaker/AIOMarketMaker.Functions && func start

# Terminal 3: ETL (for scrape/start)
cd AIOMarketMaker/AIOMarketMaker.Etl && func start --port 7072

# Terminal 4: Desktop
cd AIOMarketMaker/AIOMarketMaker.Desktop && npm run dev
```

**Step 2: Verify in UI**

1. Open Desktop app
2. Go to Settings, verify ETL API URL field exists
3. Set ETL API URL to `http://localhost:7072/api`
4. Go to Operations view
5. Click "Start Scrape"
6. Verify it switches to History view
7. Watch progress bar update every 2 seconds

**Step 3: Commit any fixes**

```bash
git add -A
git commit -m "fix: resolve UI issues for real-time progress"
```

---

## Summary of Changes

| Task | File | Change |
|------|------|--------|
| 1.1 | `config.json` | Add `etlApi.baseUrl` config |
| 1.2 | `app.js` | Add `etlApi` to settings data model |
| 2.1 | `app.js` | Add `etlApiCall()` method |
| 3.1 | `app.js` | Update `startScrape()` to call ETL |
| 4.1 | `app.js` | Change polling interval to 2 seconds |
| 5.1 | `index.html` | Add ETL API URL to settings UI |
| 6.1 | - | Test and verify |
