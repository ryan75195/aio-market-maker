# Standalone Migrations Project Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Create a minimal standalone migrations project to replace Console app usage in CI/CD pipeline.

**Architecture:** Single console app that only references Core for MigrationRunner. No Pinecone, OpenAI, or other dependencies. Called from GitHub Actions pipeline.

**Tech Stack:** .NET 8.0, MigrationRunner (from Core)

---

## Summary of Changes

| # | Change | Files |
|---|--------|-------|
| 1 | Create Migrations project | New: `AIOMarketMaker.Migrations/` |
| 2 | Update pipeline to use new project | Modify: `.github/workflows/deploy-functions.yml` |

---

## Task 1: Create AIOMarketMaker.Migrations Project

**Files:**
- Create: `AIOMarketMaker.Migrations/AIOMarketMaker.Migrations.csproj`
- Create: `AIOMarketMaker.Migrations/Program.cs`

**Step 1: Create project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AIOMarketMaker.Core\AIOMarketMaker.Core.csproj" />
  </ItemGroup>

</Project>
```

**Step 2: Create Program.cs**

```csharp
using AIOMarketMaker.Core.Data.Migrations;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: AIOMarketMaker.Migrations <connection-string>");
    return 1;
}

var connectionString = args[0];
Console.WriteLine("=== AIOMarketMaker SQL Server Migrations ===");

try
{
    var runner = new MigrationRunner(connectionString, null, useSqlServer: true);
    runner.ApplyMigrations();
    Console.WriteLine("Migrations completed successfully.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Migration failed: {ex.Message}");
    Console.Error.WriteLine(ex.ToString());
    return 1;
}
```

**Step 3: Verify it builds**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Migrations/AIOMarketMaker.Migrations.csproj`

Expected: Build succeeds

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Migrations/
git commit -m "feat: add standalone migrations project for CI/CD"
```

---

## Task 2: Update CI/CD Pipeline

**Files:**
- Modify: `AIOMarketMaker/.github/workflows/deploy-functions.yml`

**Step 1: Update build job to publish Migrations instead of Console**

Replace the Console publish step with:

```yaml
      - name: Restore and publish Migrations project
        run: dotnet publish AIOMarketMaker/AIOMarketMaker.Migrations/AIOMarketMaker.Migrations.csproj --configuration Release --output ./publish-migrations
```

**Step 2: Update artifact upload**

Replace:
```yaml
      - name: Upload Console app artifact
        uses: actions/upload-artifact@v4
        with:
          name: console-app
          path: ./publish-console
          if-no-files-found: error
```

With:
```yaml
      - name: Upload Migrations artifact
        uses: actions/upload-artifact@v4
        with:
          name: migrations
          path: ./publish-migrations
          if-no-files-found: error
```

**Step 3: Update deploy-migrations job**

Replace download and run steps:

```yaml
      - name: Download Migrations artifact
        uses: actions/download-artifact@v4
        with:
          name: migrations
          path: ./migrations

      - name: Run Migrations
        env:
          SQL_CONNECTION_STRING: "Server=tcp:${{ needs.deploy-infrastructure.outputs.sqlServerFqdn }},1433;Initial Catalog=${{ needs.deploy-infrastructure.outputs.sqlDatabaseName }};Persist Security Info=False;User ID=sqladmin;Password=${{ vars.SQL_ADMIN_PASSWORD }};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
        run: |
          echo "Running database migrations..."
          dotnet ./migrations/AIOMarketMaker.Migrations.dll "$SQL_CONNECTION_STRING"
```

**Step 4: Update paths trigger**

Replace `AIOMarketMaker.Console/**` with `AIOMarketMaker.Migrations/**` in the paths trigger.

**Step 5: Remove .NET setup from deploy-migrations job**

The Migrations project is self-contained, so we don't need to setup .NET in the deploy-migrations job. However, keep it for safety if the runtime isn't bundled.

**Step 6: Commit**

```bash
git add AIOMarketMaker/.github/workflows/deploy-functions.yml
git commit -m "fix: use standalone Migrations project in CI/CD pipeline"
```

---

## Verification Checklist

After all tasks complete:

1. [ ] `AIOMarketMaker.Migrations/` project exists and builds
2. [ ] Project only references `AIOMarketMaker.Core`
3. [ ] Pipeline publishes Migrations project (not Console)
4. [ ] Pipeline runs migrations using new project
5. [ ] No Pinecone/OpenAI dependencies in migration step

---

## Commit Strategy

Two commits:
1. `feat: add standalone migrations project for CI/CD`
2. `fix: use standalone Migrations project in CI/CD pipeline`

Then push to trigger pipeline.
