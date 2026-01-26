# Settings UI Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a Settings page to the Electron UI that allows configuring all app parameters (API endpoints, scraping limits, storage, OpenAI, Pinecone).

**Architecture:** Settings stored in Electron's `config.json`. UI reads/writes via IPC. Scraping params passed to API at runtime via `/scrape/start` endpoint. Other settings (storage, OpenAI, Pinecone) are informational - they show what's configured but don't override Functions config.

**Tech Stack:** Vue.js 3 (CDN), Electron IPC, Node.js fs module, ASP.NET Core (Functions API)

---

### Task 1: Expand config.json Structure

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/config.json`
- Modify: `AIOMarketMaker.Desktop/electron/config.example.json`

**Step 1: Update config.json with full structure**

```json
{
  "marketMakerApi": {
    "baseUrl": "http://localhost:7071/api",
    "functionKey": ""
  },
  "scraperApi": {
    "baseUrl": "http://localhost:7126"
  },
  "scraping": {
    "maxListingsToFetch": 10,
    "defaultLookbackDays": 180
  },
  "storage": {
    "connectionString": "UseDevelopmentStorage=true"
  },
  "openAi": {
    "apiKey": "",
    "model": "gpt-4o-mini"
  },
  "pinecone": {
    "apiKey": "",
    "indexName": "arbitrage"
  }
}
```

**Step 2: Update config.example.json with same structure (empty values)**

**Step 3: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/config.json AIOMarketMaker.Desktop/electron/config.example.json
git commit -m "feat: expand config structure for settings UI"
```

---

### Task 2: Add IPC Handler for Saving Config

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/main.js`
- Modify: `AIOMarketMaker.Desktop/electron/preload.js`

**Step 1: Add saveConfig IPC handler in main.js**

Add after the existing `getConfig` handler:

```javascript
ipcMain.handle('save-config', async (event, newConfig) => {
  try {
    const configPath = path.join(__dirname, 'config.json');
    fs.writeFileSync(configPath, JSON.stringify(newConfig, null, 2));
    return { success: true };
  } catch (err) {
    return { error: err.message };
  }
});
```

**Step 2: Expose saveConfig in preload.js**

Add to the `electronAPI` object:

```javascript
saveConfig: (config) => ipcRenderer.invoke('save-config', config)
```

**Step 3: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/main.js AIOMarketMaker.Desktop/electron/preload.js
git commit -m "feat: add IPC handler for saving config"
```

---

### Task 3: Add Settings Nav Item and View Structure

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/index.html`

**Step 1: Add Settings button to nav**

After the History button in `.nav-items`:

```html
<button :class="{ active: currentView === 'settings' }" @click="currentView = 'settings'">Settings</button>
```

**Step 2: Add Settings view template**

After the History view section, before the Job Form Modal:

```html
<!-- Settings View -->
<div v-if="currentView === 'settings'" class="view">
  <div class="view-header">
    <h1>Settings</h1>
    <button class="btn primary" @click="saveSettings" :disabled="savingSettings">
      {{ savingSettings ? 'Saving...' : 'Save Settings' }}
    </button>
  </div>

  <div class="settings-sections">
    <!-- API Connection -->
    <div class="settings-section">
      <h3 class="section-header" @click="toggleSection('api')">
        <span class="collapse-icon">{{ collapsedSections.api ? '▶' : '▼' }}</span>
        API Connection
      </h3>
      <div class="section-content" v-show="!collapsedSections.api">
        <div class="form-group">
          <label>MarketMaker API URL</label>
          <input v-model="settings.marketMakerApi.baseUrl" placeholder="http://localhost:7071/api">
        </div>
        <div class="form-group">
          <label>Function Key</label>
          <input v-model="settings.marketMakerApi.functionKey" type="password" placeholder="Leave blank for local">
        </div>
        <div class="form-group">
          <label>Scraper API URL</label>
          <input v-model="settings.scraperApi.baseUrl" placeholder="http://localhost:7126">
        </div>
      </div>
    </div>

    <!-- Scraping -->
    <div class="settings-section">
      <h3 class="section-header" @click="toggleSection('scraping')">
        <span class="collapse-icon">{{ collapsedSections.scraping ? '▶' : '▼' }}</span>
        Scraping
      </h3>
      <div class="section-content" v-show="!collapsedSections.scraping">
        <div class="form-group">
          <label>Max Listings to Fetch <span class="hint">(blank = unlimited)</span></label>
          <input v-model.number="settings.scraping.maxListingsToFetch" type="number" placeholder="10">
        </div>
        <div class="form-group">
          <label>Default Lookback Days</label>
          <input v-model.number="settings.scraping.defaultLookbackDays" type="number" placeholder="180">
        </div>
      </div>
    </div>

    <!-- Storage -->
    <div class="settings-section">
      <h3 class="section-header" @click="toggleSection('storage')">
        <span class="collapse-icon">{{ collapsedSections.storage ? '▶' : '▼' }}</span>
        Storage
      </h3>
      <div class="section-content" v-show="!collapsedSections.storage">
        <div class="form-group">
          <label>Connection String</label>
          <input v-model="settings.storage.connectionString" placeholder="UseDevelopmentStorage=true">
        </div>
      </div>
    </div>

    <!-- OpenAI -->
    <div class="settings-section">
      <h3 class="section-header" @click="toggleSection('openAi')">
        <span class="collapse-icon">{{ collapsedSections.openAi ? '▶' : '▼' }}</span>
        OpenAI
      </h3>
      <div class="section-content" v-show="!collapsedSections.openAi">
        <div class="form-group">
          <label>API Key</label>
          <input v-model="settings.openAi.apiKey" type="password" placeholder="sk-...">
        </div>
        <div class="form-group">
          <label>Model</label>
          <input v-model="settings.openAi.model" placeholder="gpt-4o-mini">
        </div>
      </div>
    </div>

    <!-- Pinecone -->
    <div class="settings-section">
      <h3 class="section-header" @click="toggleSection('pinecone')">
        <span class="collapse-icon">{{ collapsedSections.pinecone ? '▶' : '▼' }}</span>
        Pinecone
      </h3>
      <div class="section-content" v-show="!collapsedSections.pinecone">
        <div class="form-group">
          <label>API Key</label>
          <input v-model="settings.pinecone.apiKey" type="password" placeholder="pcsk_...">
        </div>
        <div class="form-group">
          <label>Index Name</label>
          <input v-model="settings.pinecone.indexName" placeholder="arbitrage">
        </div>
      </div>
    </div>
  </div>
</div>
```

**Step 3: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/index.html
git commit -m "feat: add settings view HTML structure"
```

---

### Task 4: Add Settings JavaScript Logic

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js`

**Step 1: Add settings-related data properties**

In the `data()` return object, add:

```javascript
settings: {
  marketMakerApi: { baseUrl: '', functionKey: '' },
  scraperApi: { baseUrl: '' },
  scraping: { maxListingsToFetch: null, defaultLookbackDays: 180 },
  storage: { connectionString: '' },
  openAi: { apiKey: '', model: '' },
  pinecone: { apiKey: '', indexName: '' }
},
collapsedSections: {
  api: false,
  scraping: false,
  storage: true,
  openAi: true,
  pinecone: true
},
savingSettings: false
```

**Step 2: Update loadConfig to populate settings**

Modify the `loadConfig` method to also populate settings:

```javascript
async loadConfig() {
  try {
    const result = await window.electronAPI.getConfig();
    if (result.error) {
      this.configError = result.error;
      this.showToast(result.error, 'error');
    } else {
      this.config = result;
      // Populate settings from config
      this.settings = JSON.parse(JSON.stringify(result)); // Deep copy
    }
  } catch (err) {
    this.configError = err.message;
    this.showToast('Failed to load config', 'error');
  }
}
```

**Step 3: Add toggleSection method**

```javascript
toggleSection(section) {
  this.collapsedSections[section] = !this.collapsedSections[section];
}
```

**Step 4: Add saveSettings method**

```javascript
async saveSettings() {
  this.savingSettings = true;
  try {
    const result = await window.electronAPI.saveConfig(this.settings);
    if (result.error) {
      this.showToast(`Failed to save: ${result.error}`, 'error');
    } else {
      this.config = JSON.parse(JSON.stringify(this.settings)); // Update active config
      this.showToast('Settings saved', 'success');
    }
  } catch (err) {
    this.showToast(`Failed to save: ${err.message}`, 'error');
  } finally {
    this.savingSettings = false;
  }
}
```

**Step 5: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/app.js
git commit -m "feat: add settings load/save logic"
```

---

### Task 5: Add Settings CSS Styles

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/styles.css`

**Step 1: Add settings section styles**

```css
/* Settings */
.settings-sections {
  display: flex;
  flex-direction: column;
  gap: 15px;
}

.settings-section {
  background: #252526;
  border-radius: 8px;
  border: 1px solid #3c3c3c;
  overflow: hidden;
}

.section-header {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 15px 20px;
  margin: 0;
  font-size: 14px;
  font-weight: 500;
  cursor: pointer;
  background: #2d2d2d;
  user-select: none;
}

.section-header:hover {
  background: #333;
}

.collapse-icon {
  font-size: 10px;
  color: #808080;
}

.section-content {
  padding: 15px 20px;
}

.section-content .form-group {
  margin-bottom: 12px;
}

.section-content .form-group:last-child {
  margin-bottom: 0;
}

.hint {
  color: #808080;
  font-size: 12px;
  font-weight: normal;
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/styles.css
git commit -m "feat: add settings UI styles"
```

---

### Task 6: Update Start Scrape to Pass Config Overrides

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js`

**Step 1: Update startScrape method to include settings**

Replace the existing `startScrape` method:

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

    const data = await this.apiCall('/scrape/start', {
      method: 'POST',
      body: Object.keys(body).length > 0 ? JSON.stringify(body) : undefined
    });
    const result = this.toCamelCase(data);
    this.lastInstanceId = result.instanceId;
    localStorage.setItem('lastInstanceId', result.instanceId);
    this.showToast(`Scrape started: ${result.instanceId}`, 'success');
  } catch (err) {
    this.showToast(`Failed to start scrape: ${err.message}`, 'error');
  } finally {
    this.loading = false;
  }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/app.js
git commit -m "feat: pass scraping config to start scrape API"
```

---

### Task 7: Update Functions API to Accept Scraping Overrides

**Files:**
- Modify: `AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs`

**Step 1: Add request record for StartScrape**

At the bottom of the file with other records:

```csharp
public record StartScrapeRequest(int? MaxListingsToFetch, int? LookbackDays);
```

**Step 2: Find and update the TriggerManualScrapeHttp function**

Modify to read the request body and store overrides in orchestration input.

First, read the ScrapeJobsApi.cs to find the exact implementation.

**Step 3: Commit**

```bash
git add AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs
git commit -m "feat: accept scraping overrides in start scrape API"
```

---

### Task 8: Update Orchestrators to Use Runtime Overrides

**Files:**
- Modify: `AIOMarketMaker.Functions/Contracts/OrchestratorContracts.cs`
- Modify: `AIOMarketMaker.Functions/Functions/Orchestrators/ScrapeOrchestrator.cs`
- Modify: `AIOMarketMaker.Functions/Activities/GetJobDetailsActivity.cs`

**Step 1: Add ScrapeOrchestratorInput record**

```csharp
public record ScrapeOrchestratorInput(int? MaxListingsToFetch, int? LookbackDays);
```

**Step 2: Update ScrapeOrchestrator to read and pass overrides**

**Step 3: Update GetJobDetailsActivity or JobOrchestrator to use overrides**

**Step 4: Commit**

```bash
git add AIOMarketMaker.Functions/Contracts/OrchestratorContracts.cs
git add AIOMarketMaker.Functions/Functions/Orchestrators/ScrapeOrchestrator.cs
git add AIOMarketMaker.Functions/Activities/GetJobDetailsActivity.cs
git commit -m "feat: use runtime scraping overrides in orchestrators"
```

---

### Task 9: Test End-to-End

**Step 1: Restart Electron app**

```bash
cd AIOMarketMaker.Desktop/electron
npm start
```

**Step 2: Navigate to Settings**

- Verify all sections display
- Verify values loaded from config.json
- Toggle sections collapse/expand

**Step 3: Modify and save settings**

- Change MaxListingsToFetch to 5
- Click Save
- Verify toast shows success
- Restart app, verify value persisted

**Step 4: Test scrape with overrides**

- Set MaxListingsToFetch to 3 in Settings
- Start a scrape
- Verify Functions logs show "Limiting to 3 listings"

**Step 5: Final commit**

```bash
git add -A
git commit -m "feat: complete settings UI implementation"
```

---

## Summary

| Task | Description | Files |
|------|-------------|-------|
| 1 | Expand config structure | config.json |
| 2 | IPC save handler | main.js, preload.js |
| 3 | Settings HTML template | index.html |
| 4 | Settings JS logic | app.js |
| 5 | Settings CSS | styles.css |
| 6 | Update startScrape | app.js |
| 7 | API accept overrides | ScrapeJobsApi.cs |
| 8 | Orchestrators use overrides | Contracts, Orchestrators |
| 9 | End-to-end testing | - |
