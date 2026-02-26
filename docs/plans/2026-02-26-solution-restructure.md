# Solution Restructure: Consolidate ETL into Core + Console

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Eliminate the bloated ETL project by moving services to Core and commands to Console, then deleting ETL and Functions projects.

**Architecture:** The ETL project currently blends three concerns: shared services (ScrapeJobProcessor, StatusRefreshRunner, DbWriteGate), shared models (ScrapeRunModels), and CLI commands. Services and models move to Core (where the data layer already lives). CLI commands move to Console (which already has a proper ITask runner). The Api drops its Etl reference and uses Core directly.

**Tech Stack:** .NET 8.0, EF Core, NUnit, existing ITask infrastructure in Console project

---

## Current State

```
Api ──→ Etl ──→ Core
          │──→ ML
Console ──→ Core
```

Api references Etl for: IScrapeJobProcessor, ScrapeJobProcessor, DbWriteGate, StatusRefreshRunner, IComparablesEtlService (already in Core), ScrapeRunModels (ScrapingConfig, ManualScrapeRequest, etc.)

## Target State

```
Api ──→ Core ──→ ML
Console ──→ Core ──→ ML
```

## Key Findings

- **Data layer already in Core** — `EtlDbContext`, `Listing`, `ScrapeJob`, `MigrationRunner` all live in `AIOMarketMaker.Core.Data`. The Etl `Data/` folder is a stale duplicate (only referenced by Etl's own dead `EtlDbContext`).
- **Functions project is empty** — 0 source files, all archived. Can delete.
- **Console already has ITask pattern** — TaskRunner, auto-discovery via DI, help output.
- **4 dead model files in Etl** — `ListingEtlInput.cs`, `ScrapeJobMessage.cs`, `EnqueueScrapeRetryInput.cs`, `SweepModels.cs` (only referenced in docs).
- **Etl `Startup.cs`** is dead (superseded by `Configure.cs`).
- **Console `Startup.cs`** has its own DI setup that will need merging with Etl's `Configure.cs` to support the richer service set (ONNX classifier, Azure Storage, etc.).

## Behavioral Parity Checklist

- [x] CLI commands preserve identical behavior (just new invocation syntax: `dotnet run -- backfill-confidence` instead of `dotnet run -- --backfill-confidence`)
- [x] Api continues to use IScrapeJobProcessor, DbWriteGate, StatusRefreshRunner with same behavior
- [x] ScrapeRunModels (ScrapingConfig, ManualScrapeRequest, etc.) remain accessible to Api
- [x] Console tasks (pricing, search, migrate) continue to work
- [x] Azure Functions packages removed from Etl → no impact (zero triggers defined)

## What We're Dropping

| Feature | Why | Impact |
|---------|-----|--------|
| `AIOMarketMaker.Etl` project | Consolidated into Core + Console | None — same code, better home |
| `AIOMarketMaker.Functions` project | Empty — all code archived | None |
| Etl `Data/` folder (duplicate) | Stale copy — Core has canonical versions | None |
| Etl `Models/ListingEtlInput.cs` | Dead — only in docs | None |
| Etl `Models/ScrapeJobMessage.cs` | Dead — only in docs | None |
| Etl `Models/EnqueueScrapeRetryInput.cs` | Dead — only in docs | None |
| Etl `Models/SweepModels.cs` | Dead — only in docs | None |
| Etl `Startup.cs` | Dead — superseded by Configure.cs | None |
| `--flag` CLI syntax | Replaced by `taskname` syntax | Muscle memory — document in commit |
| Azure Functions Worker packages | No triggers defined | None |

---

### Task 1: Delete dead code from Etl

**Files:**
- Delete: `AIOMarketMaker.Etl/Data/` (entire folder — stale duplicate of Core/Data)
- Delete: `AIOMarketMaker.Etl/Models/ListingEtlInput.cs`
- Delete: `AIOMarketMaker.Etl/Models/ScrapeJobMessage.cs`
- Delete: `AIOMarketMaker.Etl/Models/EnqueueScrapeRetryInput.cs`
- Delete: `AIOMarketMaker.Etl/Models/SweepModels.cs`
- Delete: `AIOMarketMaker.Etl/Startup.cs`
- Delete: `AIOMarketMaker.Etl/Utils/LocalStorage.cs` (check if used first)

**Step 1: Verify nothing references dead files**

Run: `grep -r "ListingEtlInput\|ScrapeJobMessage\|EnqueueScrapeRetryInput\|SweepModels\|SweepResult\|SweepListingUpdate" --include="*.cs" AIOMarketMaker.Etl/ AIOMarketMaker.Api/ AIOMarketMaker.Core/`
Expected: No matches in .cs files (only in .md docs)

Run: `grep -r "LocalStorage" --include="*.cs" AIOMarketMaker.Etl/`
Expected: Only the file's own definition

Run: `grep -r "AIOMarketMaker\.Etl\.Data" --include="*.cs" AIOMarketMaker.Api/ AIOMarketMaker.Core/`
Expected: No matches (Api/Core use AIOMarketMaker.Core.Data)

**Step 2: Delete the files**

```bash
rm -rf AIOMarketMaker.Etl/Data/
rm AIOMarketMaker.Etl/Models/ListingEtlInput.cs
rm AIOMarketMaker.Etl/Models/ScrapeJobMessage.cs
rm AIOMarketMaker.Etl/Models/EnqueueScrapeRetryInput.cs
rm AIOMarketMaker.Etl/Models/SweepModels.cs
rm AIOMarketMaker.Etl/Startup.cs
rm AIOMarketMaker.Etl/Utils/LocalStorage.cs  # if unused
```

**Step 3: Build ETL project to verify nothing broke**

Run: `dotnet build AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: 0 errors

**Step 4: Commit**

```bash
git add -A && git commit -m "chore: delete dead code from ETL (stale data layer, unused models, old startup)"
```

---

### Task 2: Move ScrapeRunModels to Core

**Files:**
- Move: `AIOMarketMaker.Etl/Models/ScrapeRunModels.cs` → `AIOMarketMaker.Core/Services/ScrapeRunModels.cs`
- Modify: Update namespace from `AIOMarketMaker.Etl.Models` → `AIOMarketMaker.Core.Services`
- Modify: All files in Api and Etl that `using AIOMarketMaker.Etl.Models` → `using AIOMarketMaker.Core.Services`

**Step 1: Move file and update namespace**

Copy `ScrapeRunModels.cs` to Core/Services/, change namespace to `AIOMarketMaker.Core.Services`.

**Step 2: Update all using statements**

Files to update (found by grep):
- `AIOMarketMaker.Api/Program.cs` — `using AIOMarketMaker.Etl.Models` → `using AIOMarketMaker.Core.Services`
- `AIOMarketMaker.Api/Services/NightlyScrapeService.cs`
- `AIOMarketMaker.Api/Services/StartupRecoveryService.cs`
- `AIOMarketMaker.Api/Endpoints/ScrapeEndpoints.cs`
- `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs`

**Step 3: Delete old file**

```bash
rm AIOMarketMaker.Etl/Models/ScrapeRunModels.cs
```

**Step 4: Build full solution**

Run: `dotnet build AIOMarketMaker.sln`
Expected: 0 errors

**Step 5: Run unit tests**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj`
Expected: All pass

**Step 6: Commit**

```bash
git commit -m "refactor: move ScrapeRunModels from Etl to Core"
```

---

### Task 3: Move ETL services to Core

**Files:**
- Move: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs` → `AIOMarketMaker.Core/Services/ScrapeJobProcessor.cs`
- Move: `AIOMarketMaker.Etl/Services/StatusRefreshRunner.cs` → `AIOMarketMaker.Core/Services/StatusRefreshRunner.cs`
- Move: `AIOMarketMaker.Etl/Services/DbWriteGate.cs` → `AIOMarketMaker.Core/Services/DbWriteGate.cs`
- Modify: Update namespaces from `AIOMarketMaker.Etl.Services` → `AIOMarketMaker.Core.Services`
- Modify: All Api files that `using AIOMarketMaker.Etl.Services` → `using AIOMarketMaker.Core.Services`

**Step 1: Move files and update namespaces**

Copy all three to Core/Services/, change namespace to `AIOMarketMaker.Core.Services`.

ScrapeJobProcessor.cs also has `using AIOMarketMaker.Etl.Models` which was already moved in Task 2 — verify this using is gone or updated.

**Step 2: Update using statements in Api**

Files to update:
- `AIOMarketMaker.Api/Program.cs` — remove `using AIOMarketMaker.Etl.Services`
- `AIOMarketMaker.Api/Services/NightlyScrapeService.cs`
- `AIOMarketMaker.Api/Services/StartupRecoveryService.cs`
- `AIOMarketMaker.Api/Endpoints/ScrapeEndpoints.cs`

These files already have `using AIOMarketMaker.Core.Services`, so just delete the Etl.Services line.

**Step 3: Delete old files from Etl**

```bash
rm AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs
rm AIOMarketMaker.Etl/Services/StatusRefreshRunner.cs
rm AIOMarketMaker.Etl/Services/DbWriteGate.cs
```

**Step 4: Update Etl commands that reference Etl.Services**

Check if any commands used `using AIOMarketMaker.Etl.Services` and update to Core.Services.

**Step 5: Check if Core.csproj needs new package references**

ScrapeJobProcessor may depend on packages only in Etl.csproj (e.g., `System.Threading.Channels`). Check and add to Core.csproj if needed.

**Step 6: Build full solution**

Run: `dotnet build AIOMarketMaker.sln`
Expected: 0 errors

**Step 7: Run unit tests**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj`
Expected: All pass

**Step 8: Commit**

```bash
git commit -m "refactor: move ScrapeJobProcessor, StatusRefreshRunner, DbWriteGate from Etl to Core"
```

---

### Task 4: Remove Api's dependency on Etl

**Files:**
- Modify: `AIOMarketMaker.Api/AIOMarketMaker.Api.csproj` — remove `<ProjectReference>` to Etl
- Verify: No remaining `using AIOMarketMaker.Etl.*` in Api

**Step 1: Remove project reference**

Edit Api.csproj to remove:
```xml
<ProjectReference Include="..\AIOMarketMaker.Etl\AIOMarketMaker.Etl.csproj" />
```

**Step 2: Verify no remaining Etl references**

Run: `grep -r "AIOMarketMaker\.Etl" --include="*.cs" AIOMarketMaker.Api/`
Expected: No matches

**Step 3: Build Api**

Run: `dotnet build AIOMarketMaker.Api/AIOMarketMaker.Api.csproj`
Expected: 0 errors

**Step 4: Commit**

```bash
git commit -m "refactor: remove Api dependency on Etl project"
```

---

### Task 5: Move ETL commands to Console as ITask implementations

**Files:**
- Create: `AIOMarketMaker.Console/Tasks/BackfillConfidenceTask.cs`
- Create: `AIOMarketMaker.Console/Tasks/ComparablesTask.cs`
- Create: `AIOMarketMaker.Console/Tasks/ReindexMissingTask.cs`
- Create: `AIOMarketMaker.Console/Tasks/ValidationTask.cs`
- Create: `AIOMarketMaker.Console/Tasks/KAnalysisTask.cs`
- Create: `AIOMarketMaker.Console/Tasks/BatchLabelTask.cs`
- Modify: `AIOMarketMaker.Console/Startup.cs` — register new tasks
- Modify: `AIOMarketMaker.Console/AIOMarketMaker.Console.csproj` — add ML reference and required packages

**Pattern for each command → task conversion:**

```csharp
// From: static class with Run(IHost host, string[] args)
// To: ITask implementation with DI constructor injection

public class BackfillConfidenceTask : ITask
{
    private readonly IDbContextFactory<EtlDbContext> _dbFactory;
    private readonly IVariantClassifierClient _classifier;

    public string Name => "backfill-confidence";
    public string Description => "Backfill ClassifierConfidence + IsComparable using ensemble model";

    public BackfillConfidenceTask(
        IDbContextFactory<EtlDbContext> dbFactory,
        IVariantClassifierClient classifier)
    {
        _dbFactory = dbFactory;
        _classifier = classifier;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        // Move body from BackfillConfidenceCommand.Run, replacing
        // service resolution from host with injected fields
        ...
    }
}
```

**Step 1: Update Console.csproj with required dependencies**

Add project reference to ML and required packages (Azure Storage, ONNX, EF Core SqlServer, etc.).

**Step 2: Update Console Startup.cs DI**

Merge Etl's Configure.cs service registrations into Console's HostHelper.CreateHost. Register all services the commands need (classifier, vector index, embeddings, etc.) plus the new tasks.

**Step 3: Create each task file**

Convert each static command to an ITask implementation. The internal logic stays identical — only the outer shell changes from `static Run(IHost)` to `ITask.ExecuteAsync(string[], CancellationToken)` with DI constructor injection.

**Step 4: Register tasks in Startup.cs**

```csharp
services.AddTask<BackfillConfidenceTask>();
services.AddTask<ComparablesTask>();
services.AddTask<ReindexMissingTask>();
services.AddTask<ValidationTask>();
services.AddTask<KAnalysisTask>();
services.AddTask<BatchLabelTask>();
```

**Step 5: Build Console**

Run: `dotnet build AIOMarketMaker.Console/AIOMarketMaker.Console.csproj`
Expected: 0 errors

**Step 6: Verify help works**

Run: `dotnet run --project AIOMarketMaker.Console -- help`
Expected: All tasks listed with descriptions

**Step 7: Commit**

```bash
git commit -m "feat: migrate ETL commands to Console as ITask implementations"
```

---

### Task 6: Delete Etl and Functions projects

**Files:**
- Delete: `AIOMarketMaker.Etl/` (entire project)
- Delete: `AIOMarketMaker.Functions/` (entire project)
- Modify: `AIOMarketMaker.sln` — remove both project references
- Modify: Any test projects referencing Etl

**Step 1: Check for remaining references to Etl**

Run: `grep -r "AIOMarketMaker\.Etl\|AIOMarketMaker\.Functions" --include="*.csproj" .`
Expected: Only the sln file and their own csproj

**Step 2: Remove from solution**

```bash
dotnet sln AIOMarketMaker.sln remove AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj
dotnet sln AIOMarketMaker.sln remove AIOMarketMaker.Functions/AIOMarketMaker.Functions.csproj
```

**Step 3: Delete project directories**

```bash
rm -rf AIOMarketMaker.Etl/
rm -rf AIOMarketMaker.Functions/
```

**Step 4: Build full solution**

Run: `dotnet build AIOMarketMaker.sln`
Expected: 0 errors

**Step 5: Run all tests**

Run: `dotnet test AIOMarketMaker.sln`
Expected: All pass

**Step 6: Commit**

```bash
git commit -m "chore: remove Etl and Functions projects from solution"
```

---

### Task 7: Clean up Console project

**Files:**
- Delete: `AIOMarketMaker.Console/Tasks/TerminateOrchestrationsTask.cs` (Durable Functions — dead)
- Delete: `AIOMarketMaker.Console/Tasks/SearchTestTask.cs` (if it's a duplicate of SearchTask)
- Modify: `AIOMarketMaker.Console/Startup.cs` — remove registrations for deleted tasks

**Step 1: Review SearchTestTask and TerminateOrchestrationsTask**

Check if they reference archived/removed infrastructure (Durable Functions, old orchestrators).

**Step 2: Delete dead tasks, update registrations**

**Step 3: Build and test**

**Step 4: Commit**

```bash
git commit -m "chore: remove dead Console tasks (TerminateOrchestrations, SearchTest)"
```

---

## Final State

```
AIOMarketMaker.sln
├── AIOMarketMaker.Api          — HTTP API (references Core, ML, WebScraper.Storage)
├── AIOMarketMaker.Core         — Domain logic, data, services, parsers
├── AIOMarketMaker.ML           — ONNX classifier, training scripts
├── AIOMarketMaker.Console      — CLI tools (references Core, ML, WebScraper.Storage)
├── AIOMarketMaker.Desktop      — Electron UI
├── AIOMarketMaker.Tests.*      — Test projects
└── AIOWebScraper.Storage.Azure — External scraper integration
```

Projects removed: Etl, Functions (2 fewer projects, ~2,500 lines deleted)
