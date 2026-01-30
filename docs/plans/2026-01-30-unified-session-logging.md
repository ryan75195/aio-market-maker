# Unified Session Logging Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add file-based logging to all components so each dev session captures logs to a shared folder for debugging.

**Architecture:** Each component writes structured JSON logs to a session folder (`logs/session-{timestamp}/`). The session path is passed via `LOG_SESSION_PATH` environment variable, set by `/setup-local-env`. Docker workers mount the folder as a volume.

**Tech Stack:** Serilog.Sinks.File, structured JSON logging, environment variable configuration

---

## Task 1: Add Serilog.Sinks.File to AIOMarketMaker.Etl

**Files:**
- Modify: `AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
- Modify: `AIOMarketMaker.Etl/Program.cs`

**Step 1: Add package reference**

```bash
cd <REPO_ROOT>/AIOMarketMaker.Etl
dotnet add package Serilog.Sinks.File
```

**Step 2: Verify package added**

Run: `grep -i "serilog.sinks.file" AIOMarketMaker.Etl.csproj`
Expected: `<PackageReference Include="Serilog.Sinks.File"`

**Step 3: Update Program.cs to add file sink**

In `AIOMarketMaker.Etl/Program.cs`, find the Serilog configuration and update:

```csharp
var logSessionPath = Environment.GetEnvironmentVariable("LOG_SESSION_PATH");

var loggerConfig = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Component", "AIOMarketMaker.Etl")
    .WriteTo.Console();

if (!string.IsNullOrEmpty(logSessionPath))
{
    var logFile = Path.Combine(logSessionPath, "etl-.json");
    loggerConfig.WriteTo.File(
        new Serilog.Formatting.Compact.CompactJsonFormatter(),
        logFile,
        rollingInterval: RollingInterval.Hour,
        retainedFileCountLimit: null);
}

Log.Logger = loggerConfig.CreateLogger();
```

**Step 4: Add CompactJsonFormatter package**

```bash
dotnet add package Serilog.Formatting.Compact
```

**Step 5: Build to verify**

Run: `dotnet build AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build succeeded

**Step 6: Commit**

```bash
git add AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj AIOMarketMaker.Etl/Program.cs
git commit -m "feat(etl): add file logging with session path support"
```

---

## Task 2: Add Serilog.Sinks.File to AIOMarketMaker.Functions

**Files:**
- Modify: `AIOMarketMaker.Functions/AIOMarketMaker.Functions.csproj`
- Modify: `AIOMarketMaker.Functions/Program.cs`

**Step 1: Add packages**

```bash
cd <REPO_ROOT>/AIOMarketMaker.Functions
dotnet add package Serilog
dotnet add package Serilog.Sinks.File
dotnet add package Serilog.Formatting.Compact
dotnet add package Serilog.Extensions.Hosting
```

**Step 2: Update Program.cs**

Add Serilog configuration before `builder.Build()`:

```csharp
var logSessionPath = Environment.GetEnvironmentVariable("LOG_SESSION_PATH");

var loggerConfig = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Component", "AIOMarketMaker.Functions")
    .WriteTo.Console();

if (!string.IsNullOrEmpty(logSessionPath))
{
    var logFile = Path.Combine(logSessionPath, "functions-.json");
    loggerConfig.WriteTo.File(
        new Serilog.Formatting.Compact.CompactJsonFormatter(),
        logFile,
        rollingInterval: RollingInterval.Hour,
        retainedFileCountLimit: null);
}

Log.Logger = loggerConfig.CreateLogger();
builder.Services.AddLogging(lb => lb.AddSerilog(Log.Logger));
```

Add using statements:
```csharp
using Serilog;
using Serilog.Formatting.Compact;
```

**Step 3: Build to verify**

Run: `dotnet build AIOMarketMaker.Functions/AIOMarketMaker.Functions.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add AIOMarketMaker.Functions/AIOMarketMaker.Functions.csproj AIOMarketMaker.Functions/Program.cs
git commit -m "feat(functions): add file logging with session path support"
```

---

## Task 3: Add Serilog.Sinks.File to AIOWebScraper API

**Files:**
- Modify: `../AIOWebScraper/AIOWebScraper/AIOWebScraper.csproj`
- Modify: `../AIOWebScraper/AIOWebScraper/Program.cs`

**Step 1: Add packages**

```bash
cd <EXTERNAL_SCRAPER_REPO>/AIOWebScraper
dotnet add package Serilog.Sinks.File
dotnet add package Serilog.Formatting.Compact
```

**Step 2: Update Program.cs**

Add file sink configuration:

```csharp
var logSessionPath = Environment.GetEnvironmentVariable("LOG_SESSION_PATH");

var loggerConfig = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Component", "AIOWebScraper.Api")
    .WriteTo.Console();

if (!string.IsNullOrEmpty(logSessionPath))
{
    var logFile = Path.Combine(logSessionPath, "scraper-api-.json");
    loggerConfig.WriteTo.File(
        new Serilog.Formatting.Compact.CompactJsonFormatter(),
        logFile,
        rollingInterval: RollingInterval.Hour,
        retainedFileCountLimit: null);
}

Log.Logger = loggerConfig.CreateLogger();
builder.Services.AddLogging(lb => lb.AddSerilog(Log.Logger));
```

**Step 3: Build to verify**

Run: `dotnet build ../AIOWebScraper/AIOWebScraper/AIOWebScraper.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
cd <EXTERNAL_SCRAPER_REPO>
git add AIOWebScraper/AIOWebScraper.csproj AIOWebScraper/Program.cs
git commit -m "feat(api): add file logging with session path support"
```

---

## Task 4: Update ScraperWorker Logging (Both Modes)

**Files:**
- Modify: `../AIOWebScraper/ScraperWorker/ScraperWorker.csproj`
- Modify: `../AIOWebScraper/ScraperWorker/DedicatedStartup.cs`
- Modify: `../AIOWebScraper/ScraperWorker/SimpleQueueModeStartup.cs`

**Step 1: Add packages (if not already present)**

```bash
cd <EXTERNAL_SCRAPER_REPO>/ScraperWorker
dotnet add package Serilog.Sinks.File
dotnet add package Serilog.Formatting.Compact
```

**Step 2: Update DedicatedStartup.cs**

Find the Serilog configuration and add file sink:

```csharp
var logSessionPath = Environment.GetEnvironmentVariable("LOG_SESSION_PATH");

var loggerConfig = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Component", "ScraperWorker.Dedicated")
    .WriteTo.Console();

if (!string.IsNullOrEmpty(logSessionPath))
{
    var logFile = Path.Combine(logSessionPath, "worker-dedicated-.json");
    loggerConfig.WriteTo.File(
        new Serilog.Formatting.Compact.CompactJsonFormatter(),
        logFile,
        rollingInterval: RollingInterval.Hour,
        retainedFileCountLimit: null);
}
```

**Step 3: Update SimpleQueueModeStartup.cs**

Same pattern, but with worker ID for multi-container:

```csharp
var logSessionPath = Environment.GetEnvironmentVariable("LOG_SESSION_PATH");
var workerId = Environment.GetEnvironmentVariable("WORKER_ID") ?? "0";

var loggerConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Component", "ScraperWorker.Queue")
    .Enrich.WithProperty("WorkerId", workerId)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

if (!string.IsNullOrEmpty(logSessionPath))
{
    var logFile = Path.Combine(logSessionPath, $"worker-{workerId}-.json");
    loggerConfig.WriteTo.File(
        new Serilog.Formatting.Compact.CompactJsonFormatter(),
        logFile,
        rollingInterval: RollingInterval.Hour,
        retainedFileCountLimit: null);
}
```

**Step 4: Build to verify**

Run: `dotnet build ../AIOWebScraper/ScraperWorker/ScraperWorker.csproj`
Expected: Build succeeded

**Step 5: Commit**

```bash
cd <EXTERNAL_SCRAPER_REPO>
git add ScraperWorker/ScraperWorker.csproj ScraperWorker/DedicatedStartup.cs ScraperWorker/SimpleQueueModeStartup.cs
git commit -m "feat(worker): add file logging with session path and worker ID"
```

---

## Task 5: Update /setup-local-env to Create Session Folder and Set Environment Variable

**Files:**
- Modify: The setup-local-env skill/script (location TBD - check Claude skills or scripts folder)

**Step 1: Find the setup-local-env implementation**

```bash
find <USER_HOME> -name "*setup-local-env*" -type f 2>/dev/null | head -5
```

**Step 2: Add session folder creation**

At the start of the script, add:

```bash
# Create session log folder
SESSION_TIMESTAMP=$(date +%Y-%m-%d-%H-%M-%S)
LOG_SESSION_PATH="<LOGS_DIR>/session-$SESSION_TIMESTAMP"
mkdir -p "$LOG_SESSION_PATH"
export LOG_SESSION_PATH
echo "Log session: $LOG_SESSION_PATH"
```

**Step 3: Pass to func start processes**

When starting Azure Functions:
```bash
LOG_SESSION_PATH="$LOG_SESSION_PATH" func start --port 7072
```

**Step 4: Pass to Docker workers via volume mount and env var**

Update docker run commands:
```bash
docker run -d \
  -e LOG_SESSION_PATH=/logs \
  -e WORKER_ID=$i \
  -v "$LOG_SESSION_PATH:/logs" \
  ... rest of docker args
```

**Step 5: Test manually**

```bash
# Create test session folder
mkdir -p <LOGS_DIR>/test-session
export LOG_SESSION_PATH="<LOGS_DIR>/test-session"

# Start ETL and verify log file appears
cd <REPO_ROOT>/AIOMarketMaker.Etl
func start --port 7072
# Check: ls <LOGS_DIR>/test-session/
# Expected: etl-*.json file appears
```

**Step 6: Commit**

```bash
git add <setup-local-env files>
git commit -m "feat(setup): create session log folder and pass LOG_SESSION_PATH"
```

---

## Task 6: Add .gitignore Entry for Logs

**Files:**
- Modify: `<REPOS_DIR>/.gitignore` or individual repo .gitignore files

**Step 1: Add logs folder to .gitignore**

```bash
echo "logs/" >> <REPO_ROOT>/.gitignore
echo "logs/" >> <EXTERNAL_SCRAPER_REPO>/.gitignore
```

**Step 2: Commit**

```bash
cd <REPO_ROOT>
git add .gitignore
git commit -m "chore: ignore logs folder"

cd <EXTERNAL_SCRAPER_REPO>
git add .gitignore
git commit -m "chore: ignore logs folder"
```

---

## Verification Checklist

After all tasks complete:

- [ ] `LOG_SESSION_PATH` environment variable is set by setup-local-env
- [ ] Session folder created at `logs/session-{timestamp}/`
- [ ] AIOMarketMaker.Etl writes to `etl-*.json`
- [ ] AIOMarketMaker.Functions writes to `functions-*.json`
- [ ] AIOWebScraper API writes to `scraper-api-*.json`
- [ ] ScraperWorker (Dedicated) writes to `worker-dedicated-*.json`
- [ ] ScraperWorker (Queue) writes to `worker-{id}-*.json` per container
- [ ] Docker containers can write to mounted volume
- [ ] Logs are structured JSON (can be parsed with jq)
- [ ] logs/ folder is gitignored

---

## Usage After Implementation

```bash
# Start environment
/setup-local-env start --workers 15
# Output: Log session: <LOGS_DIR>/session-2026-01-30-14-30-00

# Run a scrape, then check logs
ls logs/session-2026-01-30-14-30-00/
# etl-20260130.json
# functions-20260130.json
# scraper-api-20260130.json
# worker-0-20260130.json
# worker-1-20260130.json
# ...

# Query logs for a specific ScrapeRunId
cat logs/session-*/etl-*.json | jq 'select(.ScrapeRunId == 18050)'

# Find errors across all components
cat logs/session-*/*.json | jq 'select(.Level == "Error")'
```
