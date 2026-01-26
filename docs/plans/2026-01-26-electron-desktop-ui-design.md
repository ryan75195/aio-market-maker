# Electron Desktop UI Design

## Overview

Minimal admin console for AIOMarketMaker using Electron + Vue. Provides job management and operations control with configurable API endpoints for local/production switching.

## Architecture

```
AIOMarketMaker.Desktop/
├── package.json           # Electron + Vue dependencies
├── config.json            # API endpoints (gitignored)
├── config.example.json    # Template for config
├── main.js                # Electron main process
├── preload.js             # Bridge between Electron and renderer
├── src/
│   ├── App.vue            # Root component with navigation
│   ├── main.js            # Vue entry point
│   ├── api/
│   │   └── client.js      # HTTP client using config.json
│   ├── views/
│   │   ├── JobsView.vue       # CRUD for scrape jobs
│   │   └── OperationsView.vue # Start/stop scrapes, view status
│   └── components/
│       ├── JobList.vue        # Table of jobs with enable/disable
│       ├── JobForm.vue        # Create/edit job modal
│       └── OrchestrationStatus.vue  # Running scrape status
```

## Config File

`config.json` (gitignored):
```json
{
  "marketMakerApi": {
    "baseUrl": "https://YOUR-FUNCTION-APP.azurewebsites.net/api",
    "functionKey": "your-function-key-here"
  },
  "webScraperApi": {
    "baseUrl": "http://localhost:7126/api"
  }
}
```

## UI Views

### Jobs View (default)
- Table: ID, Search Term, Filter Instructions, Enabled toggle, Last Run
- New Job button opens modal form
- Row actions: Edit, Delete, Enable/Disable
- API: `GET/POST /jobs`, `PUT/DELETE /jobs/{id}`

### Operations View
- Start Scrape button (`POST /scrape/start`)
- Purge All button with confirmation (`POST /orchestration/purge`)
- Shows last triggered instanceId
- Terminate button (`DELETE /orchestration/{instanceId}`)

### Navigation
- Left sidebar: Jobs, Operations
- Header shows current target (Production/Local)

## Technical Details

### API Client
- Reads config via Electron preload bridge
- Appends `?code={functionKey}` to MarketMaker requests
- Simple fetch wrapper, no external HTTP library

### Preload Bridge
- `window.electronAPI.getConfig()` - reads config.json
- `window.electronAPI.getConfigPath()` - shows config location
- Uses `contextBridge` for security

### Error Handling
- Toast notifications for API errors
- Network errors: "Cannot connect to {baseUrl}"
- Auth errors: "Check your function key in config.json"

## Running

```bash
cd AIOMarketMaker/AIOMarketMaker.Desktop
npm install
cp config.example.json config.json  # Edit with keys
npm run dev   # Development
npm run build # Package (optional)
```

## Styling
- Dark theme, minimal CSS
- No CSS framework
