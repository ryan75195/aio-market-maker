# Event-Driven ETL Pipeline Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace polling-based batch processing with event-driven per-listing pipeline using blob triggers and Durable Functions.

**Architecture:** Blob triggers fire when scraper saves HTML, starting a Durable orchestration per listing that waits for both listing + description blobs (or 5-min timeout), then processes and saves to database.

**Tech Stack:** .NET 8, Azure Functions v4, Durable Functions, Azure Blob Storage, Azurite (local), NUnit/Moq

---

## Phase 1: Scraper Changes (AIOWebScraper)

### Task 1.1: Add GroupId and FileKey to ScrapeQueueMessage

**Files:**
- Modify: `<EXTERNAL_SCRAPER_REPO>\AIOWebScraper.Storage.Azure\QueueMessage.cs`
- Test: `<EXTERNAL_SCRAPER_REPO>\AIOWebScraper.Tests\Unit\QueueMessageTests.cs`

**Step 1: Write the failing test**

Create test file:
```csharp
// QueueMessageTests.cs
using NUnit.Framework;
using ScraperWorker.Services;

namespace AIOWebScraper.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class ScrapeQueueMessage_UnitTests
{
    [Test]
    public void Should_have_GroupId_property()
    {
        var message = new ScrapeQueueMessage
        {
            JobId = "job123",
            Url = "https://example.com",
            GroupId = "listing123"
        };

        Assert.That(message.GroupId, Is.EqualTo("listing123"));
    }

    [Test]
    public void Should_have_FileKey_property()
    {
        var message = new ScrapeQueueMessage
        {
            JobId = "job123",
            Url = "https://example.com",
            FileKey = "listing"
        };

        Assert.That(message.FileKey, Is.EqualTo("listing"));
    }

    [Test]
    public void Should_allow_null_GroupId_and_FileKey_for_backward_compatibility()
    {
        var message = new ScrapeQueueMessage
        {
            JobId = "job123",
            Url = "https://example.com"
        };

        Assert.Multiple(() =>
        {
            Assert.That(message.GroupId, Is.Null);
            Assert.That(message.FileKey, Is.Null);
        });
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test AIOWebScraper/AIOWebScraper.Tests/AIOWebScraper.Tests.csproj --filter "FullyQualifiedName~ScrapeQueueMessage_UnitTests" -v n`

Expected: FAIL - `GroupId` and `FileKey` properties don't exist

**Step 3: Write minimal implementation**

Add to `QueueMessage.cs` after line 42 (after `ProxyConfigJson`):
```csharp
    /// <summary>
    /// Optional caller-defined grouping identifier (e.g., listing ID).
    /// When provided with FileKey, blob path becomes: {jobId}/{GroupId}/{FileKey}.html
    /// </summary>
    public string? GroupId { get; init; }

    /// <summary>
    /// Optional caller-defined file name (e.g., "listing" or "description").
    /// When provided with GroupId, blob path becomes: {jobId}/{GroupId}/{FileKey}.html
    /// </summary>
    public string? FileKey { get; init; }
```

**Step 4: Run test to verify it passes**

Run: `dotnet test AIOWebScraper/AIOWebScraper.Tests/AIOWebScraper.Tests.csproj --filter "FullyQualifiedName~ScrapeQueueMessage_UnitTests" -v n`

Expected: PASS

**Step 5: Commit**

```bash
cd <EXTERNAL_SCRAPER_REPO>
git add AIOWebScraper.Storage.Azure/QueueMessage.cs AIOWebScraper.Tests/Unit/QueueMessageTests.cs
git commit -m "feat: add GroupId and FileKey to ScrapeQueueMessage for custom blob paths"
```

---

### Task 1.2: Update AzureJobRepository to use GroupId/FileKey for blob paths

**Files:**
- Modify: `<EXTERNAL_SCRAPER_REPO>\AIOWebScraper.Storage.Azure\AzureJobRepository.cs`
- Modify: `<EXTERNAL_SCRAPER_REPO>\AIOWebScraper.Storage.Azure\IJobRepository.cs` (interface update)
- Test: `<EXTERNAL_SCRAPER_REPO>\AIOWebScraper.Tests\Unit\AzureJobRepositoryTests.cs`

**Step 1: Write the failing test**

```csharp
// AzureJobRepositoryTests.cs
using NUnit.Framework;

namespace AIOWebScraper.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class BlobPathBuilder_UnitTests
{
    [Test]
    public void Should_use_grouped_path_when_GroupId_and_FileKey_provided()
    {
        var result = BlobPathBuilder.Build(
            jobId: "job123",
            url: "https://ebay.com/itm/12345",
            groupId: "12345",
            fileKey: "listing");

        Assert.That(result, Is.EqualTo("job123/12345/listing.html"));
    }

    [Test]
    public void Should_use_legacy_path_when_GroupId_is_null()
    {
        var result = BlobPathBuilder.Build(
            jobId: "job123",
            url: "https://ebay.com/itm/12345",
            groupId: null,
            fileKey: "listing");

        // Legacy format: sanitized URL
        Assert.That(result, Does.StartWith("job123/"));
        Assert.That(result, Does.EndWith(".html"));
        Assert.That(result, Does.Not.Contain("/12345/listing"));
    }

    [Test]
    public void Should_use_legacy_path_when_FileKey_is_null()
    {
        var result = BlobPathBuilder.Build(
            jobId: "job123",
            url: "https://ebay.com/itm/12345",
            groupId: "12345",
            fileKey: null);

        // Legacy format when FileKey missing
        Assert.That(result, Does.StartWith("job123/"));
        Assert.That(result, Does.Not.Contain("/12345/listing"));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test AIOWebScraper/AIOWebScraper.Tests/AIOWebScraper.Tests.csproj --filter "FullyQualifiedName~BlobPathBuilder_UnitTests" -v n`

Expected: FAIL - `BlobPathBuilder` class doesn't exist

**Step 3: Write minimal implementation**

Create new file `AIOWebScraper.Storage.Azure/BlobPathBuilder.cs`:
```csharp
using System.Text.RegularExpressions;

namespace ScraperWorker.Services;

/// <summary>
/// Builds blob storage paths for scraped HTML content.
/// </summary>
public static class BlobPathBuilder
{
    /// <summary>
    /// Builds the blob path based on provided parameters.
    /// If GroupId and FileKey are both provided, uses grouped format: {jobId}/{groupId}/{fileKey}.html
    /// Otherwise uses legacy format: {jobId}/{sanitizedUrl}.html
    /// </summary>
    public static string Build(string jobId, string url, string? groupId, string? fileKey)
    {
        if (!string.IsNullOrEmpty(groupId) && !string.IsNullOrEmpty(fileKey))
        {
            return $"{jobId}/{groupId}/{fileKey}.html";
        }

        // Legacy format: sanitize URL for blob name
        var safeUrl = Regex.Replace(url, @"[^\w\-]", "_");
        return $"{jobId}/{safeUrl}.html";
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test AIOWebScraper/AIOWebScraper.Tests/AIOWebScraper.Tests.csproj --filter "FullyQualifiedName~BlobPathBuilder_UnitTests" -v n`

Expected: PASS

**Step 5: Commit**

```bash
git add AIOWebScraper.Storage.Azure/BlobPathBuilder.cs AIOWebScraper.Tests/Unit/AzureJobRepositoryTests.cs
git commit -m "feat: add BlobPathBuilder for grouped blob paths"
```

---

### Task 1.3: Update IJobRepository interface and SaveContentAsync

**Files:**
- Modify: `<EXTERNAL_SCRAPER_REPO>\AIOWebScraper.Storage.Azure\AzureJobRepository.cs`

**Step 1: Write the failing test**

Add to existing test file or create integration test:
```csharp
[Test]
[Category("Integration")]
public async Task SaveContentAsync_Should_use_grouped_path_when_GroupId_and_FileKey_provided()
{
    // This test requires Azurite running - mark as integration
    // For unit testing, we verify BlobPathBuilder separately
}
```

**Step 2: Update interface**

In `AzureJobRepository.cs`, update the `IJobRepository` interface (line 13):
```csharp
Task SaveContentAsync(string jobId, string url, string html, string? groupId, string? fileKey, CancellationToken ct);
```

Also add an overload for backward compatibility:
```csharp
Task SaveContentAsync(string jobId, string url, string html, CancellationToken ct);
```

**Step 3: Update implementation**

Replace the `SaveContentAsync` method (lines 97-133) with:
```csharp
public Task SaveContentAsync(string jobId, string url, string html, CancellationToken ct)
{
    return SaveContentAsync(jobId, url, html, groupId: null, fileKey: null, ct);
}

public async Task SaveContentAsync(string jobId, string url, string html, string? groupId, string? fileKey, CancellationToken ct)
{
    var blobName = BlobPathBuilder.Build(jobId, url, groupId, fileKey);
    try
    {
        var blobClient = _blob.GetBlobClient(blobName);
        await blobClient.UploadAsync(
            BinaryData.FromString(html),
            overwrite: true,
            cancellationToken: ct);

        var update = new JobItemEntity(
            jobId,
            JobStatusType.Success,
            url,
            DateTimeOffset.UtcNow,
            blobClient.Uri.ToString(),
            error: null);

        await _itemTable.UpsertEntityAsync(update, TableUpdateMode.Merge, ct);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "SaveContentAsync failed for jobId {JobId}, url {Url}", jobId, url);

        var update = new JobItemEntity(
            jobId,
            JobStatusType.Failure,
            url,
            DateTimeOffset.UtcNow,
            blobUri: $"{_blob.Uri}/{blobName}",
            error: ex.Message);

        await _itemTable.UpsertEntityAsync(update, TableUpdateMode.Merge, ct);
    }
}
```

**Step 4: Update GetFileContentsAsync similarly**

Add overloaded method:
```csharp
public Task<string> GetFileContentsAsync(string jobId, string url, CancellationToken ct)
{
    return GetFileContentsAsync(jobId, url, groupId: null, fileKey: null, ct);
}

public async Task<string> GetFileContentsAsync(string jobId, string url, string? groupId, string? fileKey, CancellationToken ct)
{
    try
    {
        var blobName = BlobPathBuilder.Build(jobId, url, groupId, fileKey);
        var blobClient = _blob.GetBlobClient(blobName);
        var download = await blobClient.DownloadContentAsync(cancellationToken: ct);
        return download.Value.Content.ToString();
    }
    catch (RequestFailedException ex) when (ex.Status == 404)
    {
        _logger.LogWarning("GetFileContentsAsync: Blob not found for url {Url}", url);
        return string.Empty;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "GetFileContentsAsync failed for jobId {JobId}, url {Url}", jobId, url);
        throw;
    }
}
```

**Step 5: Run all scraper tests**

Run: `dotnet test AIOWebScraper/AIOWebScraper.Tests/AIOWebScraper.Tests.csproj --filter Category=Unit -v n`

Expected: PASS

**Step 6: Commit**

```bash
git add AIOWebScraper.Storage.Azure/AzureJobRepository.cs
git commit -m "feat: update SaveContentAsync and GetFileContentsAsync to support grouped blob paths"
```

---

### Task 1.4: Update JobItemProcessor to pass GroupId/FileKey

**Files:**
- Modify: `<EXTERNAL_SCRAPER_REPO>\ScraperWorker\Services\JobItemProcessor.cs`
- Modify: `<EXTERNAL_SCRAPER_REPO>\ScraperWorker\Services\SimpleQueueWorker.cs`

**Step 1: Update JobItemProcessor interface**

The processor needs to receive GroupId and FileKey from the queue message.

**Step 2: Update ProcessAsync signature**

In `JobItemProcessor.cs`, update the interface and method to accept optional parameters:
```csharp
public interface IJobItemProcessor
{
    Task<bool> ProcessAsync(string url, string jobId, string? groupId, string? fileKey, CancellationToken ct);
}
```

**Step 3: Update implementation**

Update the call to `SaveContentAsync`:
```csharp
await _repo.SaveContentAsync(jobId, url, result.Html!, groupId, fileKey, ct);
```

**Step 4: Update SimpleQueueWorker**

In `SimpleQueueWorker.cs`, update the call to pass GroupId/FileKey from the message:
```csharp
var success = await _processor.ProcessAsync(
    message.Url,
    message.JobId,
    message.GroupId,
    message.FileKey,
    ct);
```

**Step 5: Run tests**

Run: `dotnet test AIOWebScraper/AIOWebScraper.Tests/AIOWebScraper.Tests.csproj -v n`

Expected: PASS

**Step 6: Commit**

```bash
git add ScraperWorker/Services/JobItemProcessor.cs ScraperWorker/Services/SimpleQueueWorker.cs
git commit -m "feat: pass GroupId/FileKey through processor to repository"
```

---

## Phase 2: Market Maker Core Changes

### Task 2.1: Add BuildDescriptionUrl to EbayUrlBuilder

**Files:**
- Modify: `<REPO_ROOT>\AIOMarketMaker.Core\Services\EbayUrlBuilder.cs`
- Test: `<REPO_ROOT>\AIOMarketMaker.Tests\Unit\Services\EbayUrlBuilderTests.cs`

**Step 1: Write the failing test**

```csharp
// EbayUrlBuilderTests.cs
using NUnit.Framework;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class EbayUrlBuilder_UnitTests
{
    private EbayUrlBuilder _urlBuilder = null!;

    [SetUp]
    public void SetUp()
    {
        _urlBuilder = new EbayUrlBuilder();
    }

    [Test]
    public void BuildDescriptionUrl_Should_return_valid_description_url()
    {
        var result = _urlBuilder.BuildDescriptionUrl("306278488042");

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.StartWith("https://itm.ebaydesc.com/itmdesc/306278488042"));
            Assert.That(result, Does.Contain("excSoj=1"));
            Assert.That(result, Does.Contain("domain=ebay.com"));
        });
    }

    [Test]
    public void BuildDescriptionUrl_Should_throw_for_null_listingId()
    {
        Assert.Throws<ArgumentException>(() => _urlBuilder.BuildDescriptionUrl(null!));
    }

    [Test]
    public void BuildDescriptionUrl_Should_throw_for_empty_listingId()
    {
        Assert.Throws<ArgumentException>(() => _urlBuilder.BuildDescriptionUrl(""));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~EbayUrlBuilder_UnitTests.BuildDescriptionUrl" -v n`

Expected: FAIL - `BuildDescriptionUrl` method doesn't exist

**Step 3: Write minimal implementation**

Add to `EbayUrlBuilder.cs` interface:
```csharp
public interface IEbayUrlBuilder
{
    string BuildSearchUrl(string query, bool sold, int page, Condition condition, BuyingFormat buyingFormat);
    string BuildListingUrl(string itemId);
    string BuildDescriptionUrl(string listingId);
}
```

Add implementation:
```csharp
public string BuildDescriptionUrl(string listingId)
{
    if (string.IsNullOrWhiteSpace(listingId))
        throw new ArgumentException("Listing ID cannot be null or empty.", nameof(listingId));

    return $"https://itm.ebaydesc.com/itmdesc/{listingId}" +
           "?t=0&category=139971&excSoj=1&ver=0&excTrk=1&lsite=3" +
           "&ittenable=false&domain=ebay.com&descgauge=1&cspheader=1&oneClk=2&secureDesc=1";
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~EbayUrlBuilder_UnitTests.BuildDescriptionUrl" -v n`

Expected: PASS

**Step 5: Commit**

```bash
cd <REPO_ROOT>
git add AIOMarketMaker.Core/Services/EbayUrlBuilder.cs AIOMarketMaker.Tests/Unit/Services/EbayUrlBuilderTests.cs
git commit -m "feat: add BuildDescriptionUrl to EbayUrlBuilder for parallel description fetching"
```

---

### Task 2.2: Add ScrapeUrlRequest model

**Files:**
- Create: `<REPO_ROOT>\AIOMarketMaker.Core\Models\ScrapeUrlRequest.cs`
- Test: `<REPO_ROOT>\AIOMarketMaker.Tests\Unit\Models\ScrapeUrlRequestTests.cs`

**Step 1: Write the failing test**

```csharp
// ScrapeUrlRequestTests.cs
using NUnit.Framework;
using AIOMarketMaker.Core.Models;

namespace AIOMarketMaker.Tests.Unit.Models;

[TestFixture]
[Category("Unit")]
public class ScrapeUrlRequest_UnitTests
{
    [Test]
    public void Should_store_all_properties()
    {
        var request = new ScrapeUrlRequest
        {
            Url = "https://ebay.com/itm/123",
            GroupId = "123",
            FileKey = "listing"
        };

        Assert.Multiple(() =>
        {
            Assert.That(request.Url, Is.EqualTo("https://ebay.com/itm/123"));
            Assert.That(request.GroupId, Is.EqualTo("123"));
            Assert.That(request.FileKey, Is.EqualTo("listing"));
        });
    }

    [Test]
    public void Should_allow_null_GroupId_and_FileKey()
    {
        var request = new ScrapeUrlRequest
        {
            Url = "https://ebay.com/itm/123"
        };

        Assert.Multiple(() =>
        {
            Assert.That(request.GroupId, Is.Null);
            Assert.That(request.FileKey, Is.Null);
        });
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ScrapeUrlRequest_UnitTests" -v n`

Expected: FAIL - `ScrapeUrlRequest` doesn't exist

**Step 3: Write minimal implementation**

Create `AIOMarketMaker.Core/Models/ScrapeUrlRequest.cs`:
```csharp
namespace AIOMarketMaker.Core.Models;

/// <summary>
/// Request to scrape a single URL with optional grouping metadata.
/// </summary>
public record ScrapeUrlRequest
{
    /// <summary>
    /// The URL to scrape.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Optional grouping identifier (e.g., listing ID).
    /// When provided with FileKey, determines blob path structure.
    /// </summary>
    public string? GroupId { get; init; }

    /// <summary>
    /// Optional file key (e.g., "listing" or "description").
    /// When provided with GroupId, determines blob file name.
    /// </summary>
    public string? FileKey { get; init; }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ScrapeUrlRequest_UnitTests" -v n`

Expected: PASS

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Models/ScrapeUrlRequest.cs AIOMarketMaker.Tests/Unit/Models/ScrapeUrlRequestTests.cs
git commit -m "feat: add ScrapeUrlRequest model for grouped scrape submissions"
```

---

### Task 2.3: Add DescriptionStatus to Listing model

**Files:**
- Modify: `<REPO_ROOT>\AIOMarketMaker.Core\Data\Models\Listing.cs`
- Create: `<REPO_ROOT>\AIOMarketMaker.Core\Data\Migrations\021_AddDescriptionStatusToListings.sql`

**Step 1: Add property to model**

Add to `Listing.cs` after line 28 (`Description` property):
```csharp
/// <summary>
/// Status of description fetching: pending, complete, missing, failed
/// </summary>
public string DescriptionStatus { get; set; } = "pending";
```

**Step 2: Create migration**

Create `021_AddDescriptionStatusToListings.sql`:
```sql
-- Migration: 021_AddDescriptionStatusToListings
-- Description: Adds DescriptionStatus column to track description fetch status
-- Date: 2026-01-27

ALTER TABLE Listings ADD COLUMN DescriptionStatus TEXT DEFAULT 'pending';

-- Update existing rows with descriptions to 'complete'
UPDATE Listings SET DescriptionStatus = 'complete' WHERE Description IS NOT NULL AND Description != '';

-- Update existing rows without descriptions to 'missing' (they were processed before this feature)
UPDATE Listings SET DescriptionStatus = 'missing' WHERE Description IS NULL OR Description = '';
```

**Step 3: Commit**

```bash
git add AIOMarketMaker.Core/Data/Models/Listing.cs AIOMarketMaker.Core/Data/Migrations/021_AddDescriptionStatusToListings.sql
git commit -m "feat: add DescriptionStatus to Listing model for tracking description fetch status"
```

---

## Phase 3: Convert ETL to Azure Functions

### Task 3.1: Add Azure Functions packages to ETL project

**Files:**
- Modify: `<REPO_ROOT>\AIOMarketMaker.Etl\AIOMarketMaker.Etl.csproj`

**Step 1: Update csproj**

Replace the contents with Azure Functions configuration:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <FunctionsEnableWorkerIndexing>false</FunctionsEnableWorkerIndexing>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Azure.Functions.Worker" Version="1.23.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.DurableTask" Version="1.1.7" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Storage.Blobs" Version="6.6.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Timer" Version="4.3.1" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="1.18.1" />
    <PackageReference Include="Microsoft.ApplicationInsights.WorkerService" Version="2.22.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.ApplicationInsights" Version="1.4.0" />
    <PackageReference Include="CsvHelper" Version="33.0.1" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.1" />
    <PackageReference Include="Serilog" Version="4.3.0" />
    <PackageReference Include="Azure.Storage.Blobs" Version="12.24.0" />
    <PackageReference Include="Serilog.Sinks.Console" Version="4.*" />
    <PackageReference Include="Serilog.AspNetCore" Version="6.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\AIOWebScraper\AIOWebScraper.Storage.Azure\AIOWebScraper.Storage.Azure.csproj" />
    <ProjectReference Include="..\AIOMarketMaker.Core\AIOMarketMaker.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="host.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
    <None Update="local.settings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <CopyToPublishDirectory>Never</CopyToPublishDirectory>
    </None>
  </ItemGroup>

  <ItemGroup>
    <None Update="Data\Migrations\*.sql">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

**Step 2: Create host.json**

Create `AIOMarketMaker.Etl/host.json`:
```json
{
  "version": "2.0",
  "logging": {
    "logLevel": {
      "default": "Information",
      "DurableTask.AzureStorage": "Warning",
      "DurableTask.Core": "Warning"
    },
    "applicationInsights": {
      "samplingSettings": {
        "isEnabled": true
      }
    }
  },
  "extensions": {
    "durableTask": {
      "storageProvider": {
        "type": "AzureStorage"
      }
    },
    "blobs": {
      "maxDegreeOfParallelism": 8
    }
  },
  "functionTimeout": "00:10:00"
}
```

**Step 3: Restore packages**

Run: `dotnet restore AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`

**Step 4: Commit**

```bash
git add AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj AIOMarketMaker.Etl/host.json
git commit -m "feat: convert ETL project to Azure Functions with Durable Functions support"
```

---

### Task 3.2: Create Program.cs for Azure Functions

**Files:**
- Modify: `<REPO_ROOT>\AIOMarketMaker.Etl\Program.cs`

**Step 1: Update Program.cs**

Replace contents with Azure Functions host builder:
```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AIOMarketMaker.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using ScraperWorker.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Configuration
        var config = context.Configuration;
        var blobConnectionString = config["blobStorageConnectionString"]
            ?? config["AzureWebJobsStorage"]
            ?? "UseDevelopmentStorage=true";
        var tableConnectionString = config["tableStorageConnectionString"]
            ?? config["AzureWebJobsStorage"]
            ?? "UseDevelopmentStorage=true";

        // Azure Storage clients
        services.AddSingleton(_ => new BlobServiceClient(blobConnectionString));
        services.AddSingleton(_ => new TableServiceClient(tableConnectionString));
        services.AddSingleton<IJobRepository, AzureJobRepository>();

        // Core services
        services.AddEbayScraperPipeline(config);
    })
    .Build();

host.Run();
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Etl/Program.cs
git commit -m "feat: update ETL Program.cs for Azure Functions host"
```

---

### Task 3.3: Create ListingEtlInput model

**Files:**
- Create: `<REPO_ROOT>\AIOMarketMaker.Etl\Models\ListingEtlInput.cs`

**Step 1: Create model**

```csharp
namespace AIOMarketMaker.Etl.Models;

public enum TriggerSource
{
    Listing,
    Description
}

public record ListingEtlInput(
    string JobId,
    string ListingId,
    TriggerSource TriggerSource
);

public record BlobState(
    bool HasListing,
    bool HasDescription,
    string? MissingBlob
)
{
    public bool HasBoth => HasListing && HasDescription;
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Etl/Models/ListingEtlInput.cs
git commit -m "feat: add ListingEtlInput and BlobState models for ETL orchestration"
```

---

### Task 3.4: Create blob triggers

**Files:**
- Create: `<REPO_ROOT>\AIOMarketMaker.Etl\Triggers\ListingBlobTrigger.cs`
- Create: `<REPO_ROOT>\AIOMarketMaker.Etl\Triggers\DescriptionBlobTrigger.cs`

**Step 1: Create ListingBlobTrigger**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Orchestrators;

namespace AIOMarketMaker.Etl.Triggers;

public class ListingBlobTrigger
{
    private readonly ILogger<ListingBlobTrigger> _logger;

    public ListingBlobTrigger(ILogger<ListingBlobTrigger> logger)
    {
        _logger = logger;
    }

    [Function("OnListingBlobCreated")]
    public async Task Run(
        [BlobTrigger("html/{jobId}/{listingId}/listing.html", Connection = "blobStorageConnectionString")] string html,
        [DurableClient] DurableTaskClient client,
        string jobId,
        string listingId)
    {
        var instanceId = $"etl-{jobId}-{listingId}";
        _logger.LogInformation("Listing blob trigger fired for {ListingId} in job {JobId}", listingId, jobId);

        var existingInstance = await client.GetInstanceAsync(instanceId);
        if (existingInstance == null)
        {
            _logger.LogInformation("Starting new orchestration {InstanceId}", instanceId);
            await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(ListingEtlOrchestrator),
                new ListingEtlInput(jobId, listingId, TriggerSource.Listing),
                new StartOrchestrationOptions { InstanceId = instanceId });
        }
        else
        {
            _logger.LogInformation("Orchestration {InstanceId} already exists, raising event", instanceId);
            await client.RaiseEventAsync(instanceId, "listing-ready", true);
        }
    }
}
```

**Step 2: Create DescriptionBlobTrigger**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Orchestrators;

namespace AIOMarketMaker.Etl.Triggers;

public class DescriptionBlobTrigger
{
    private readonly ILogger<DescriptionBlobTrigger> _logger;

    public DescriptionBlobTrigger(ILogger<DescriptionBlobTrigger> logger)
    {
        _logger = logger;
    }

    [Function("OnDescriptionBlobCreated")]
    public async Task Run(
        [BlobTrigger("html/{jobId}/{listingId}/description.html", Connection = "blobStorageConnectionString")] string html,
        [DurableClient] DurableTaskClient client,
        string jobId,
        string listingId)
    {
        var instanceId = $"etl-{jobId}-{listingId}";
        _logger.LogInformation("Description blob trigger fired for {ListingId} in job {JobId}", listingId, jobId);

        var existingInstance = await client.GetInstanceAsync(instanceId);
        if (existingInstance == null)
        {
            _logger.LogInformation("Starting new orchestration {InstanceId}", instanceId);
            await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(ListingEtlOrchestrator),
                new ListingEtlInput(jobId, listingId, TriggerSource.Description),
                new StartOrchestrationOptions { InstanceId = instanceId });
        }
        else
        {
            _logger.LogInformation("Orchestration {InstanceId} already exists, raising event", instanceId);
            await client.RaiseEventAsync(instanceId, "description-ready", true);
        }
    }
}
```

**Step 3: Commit**

```bash
git add AIOMarketMaker.Etl/Triggers/
git commit -m "feat: add blob triggers for listing and description HTML files"
```

---

### Task 3.5: Create CheckBlobsActivity

**Files:**
- Create: `<REPO_ROOT>\AIOMarketMaker.Etl\Activities\CheckBlobsActivity.cs`

**Step 1: Create activity**

```csharp
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

public class CheckBlobsActivity
{
    private readonly BlobServiceClient _blobService;
    private readonly ILogger<CheckBlobsActivity> _logger;

    public CheckBlobsActivity(BlobServiceClient blobService, ILogger<CheckBlobsActivity> logger)
    {
        _blobService = blobService;
        _logger = logger;
    }

    [Function(nameof(CheckBlobsActivity))]
    public async Task<BlobState> Run([ActivityTrigger] ListingEtlInput input)
    {
        var container = _blobService.GetBlobContainerClient("html");

        var listingBlobPath = $"{input.JobId}/{input.ListingId}/listing.html";
        var descriptionBlobPath = $"{input.JobId}/{input.ListingId}/description.html";

        var listingBlob = container.GetBlobClient(listingBlobPath);
        var descriptionBlob = container.GetBlobClient(descriptionBlobPath);

        var hasListing = await listingBlob.ExistsAsync();
        var hasDescription = await descriptionBlob.ExistsAsync();

        _logger.LogInformation(
            "Blob check for {ListingId}: listing={HasListing}, description={HasDescription}",
            input.ListingId, hasListing.Value, hasDescription.Value);

        string? missingBlob = null;
        if (!hasListing.Value) missingBlob = "listing";
        else if (!hasDescription.Value) missingBlob = "description";

        return new BlobState(hasListing.Value, hasDescription.Value, missingBlob);
    }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Etl/Activities/CheckBlobsActivity.cs
git commit -m "feat: add CheckBlobsActivity to verify blob existence"
```

---

### Task 3.6: Create ProcessListingActivity

**Files:**
- Create: `<REPO_ROOT>\AIOMarketMaker.Etl\Activities\ProcessListingActivity.cs`

**Step 1: Create activity**

```csharp
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Etl.Models;
using AngleSharp;
using AngleSharp.Html.Parser;

namespace AIOMarketMaker.Etl.Activities;

public class ProcessListingActivity
{
    private readonly BlobServiceClient _blobService;
    private readonly EtlDbContext _dbContext;
    private readonly IEbayListingParser _listingParser;
    private readonly ILogger<ProcessListingActivity> _logger;

    public ProcessListingActivity(
        BlobServiceClient blobService,
        EtlDbContext dbContext,
        IEbayListingParser listingParser,
        ILogger<ProcessListingActivity> logger)
    {
        _blobService = blobService;
        _dbContext = dbContext;
        _listingParser = listingParser;
        _logger = logger;
    }

    [Function(nameof(ProcessListingActivity))]
    public async Task Run([ActivityTrigger] ProcessListingInput input)
    {
        var container = _blobService.GetBlobContainerClient("html");

        // Fetch listing HTML (required)
        var listingBlobPath = $"{input.JobId}/{input.ListingId}/listing.html";
        var listingBlob = container.GetBlobClient(listingBlobPath);
        var listingContent = await listingBlob.DownloadContentAsync();
        var listingHtml = listingContent.Value.Content.ToString();

        // Parse listing
        var parser = new HtmlParser();
        var listingDoc = await parser.ParseDocumentAsync(listingHtml);
        var extractedListing = _listingParser.ParseProductListing(listingDoc, $"https://ebay.com/itm/{input.ListingId}");

        // Try to fetch description (optional)
        string? description = null;
        var descriptionStatus = "missing";

        if (input.HasDescription)
        {
            try
            {
                var descBlobPath = $"{input.JobId}/{input.ListingId}/description.html";
                var descBlob = container.GetBlobClient(descBlobPath);
                var descContent = await descBlob.DownloadContentAsync();
                var descHtml = descContent.Value.Content.ToString();

                var descDoc = await parser.ParseDocumentAsync(descHtml);
                description = _listingParser.ParseDescription(descDoc);
                descriptionStatus = string.IsNullOrEmpty(description) ? "failed" : "complete";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse description for {ListingId}", input.ListingId);
                descriptionStatus = "failed";
            }
        }

        // Save to database
        var existing = await _dbContext.Listings.FindAsync(input.ListingId);
        if (existing != null)
        {
            // Update existing
            existing.Title = extractedListing.title;
            existing.Price = extractedListing.price;
            existing.Description = description;
            existing.DescriptionStatus = descriptionStatus;
            existing.UpdatedUtc = DateTime.UtcNow;
        }
        else
        {
            // Insert new
            var listing = new Listing
            {
                ListingId = input.ListingId,
                ScrapeJobId = input.ScrapeJobId,
                Title = extractedListing.title,
                Price = extractedListing.price,
                Currency = extractedListing.currency,
                Description = description,
                DescriptionStatus = descriptionStatus,
                Url = extractedListing.url,
                Condition = extractedListing.condition?.ToString(),
                CreatedUtc = DateTime.UtcNow
            };
            _dbContext.Listings.Add(listing);
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation(
            "Processed listing {ListingId}: descriptionStatus={Status}",
            input.ListingId, descriptionStatus);
    }
}

public record ProcessListingInput(
    string JobId,
    string ListingId,
    int ScrapeJobId,
    bool HasDescription
);
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Etl/Activities/ProcessListingActivity.cs
git commit -m "feat: add ProcessListingActivity for parsing and saving listings"
```

---

### Task 3.7: Create ListingEtlOrchestrator

**Files:**
- Create: `<REPO_ROOT>\AIOMarketMaker.Etl\Orchestrators\ListingEtlOrchestrator.cs`

**Step 1: Create orchestrator**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Orchestrators;

public class ListingEtlOrchestrator
{
    private const int TimeoutMinutes = 5;

    [Function(nameof(ListingEtlOrchestrator))]
    public async Task Run([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<ListingEtlOrchestrator>();
        var input = context.GetInput<ListingEtlInput>()!;

        logger.LogInformation(
            "Starting ETL orchestration for listing {ListingId} (triggered by {Source})",
            input.ListingId, input.TriggerSource);

        // Check what blobs exist
        var state = await context.CallActivityAsync<BlobState>(
            nameof(CheckBlobsActivity), input);

        // Wait for partner if needed
        if (!state.HasBoth)
        {
            logger.LogInformation(
                "Waiting for {MissingBlob} blob for listing {ListingId}",
                state.MissingBlob, input.ListingId);

            var timeout = context.CurrentUtcDateTime.AddMinutes(TimeoutMinutes);
            using var cts = new CancellationTokenSource();

            var timeoutTask = context.CreateTimer(timeout, cts.Token);
            var eventName = state.MissingBlob == "listing" ? "listing-ready" : "description-ready";
            var partnerEvent = context.WaitForExternalEvent<bool>(eventName);

            var winner = await Task.WhenAny(timeoutTask, partnerEvent);

            if (winner == partnerEvent)
            {
                cts.Cancel(); // Cancel the timer
                logger.LogInformation("Partner blob arrived for listing {ListingId}", input.ListingId);

                // Re-check blob state after event
                state = await context.CallActivityAsync<BlobState>(
                    nameof(CheckBlobsActivity), input);
            }
            else
            {
                logger.LogWarning(
                    "Timeout waiting for {MissingBlob} blob for listing {ListingId}",
                    state.MissingBlob, input.ListingId);
            }
        }

        // Process listing (with or without description)
        var processInput = new ProcessListingInput(
            input.JobId,
            input.ListingId,
            ScrapeJobId: 0, // TODO: Get from job metadata
            HasDescription: state.HasDescription
        );

        await context.CallActivityAsync(nameof(ProcessListingActivity), processInput);

        logger.LogInformation(
            "ETL orchestration completed for listing {ListingId} (hasDescription={HasDescription})",
            input.ListingId, state.HasDescription);
    }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Etl/Orchestrators/ListingEtlOrchestrator.cs
git commit -m "feat: add ListingEtlOrchestrator with partner wait and timeout logic"
```

---

## Phase 4: Strip Down Functions Project to HTTP API Only

### Task 4.1: Remove Durable Functions from AIOMarketMaker.Functions

**Files:**
- Modify: `<REPO_ROOT>\AIOMarketMaker.Functions\AIOMarketMaker.Functions.csproj`

**Step 1: Remove DurableTask package**

Update csproj to remove Durable Functions while keeping HTTP:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <FunctionsEnableWorkerIndexing>false</FunctionsEnableWorkerIndexing>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Azure.Functions.Worker" Version="1.23.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http" Version="3.2.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="1.18.1" />
    <PackageReference Include="Microsoft.ApplicationInsights.WorkerService" Version="2.22.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.ApplicationInsights" Version="1.4.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="8.0.11" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\AIOMarketMaker.Core\AIOMarketMaker.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="host.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
    <None Update="local.settings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <CopyToPublishDirectory>Never</CopyToPublishDirectory>
    </None>
  </ItemGroup>
</Project>
```

**Step 2: Move/archive existing orchestrators**

Create `_archived` folder and move orchestrator files there (or delete them):
```bash
mkdir -p AIOMarketMaker.Functions/_archived
mv AIOMarketMaker.Functions/Functions/Orchestrators/* AIOMarketMaker.Functions/_archived/
mv AIOMarketMaker.Functions/Activities/* AIOMarketMaker.Functions/_archived/
```

**Step 3: Create simple HTTP API endpoints**

Create `AIOMarketMaker.Functions/Functions/ApiEndpoints.cs`:
```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;

namespace AIOMarketMaker.Functions.Functions;

public class ApiEndpoints
{
    private readonly ILogger<ApiEndpoints> _logger;

    public ApiEndpoints(ILogger<ApiEndpoints> logger)
    {
        _logger = logger;
    }

    [Function("GetHealth")]
    public HttpResponseData GetHealth(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.WriteString("Healthy");
        return response;
    }

    [Function("GetJobStatus")]
    public async Task<HttpResponseData> GetJobStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "jobs/{jobId}/status")] HttpRequestData req,
        string jobId)
    {
        _logger.LogInformation("Getting status for job {JobId}", jobId);

        // TODO: Implement job status lookup from Table Storage
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { jobId, status = "unknown" });
        return response;
    }
}
```

**Step 4: Commit**

```bash
git add AIOMarketMaker.Functions/
git commit -m "refactor: strip Functions project to HTTP API only, move Durable Functions to ETL"
```

---

## Phase 5: Integration Testing

### Task 5.1: Create local integration test

**Files:**
- Create: `<REPO_ROOT>\AIOMarketMaker.Tests\Integration\EtlBlobTriggerIntegrationTests.cs`

**Step 1: Create test**

```csharp
using NUnit.Framework;
using Azure.Storage.Blobs;

namespace AIOMarketMaker.Tests.Integration;

[TestFixture]
[Category("Integration")]
[Explicit("Requires Azurite running locally")]
public class EtlBlobTriggerIntegrationTests
{
    private BlobServiceClient _blobService = null!;

    [SetUp]
    public void SetUp()
    {
        _blobService = new BlobServiceClient("UseDevelopmentStorage=true");
    }

    [Test]
    public async Task Should_create_blobs_with_grouped_path_structure()
    {
        // Arrange
        var container = _blobService.GetBlobContainerClient("html");
        await container.CreateIfNotExistsAsync();

        var jobId = Guid.NewGuid().ToString("N");
        var listingId = "123456789";

        // Act
        var listingBlob = container.GetBlobClient($"{jobId}/{listingId}/listing.html");
        var descriptionBlob = container.GetBlobClient($"{jobId}/{listingId}/description.html");

        await listingBlob.UploadAsync(BinaryData.FromString("<html>listing</html>"), overwrite: true);
        await descriptionBlob.UploadAsync(BinaryData.FromString("<html>description</html>"), overwrite: true);

        // Assert
        Assert.Multiple(async () =>
        {
            Assert.That((await listingBlob.ExistsAsync()).Value, Is.True);
            Assert.That((await descriptionBlob.ExistsAsync()).Value, Is.True);
        });

        // Cleanup
        await listingBlob.DeleteIfExistsAsync();
        await descriptionBlob.DeleteIfExistsAsync();
    }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Tests/Integration/EtlBlobTriggerIntegrationTests.cs
git commit -m "test: add integration test for grouped blob path structure"
```

---

## Verification Checklist

Before marking complete, verify:

1. **Unit tests pass:**
   ```bash
   dotnet test AIOWebScraper/AIOWebScraper.Tests --filter Category=Unit
   dotnet test AIOMarketMaker/AIOMarketMaker.Tests --filter Category=Unit
   ```

2. **Build succeeds:**
   ```bash
   dotnet build AIOWebScraper/AIOWebScraper.sln
   dotnet build AIOMarketMaker/AIOMarketMaker.sln
   ```

3. **Local functions start:**
   ```bash
   # Start Azurite first
   npx azurite --blobPort 10000 --queuePort 10001 --tablePort 10002

   # Start ETL Functions
   cd AIOMarketMaker/AIOMarketMaker.Etl && func start
   ```

4. **Blob triggers detected:**
   - Check func start output shows `OnListingBlobCreated` and `OnDescriptionBlobCreated`

---

## Summary

| Phase | Tasks | Commits |
|-------|-------|---------|
| 1. Scraper Changes | 1.1-1.4 | 4 |
| 2. Market Maker Core | 2.1-2.3 | 3 |
| 3. Convert ETL to Functions | 3.1-3.7 | 7 |
| 4. Strip Functions Project | 4.1 | 1 |
| 5. Integration Testing | 5.1 | 1 |

**Total estimated commits:** 16
