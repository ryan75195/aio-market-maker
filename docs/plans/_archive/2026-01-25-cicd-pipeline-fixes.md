# CI/CD Pipeline Fixes Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix broken CI/CD pipelines across both AIOMarketMaker and AIOWebScraper projects to enable reliable deployments.

**Architecture:** Both projects use GitHub Actions workflows with Bicep infrastructure-as-code. AIOMarketMaker deploys Azure Functions + SQL migrations. AIOWebScraper deploys Azure Container Apps + Functions API.

**Tech Stack:** GitHub Actions, Azure Bicep, .NET 8.0, Azure Functions, Azure Container Apps

---

## Summary of Changes

| # | Project | Change | Priority |
|---|---------|--------|----------|
| 1 | AIOWebScraper | Add test job before deployment | Medium |
| 2 | AIOWebScraper | Add App Insights env var to Container App Bicep | Low |
| 3 | AIOWebScraper | Remove stale branch trigger | Low |
| 5 | AIOMarketMaker | Fix MigrationCli → Console reference | **Critical** |
| 6 | AIOMarketMaker | Add test job before deployment | Medium |
| 7 | AIOMarketMaker | Remove stale branch trigger | Low |

---

## Task 1: Fix AIOMarketMaker MigrationCli Reference (CRITICAL)

**Files:**
- Modify: `AIOMarketMaker/.github/workflows/deploy-functions.yml`

**Context:** The `AIOMarketMaker.MigrationCli` project was deleted on Dec 30, 2025 and consolidated into `AIOMarketMaker.Console` with a `migrate` task. The pipeline currently references the deleted project and will fail on next deploy.

**Step 1: Update build job to publish Console instead of MigrationCli**

In `deploy-functions.yml`, replace lines 44-45:

```yaml
# OLD (broken):
      - name: Restore and publish Migration CLI
        run: dotnet publish AIOMarketMaker/AIOMarketMaker.MigrationCli/AIOMarketMaker.MigrationCli.csproj --configuration Release --output ./publish-migrations

# NEW:
      - name: Restore and publish Console app (includes migrations)
        run: dotnet publish AIOMarketMaker/AIOMarketMaker.Console/AIOMarketMaker.Console.csproj --configuration Release --output ./publish-console
```

**Step 2: Update artifact upload name**

Replace lines 54-59:

```yaml
# OLD:
      - name: Upload Migration CLI artifact
        uses: actions/upload-artifact@v4
        with:
          name: migration-cli
          path: ./publish-migrations
          if-no-files-found: error

# NEW:
      - name: Upload Console app artifact
        uses: actions/upload-artifact@v4
        with:
          name: console-app
          path: ./publish-console
          if-no-files-found: error
```

**Step 3: Update deploy-migrations job to use Console app**

Replace lines 106-122:

```yaml
# OLD:
      - name: Download Migration CLI artifact
        uses: actions/download-artifact@v4
        with:
          name: migration-cli
          path: ./migration-cli

      # ... setup .NET ...

      - name: Run Migrations
        env:
          SQL_CONNECTION_STRING: "..."
        run: |
          echo "Running database migrations..."
          dotnet ./migration-cli/AIOMarketMaker.MigrationCli.dll --connection "$SQL_CONNECTION_STRING"

# NEW:
      - name: Download Console app artifact
        uses: actions/download-artifact@v4
        with:
          name: console-app
          path: ./console-app

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - name: Run Migrations
        env:
          SQL_CONNECTION_STRING: "Server=tcp:${{ needs.deploy-infrastructure.outputs.sqlServerFqdn }},1433;Initial Catalog=${{ needs.deploy-infrastructure.outputs.sqlDatabaseName }};Persist Security Info=False;User ID=sqladmin;Password=${{ vars.SQL_ADMIN_PASSWORD }};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
        run: |
          echo "Running database migrations..."
          dotnet ./console-app/AIOMarketMaker.Console.dll -- migrate "$SQL_CONNECTION_STRING"
```

**Step 4: Update paths trigger to reference Console instead of MigrationCli**

Replace line 11:

```yaml
# OLD:
      - 'AIOMarketMaker.MigrationCli/**'

# NEW:
      - 'AIOMarketMaker.Console/**'
```

**Step 5: Verify syntax locally**

Run: `cat AIOMarketMaker/.github/workflows/deploy-functions.yml | head -50`

Expected: Valid YAML with Console references

---

## Task 2: Remove Stale Branch Trigger from AIOMarketMaker

**Files:**
- Modify: `AIOMarketMaker/.github/workflows/deploy-functions.yml`

**Step 1: Remove azure-functions-migration from branch triggers**

Replace lines 5-7:

```yaml
# OLD:
    branches:
      - master
      - azure-functions-migration

# NEW:
    branches:
      - master
```

---

## Task 3: Add Test Job to AIOMarketMaker Pipeline

**Files:**
- Modify: `AIOMarketMaker/.github/workflows/deploy-functions.yml`

**Step 1: Add test job after build, before deploy-infrastructure**

Insert new job after the `build` job (after line 59):

```yaml
  # ─────────────────────────────────────────────────────────────────
  # Job: Run Tests
  # ─────────────────────────────────────────────────────────────────
  test:
    runs-on: ubuntu-latest
    needs: build
    steps:
      - name: Checkout repository
        uses: actions/checkout@v4
        with:
          path: AIOMarketMaker

      - name: Checkout AIOWebScraper
        uses: actions/checkout@v4
        with:
          repository: private/EXTERNAL_SCRAPER_REPO
          path: AIOWebScraper
          token: ${{ secrets.GH_PAT }}

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - name: Run unit tests
        run: dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit" --configuration Release --verbosity normal
```

**Step 2: Update deploy-infrastructure to depend on test job**

Change line 63:

```yaml
# OLD:
    needs: build

# NEW:
    needs: [build, test]
```

---

## Task 4: Remove Stale Branch Trigger from AIOWebScraper

**Files:**
- Modify: `AIOWebScraper/.github/workflows/deploy-aca.yml`

**Step 1: Remove feature/azure-container-apps-migration from branch triggers**

Replace line 5:

```yaml
# OLD:
    branches: [master, feature/azure-container-apps-migration]

# NEW:
    branches: [master]
```

---

## Task 5: Add Test Job to AIOWebScraper Pipeline

**Files:**
- Modify: `AIOWebScraper/.github/workflows/deploy-aca.yml`

**Step 1: Add test job before deploy-infrastructure**

Insert new job before `deploy-infrastructure` (after line 13):

```yaml
  # ─────────────────────────────────────────────────────────────────
  # Job 0: Run Tests
  # ─────────────────────────────────────────────────────────────────
  test:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Run unit tests
        run: dotnet test AIOWebScraper.Tests/AIOWebScraper.Tests.csproj --filter "Category=Unit" --configuration Release --verbosity normal
```

**Step 2: Update deploy-infrastructure to depend on test job**

Add `needs: test` to deploy-infrastructure job (around line 18):

```yaml
  deploy-infrastructure:
    runs-on: ubuntu-latest
    needs: test  # ADD THIS LINE
    outputs:
```

---

## Task 6: Add App Insights to Container App Bicep

**Files:**
- Modify: `AIOWebScraper/infra/main.bicep`

**Step 1: Add App Insights connection string parameter**

Add after line 24 (after residentialProxy param):

```bicep
@description('Application Insights connection string (optional)')
@secure()
param appInsightsConnectionString string = ''
```

**Step 2: Add secret for App Insights in Container App**

In the `queueWorkerApp` resource, add to the secrets array (around line 189-202):

```bicep
        {
          name: 'appinsights-connection'
          value: appInsightsConnectionString
        }
```

**Step 3: Add env var for App Insights in Container App**

In the container env array (around line 219-240), add:

```bicep
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              secretRef: 'appinsights-connection'
            }
```

**Step 4: Update workflow to pass App Insights connection string**

In `deploy-aca.yml`, update the Bicep deployment parameters (around line 39-43):

```yaml
            --parameters \
              storageConnectionString="${{ vars.STORAGE_CONNECTION_STRING }}" \
              residentialProxy="${{ vars.RESIDENTIAL_PROXY }}" \
              twoCaptchaApiKey="${{ vars.TWOCAPTCHA_API_KEY }}" \
              monthlyBudgetLimit=${{ vars.MONTHLY_BUDGET_LIMIT }} \
              appInsightsConnectionString="${{ vars.APPINSIGHTS_CONNECTION_STRING }}" \
```

**Note:** You'll need to add `APPINSIGHTS_CONNECTION_STRING` as a GitHub variable. Get the value from the Log Analytics workspace created by the Bicep template.

---

## Verification Checklist

After all tasks complete:

1. [ ] `AIOMarketMaker/.github/workflows/deploy-functions.yml` references `AIOMarketMaker.Console` not `MigrationCli`
2. [ ] `AIOMarketMaker/.github/workflows/deploy-functions.yml` has `test` job
3. [ ] `AIOMarketMaker/.github/workflows/deploy-functions.yml` only triggers on `master` branch
4. [ ] `AIOWebScraper/.github/workflows/deploy-aca.yml` has `test` job
5. [ ] `AIOWebScraper/.github/workflows/deploy-aca.yml` only triggers on `master` branch
6. [ ] `AIOWebScraper/infra/main.bicep` has App Insights env var for Container App

---

## Commit Strategy

Commit each project's changes separately:

```bash
# AIOMarketMaker commits
cd AIOMarketMaker
git add .github/workflows/deploy-functions.yml
git commit -m "fix: update CI/CD to use Console app for migrations, add test job"

# AIOWebScraper commits
cd ../AIOWebScraper
git add .github/workflows/deploy-aca.yml infra/main.bicep
git commit -m "feat: add test job to CI/CD, add App Insights to Container App"
```
