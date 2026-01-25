# Azurite Local Testing Infrastructure

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enable E2E tests to run locally using Azurite (Azure Storage emulator) with the same code paths as production.

**Architecture:** Use Azurite to emulate Azure Table Storage, Queue Storage, and Blob Storage. Run the Azure Functions API and ScraperWorker (in simple queue mode) pointing to Azurite. E2E tests call the same API endpoints as production.

**Tech Stack:** Azurite (Docker), Azure Functions, ScraperWorker, NUnit, existing Azure SDK code

---

## Cleanup from Previous Implementation Attempt

During this session, we created some files that took a different approach (separate endpoint for testing). These need to be removed:

**DELETE:**
- `AIOMarketMaker.Tests/E2E/DedicatedModeWebscraperClient.cs` - Bypassed production code paths

**UPDATE (revert to production patterns):**
- `AIOMarketMaker.Tests/E2E/E2ETestFixture.cs` - Currently uses `DedicatedModeWebscraperClient` and mocked `IJobRepository`

**KEEP (still valid):**
- `AIOMarketMaker.Tests/E2E/MockEbayServer.cs` - Serves HTML snapshots for deterministic testing
- `AIOMarketMaker.Tests/E2E/MockEbayServer_Tests.cs` - Unit tests for mock server
- `AIOMarketMaker.Tests/E2E/TestableEbayUrlBuilder.cs` - Redirects eBay URLs to MockEbayServer
- `AIOMarketMaker.Tests/E2E/TestableEbayUrlBuilder_Tests.cs` - Unit tests
- `AIOMarketMaker.Tests/E2E/ScrapePipeline_E2ETests.cs` - The actual E2E tests
- `AIOMarketMaker.Tests/E2E/EbayContract_E2ETests.cs` - Contract tests for real eBay

---

## Current State

- ScraperWorker **already has queue processing** (Simple Queue Mode is default)
- Azure SDK **already supports Azurite** via `UseDevelopmentStorage=true`
- Just need to configure connection strings and set up test infrastructure

## Target Architecture

```
┌─ E2E Test ────────────────────────────────────────────────────┐
│  1. Start Azurite (Docker)                                    │
│  2. Start Azure Functions API (port 7071, points to Azurite)  │
│  3. Start ScraperWorker (queue mode, points to Azurite)       │
│  4. Start MockEbayServer (port 9999)                          │
│  5. Run tests via WebscraperClient → API → Queue → Worker     │
└───────────────────────────────────────────────────────────────┘

Same code paths as production:
  WebscraperClient
    → POST /api/NewJob
    → Job created in Table Storage (Azurite)
    → Message enqueued to Queue (Azurite)
    → ScraperWorker dequeues message
    → ScraperWorker fetches HTML from MockEbayServer
    → HTML saved to Blob Storage (Azurite)
    → Client polls /api/GetStatus
    → Client fetches results via /api/GetResults
```

---

## Tasks

### Task 1: Create Local Development Settings Files

**Files:**
- Create: `AIOWebScraper/AIOWebScraper/local.settings.local.json`
- Create: `AIOWebScraper/ScraperWorker/appsettings.local.json`

**Step 1: Create local.settings.local.json for Azure Functions**

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "tableStorageConnectionString": "UseDevelopmentStorage=true",
    "blobStorageConnectionString": "UseDevelopmentStorage=true",
    "QueueStorageConnectionString": "UseDevelopmentStorage=true"
  }
}
```

**Step 2: Create appsettings.local.json for ScraperWorker**

Note: ScraperWorker uses `blobStorageKey` (not `blobStorageConnectionString`) - this is intentional.

```json
{
  "tableStorageConnectionString": "UseDevelopmentStorage=true",
  "blobStorageKey": "UseDevelopmentStorage=true",
  "queueStorageConnectionString": "UseDevelopmentStorage=true",
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  }
}
```

**Step 3: Create local.settings.local.json for AIOMarketMaker.Functions**

Note: Update `ScraperApi__BaseUrl` to point to Azure Functions (port 7071), not dedicated mode (7126).

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "StorageConnectionString": "UseDevelopmentStorage=true",
    "ScraperApi__BaseUrl": "http://localhost:7071",
    "ScraperApi__ApiKey": ""
  }
}
```

**Step 4: Update .gitignore to exclude local settings (SECURITY)**

Real Azure credentials are exposed in committed files. Add to both `.gitignore` files:

AIOWebScraper/.gitignore:
```
local.settings.json
local.settings.local.json
appsettings.local.json
```

AIOMarketMaker/.gitignore:
```
local.settings.json
local.settings.local.json
```

**Step 5: Commit**

```bash
git add AIOWebScraper/AIOWebScraper/local.settings.local.json
git add AIOWebScraper/ScraperWorker/appsettings.local.json
git add AIOMarketMaker/AIOMarketMaker.Functions/local.settings.local.json
git add AIOWebScraper/.gitignore
git add AIOMarketMaker/.gitignore
git commit -m "feat: add local development settings for Azurite"
```

---

### Task 2: Update ScraperWorker to Load Local Settings

**Files:**
- Modify: `AIOWebScraper/ScraperWorker/Program.cs`

**Step 1: Check current configuration loading**

Review how `Program.cs` loads configuration. It should load `appsettings.json` by default.

**Step 2: Add optional appsettings.local.json loading**

In `Program.cs`, ensure configuration loads local overrides when present:

```csharp
var builder = Host.CreateDefaultBuilder(args);
builder.ConfigureAppConfiguration((context, config) =>
{
    config.AddJsonFile("appsettings.json", optional: false)
          .AddJsonFile("appsettings.local.json", optional: true)  // Add this
          .AddEnvironmentVariables();
});
```

**Step 3: Test configuration loads correctly**

Run: `cd AIOWebScraper/ScraperWorker && dotnet run -- --help`

Expected: Application starts without connection string errors (when Azurite settings present).

**Step 4: Commit**

```bash
git add AIOWebScraper/ScraperWorker/Program.cs
git commit -m "feat: ScraperWorker loads appsettings.local.json when present"
```

---

### Task 3: Create E2E Test Infrastructure Helper

**Files:**
- Create: `AIOMarketMaker.Tests/E2E/LocalTestInfrastructure.cs`

**Step 1: Write the infrastructure helper class**

```csharp
using System.Diagnostics;
using System.Net.Sockets;

namespace AIOMarketMaker.Tests.E2E;

/// <summary>
/// Manages local test infrastructure: Azurite, Azure Functions API, ScraperWorker.
/// </summary>
public class LocalTestInfrastructure : IDisposable
{
    private Process? _azuriteProcess;
    private Process? _functionsProcess;
    private Process? _workerProcess;

    public const int AzuritePort = 10000;      // Blob
    public const int AzuriteQueuePort = 10001; // Queue
    public const int AzuriteTablePort = 10002; // Table
    public const int FunctionsPort = 7071;
    public const int WorkerPort = 5000;        // Not used in queue mode, but reserved

    /// <summary>
    /// Starts Azurite using Docker.
    /// </summary>
    public async Task StartAzuriteAsync()
    {
        if (IsPortInUse(AzuritePort))
        {
            Console.WriteLine("Azurite already running on port 10000");
            return;
        }

        _azuriteProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = "run --rm -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        // Wait for Azurite to be ready
        await WaitForPortAsync(AzuritePort, TimeSpan.FromSeconds(30));
        Console.WriteLine("Azurite started");
    }

    /// <summary>
    /// Starts Azure Functions API pointing to Azurite.
    /// </summary>
    public async Task StartFunctionsApiAsync(string projectPath)
    {
        if (IsPortInUse(FunctionsPort))
        {
            Console.WriteLine("Functions API already running on port 7071");
            return;
        }

        var settingsPath = Path.Combine(projectPath, "local.settings.local.json");
        if (!File.Exists(settingsPath))
        {
            throw new FileNotFoundException($"Local settings not found: {settingsPath}");
        }

        // Copy local settings to local.settings.json for func to pick up
        var targetPath = Path.Combine(projectPath, "local.settings.json");
        File.Copy(settingsPath, targetPath, overwrite: true);

        _functionsProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "func",
            Arguments = "start",
            WorkingDirectory = projectPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        await WaitForPortAsync(FunctionsPort, TimeSpan.FromSeconds(60));
        Console.WriteLine("Azure Functions API started");
    }

    /// <summary>
    /// Starts ScraperWorker in simple queue mode pointing to Azurite.
    /// </summary>
    public async Task StartWorkerAsync(string projectPath)
    {
        _workerProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "run",
            WorkingDirectory = projectPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment =
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Local"
            }
        });

        // Give worker time to start and connect to queue
        await Task.Delay(5000);
        Console.WriteLine("ScraperWorker started in queue mode");
    }

    public void Dispose()
    {
        StopProcess(_workerProcess);
        StopProcess(_functionsProcess);
        StopProcess(_azuriteProcess);
    }

    private static void StopProcess(Process? process)
    {
        if (process != null && !process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            process.Dispose();
        }
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("localhost", port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitForPortAsync(int port, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (IsPortInUse(port))
                return;
            await Task.Delay(500);
        }
        throw new TimeoutException($"Port {port} not available after {timeout.TotalSeconds}s");
    }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Tests/E2E/LocalTestInfrastructure.cs
git commit -m "feat: add LocalTestInfrastructure helper for E2E tests"
```

---

### Task 4: Update E2ETestFixture to Use Full Infrastructure

**Files:**
- Modify: `AIOMarketMaker.Tests/E2E/E2ETestFixture.cs`
- Delete: `AIOMarketMaker.Tests/E2E/DedicatedModeWebscraperClient.cs`

**Step 1: Update E2ETestFixture to use production code paths**

```csharp
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScraperWorker.Services;
using System.Net.Sockets;

namespace AIOMarketMaker.Tests.E2E;

public abstract class E2ETestFixture
{
    protected static MockEbayServer? MockServer;
    protected static LocalTestInfrastructure? Infrastructure;
    protected EtlDbContext DbContext = null!;
    protected IEbayScraper EbayScraper = null!;
    protected SqliteConnection Connection = null!;

    private const string FunctionsApiUrl = "http://localhost:7071/";
    private const int MockEbayPort = 9999;

    private static readonly string WebScraperPath =
        Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "..", "AIOWebScraper"));

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // Start mock eBay server
        MockServer = new MockEbayServer(MockEbayPort);
        MockServer.Start();

        // Check if infrastructure is already running (for local dev)
        if (!IsInfrastructureRunning())
        {
            // For CI or fresh runs, start infrastructure
            Infrastructure = new LocalTestInfrastructure();

            try
            {
                await Infrastructure.StartAzuriteAsync();
                await Infrastructure.StartFunctionsApiAsync(
                    Path.Combine(WebScraperPath, "AIOWebScraper"));
                await Infrastructure.StartWorkerAsync(
                    Path.Combine(WebScraperPath, "ScraperWorker"));
            }
            catch (Exception ex)
            {
                Infrastructure?.Dispose();
                Assert.Ignore($"Could not start test infrastructure: {ex.Message}. " +
                    "Run manually: docker run -p 10000-10002:10000-10002 mcr.microsoft.com/azure-storage/azurite");
            }
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        MockServer?.Dispose();
        Infrastructure?.Dispose();
    }

    [SetUp]
    public async Task SetUp()
    {
        // Create in-memory SQLite database
        Connection = new SqliteConnection("Data Source=:memory:");
        await Connection.OpenAsync();

        var options = new DbContextOptionsBuilder<EtlDbContext>()
            .UseSqlite(Connection)
            .Options;

        DbContext = new EtlDbContext(options);
        await DbContext.Database.EnsureCreatedAsync();

        // Build services using PRODUCTION code paths
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

        // Use testable URL builder pointing to mock server
        services.AddSingleton<IEbayUrlBuilder>(new TestableEbayUrlBuilder(MockServer!.BaseUrl));

        // Real parsers
        services.AddSingleton<ISearchParser, EbaySearchParser>();
        services.AddSingleton<IListingParser, EbayListingParser>();

        // Real WebscraperClient pointing to Azure Functions API
        services.AddHttpClient<IWebscraperClient, WebscraperClient>(client =>
        {
            client.BaseAddress = new Uri(FunctionsApiUrl);
        });
        services.AddSingleton(new ScraperApiConfig(FunctionsApiUrl, ""));

        // Real job repository pointing to Azurite
        var azuriteConnectionString = "UseDevelopmentStorage=true";
        var tableClient = new Azure.Data.Tables.TableServiceClient(azuriteConnectionString);
        var blobClient = new Azure.Storage.Blobs.BlobServiceClient(azuriteConnectionString);
        services.AddSingleton(tableClient);
        services.AddSingleton(blobClient);
        services.AddSingleton<IJobRepository>(sp =>
            new AzureJobRepository(
                sp.GetRequiredService<Azure.Data.Tables.TableServiceClient>(),
                sp.GetRequiredService<Azure.Storage.Blobs.BlobServiceClient>(),
                sp.GetRequiredService<ILogger<AzureJobRepository>>()));

        // Real EbayScraper
        services.AddSingleton<IEbayScraper, EbayScraper>();
        services.AddSingleton(DbContext);

        var provider = services.BuildServiceProvider();
        EbayScraper = provider.GetRequiredService<IEbayScraper>();
    }

    [TearDown]
    public async Task TearDown()
    {
        await DbContext.DisposeAsync();
        await Connection.DisposeAsync();
    }

    private static bool IsInfrastructureRunning()
    {
        return IsPortInUse(LocalTestInfrastructure.FunctionsPort) &&
               IsPortInUse(LocalTestInfrastructure.AzuritePort);
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("localhost", port);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
```

**Step 2: Delete the temporary DedicatedModeWebscraperClient**

```bash
rm AIOMarketMaker.Tests/E2E/DedicatedModeWebscraperClient.cs
```

**Step 3: Add Azure SDK packages to test project if needed**

Check if `AIOMarketMaker.Tests.csproj` already references Azure.Data.Tables and Azure.Storage.Blobs.

**Step 4: Commit**

```bash
git add AIOMarketMaker.Tests/E2E/E2ETestFixture.cs
git rm AIOMarketMaker.Tests/E2E/DedicatedModeWebscraperClient.cs
git commit -m "refactor: E2E tests use production code paths with Azurite"
```

---

### Task 5: Add Documentation for Local Development

**Files:**
- Create: `AIOWebScraper/README-LOCAL-DEV.md`

**Step 1: Write local development documentation**

```markdown
# Local Development with Azurite

This guide explains how to run AIOWebScraper locally using Azurite (Azure Storage emulator).

## Prerequisites

- Docker installed and running
- .NET 8 SDK
- Azure Functions Core Tools (`func`)

## Quick Start

### 1. Start Azurite

```bash
docker run -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite
```

### 2. Copy Local Settings

```bash
# For Azure Functions API
cp AIOWebScraper/local.settings.local.json AIOWebScraper/local.settings.json

# For ScraperWorker (settings are auto-loaded)
```

### 3. Start Azure Functions API

```bash
cd AIOWebScraper/AIOWebScraper
func start
```

### 4. Start ScraperWorker (Queue Mode)

In a new terminal:

```bash
cd AIOWebScraper/ScraperWorker
dotnet run
```

The worker will automatically connect to Azurite and start polling the `scrape-work` queue.

### 5. Test the Setup

```bash
# Create a job
curl -X POST http://localhost:7071/api/NewJob \
  -H "Content-Type: application/json" \
  -d '{"urls": ["https://example.com"]}'

# Check status (replace JOB_ID with actual ID)
curl "http://localhost:7071/api/GetStatus?jobId=JOB_ID"
```

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                        Azurite                              │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐         │
│  │ Table :10002│  │ Queue :10001│  │ Blob :10000 │         │
│  └─────────────┘  └─────────────┘  └─────────────┘         │
└─────────────────────────────────────────────────────────────┘
         ▲                 ▲                 ▲
         │                 │                 │
         │                 │                 │
┌────────┴─────────────────┴─────────────────┴────────┐
│              Azure Functions API (:7071)            │
│  POST /api/NewJob → Creates job → Enqueues message  │
│  GET /api/GetStatus → Reads from Table Storage      │
│  GET /api/GetResults → Reads from Table + Blob      │
└─────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────┐
│              ScraperWorker (Queue Mode)             │
│  Polls scrape-work queue                            │
│  Fetches URLs with Playwright                       │
│  Saves HTML to Blob Storage                         │
│  Updates job status in Table Storage                │
└─────────────────────────────────────────────────────┘
```

## Connection Strings

For Azurite, use: `UseDevelopmentStorage=true`

This is a shorthand for:
```
DefaultEndpointsProtocol=http;
AccountName=devstoreaccount1;
AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;
BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;
QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;
TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;
```

## Troubleshooting

### "Queue not found" error
Azurite creates queues on first use. The `scrape-work` queue will be created when the first job is submitted.

### Worker not processing jobs
Check that the worker is running and connected to the same Azurite instance:
- Worker logs should show "Polling queue..."
- Verify connection string is `UseDevelopmentStorage=true`
```

**Step 2: Commit**

```bash
git add AIOWebScraper/README-LOCAL-DEV.md
git commit -m "docs: add local development guide with Azurite"
```

---

### Task 6: Run E2E Tests and Verify

**Step 1: Start Azurite**

```bash
docker run -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite
```

**Step 2: Copy local settings and start Functions API**

```bash
cp AIOWebScraper/AIOWebScraper/local.settings.local.json AIOWebScraper/AIOWebScraper/local.settings.json
cd AIOWebScraper/AIOWebScraper
func start
```

**Step 3: Start ScraperWorker**

In new terminal:
```bash
cd AIOWebScraper/ScraperWorker
dotnet run
```

**Step 4: Run E2E tests**

In new terminal:
```bash
cd AIOMarketMaker
dotnet test AIOMarketMaker.Tests --filter "Category=E2E&Category!=Contract" -v n
```

**Expected:** Tests execute using the full production code path through Azurite.

**Step 5: Commit final changes if any adjustments needed**

```bash
git add -A
git commit -m "chore: verify E2E tests work with Azurite infrastructure"
```

---

## Summary

After this implementation:

| Component | Local (Azurite) | Production (Azure) |
|-----------|-----------------|-------------------|
| Table Storage | Azurite :10002 | Azure Table Storage |
| Queue Storage | Azurite :10001 | Azure Queue Storage |
| Blob Storage | Azurite :10000 | Azure Blob Storage |
| Code Path | **Identical** | **Identical** |
| Connection String | `UseDevelopmentStorage=true` | Real Azure connection |

**Key benefits:**
1. Same code paths as production - no separate "test mode"
2. Tests actual Azure SDK integration
3. Easy to set up - just Docker and connection string
4. No new interfaces or mock implementations needed
