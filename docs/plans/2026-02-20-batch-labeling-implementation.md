# Batch Labeling Pipeline — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a C# batch labeling pipeline that relabels 143K v8 training pairs via the OpenAI Batch API at ~$5.57, producing a cleaner training set for the v10 ONNX variant classifier.

**Architecture:** A `BatchLabeler` service in `AIOMarketMaker.ML` handles JSONL generation, batch submission, polling, and result merging. A `--batch-label` command-line mode in `AIOMarketMaker.Etl/Program.cs` orchestrates the pipeline. Uses `OpenAI.Batch.BatchClient` and `OpenAI.Files.OpenAIFileClient` from the existing OpenAI SDK v2.8.0.

**Tech Stack:** .NET 8.0, OpenAI SDK v2.8.0 (`OpenAI.Batch`, `OpenAI.Files`), System.Text.Json for JSONL serialization, existing `LlmVariantClassifier.Prompt.cs` for the system prompt.

---

### Task 1: Create BatchLabeler with JSONL Generation

**Files:**
- Create: `AIOMarketMaker/AIOMarketMaker.ML/Services/BatchLabeler.cs`
- Test: `AIOMarketMaker/AIOMarketMaker.Tests.Unit/Services/BatchLabeler_UnitTests.cs`

**Step 1: Write the failing test for JSONL line generation**

```csharp
// BatchLabeler_UnitTests.cs
using System.Text.Json;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Services;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class BatchLabeler_UnitTests
{
    [Test]
    public void Should_generate_valid_jsonl_line_for_pair()
    {
        var pair = new ClassifyPairRequest(
            "iPhone 15 Pro Max 256GB",
            "Brand new sealed iPhone 15 Pro Max",
            "Apple iPhone 15 Pro Max 256GB Black Titanium",
            "Apple iPhone 15 Pro Max 256GB in Black Titanium, factory unlocked");

        var line = BatchLabeler.BuildBatchRequestLine("pair-42", pair);
        var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("custom_id").GetString(), Is.EqualTo("pair-42"));
            Assert.That(root.GetProperty("method").GetString(), Is.EqualTo("POST"));
            Assert.That(root.GetProperty("url").GetString(), Is.EqualTo("/v1/chat/completions"));

            var body = root.GetProperty("body");
            Assert.That(body.GetProperty("model").GetString(), Is.EqualTo("gpt-5-mini"));

            var messages = body.GetProperty("messages");
            Assert.That(messages.GetArrayLength(), Is.EqualTo(2));
            Assert.That(messages[0].GetProperty("role").GetString(), Is.EqualTo("system"));
            Assert.That(messages[1].GetProperty("role").GetString(), Is.EqualTo("user"));
            Assert.That(messages[1].GetProperty("content").GetString(), Does.Contain("iPhone 15 Pro Max 256GB"));

            var responseFormat = body.GetProperty("response_format");
            Assert.That(responseFormat.GetProperty("type").GetString(), Is.EqualTo("json_schema"));
        });
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit --filter "Should_generate_valid_jsonl_line_for_pair" --no-restore`
Expected: FAIL — `BatchLabeler` class does not exist

**Step 3: Write minimal implementation**

```csharp
// BatchLabeler.cs
using System.Text.Json;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Utils;

namespace AIOMarketMaker.ML.Services;

public class BatchLabeler
{
    private const string Model = "gpt-5-mini";
    private const int MaxDescriptionLength = 500;

    private static readonly string ResponseSchema = JsonSchemaGenerator.Generate<ClassifierResponse>();

    /// <summary>
    /// Builds a single JSONL line for the OpenAI Batch API.
    /// </summary>
    public static string BuildBatchRequestLine(string customId, ClassifyPairRequest pair)
    {
        var userPrompt = LlmVariantClassifier.BuildUserPrompt(pair);

        var request = new
        {
            custom_id = customId,
            method = "POST",
            url = "/v1/chat/completions",
            body = new
            {
                model = Model,
                messages = new object[]
                {
                    new { role = "system", content = LlmVariantClassifier.SystemPromptText },
                    new { role = "user", content = userPrompt }
                },
                response_format = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "ClassifierResponse",
                        strict = true,
                        schema = JsonSerializer.Deserialize<JsonElement>(ResponseSchema)
                    }
                }
            }
        };

        return JsonSerializer.Serialize(request);
    }
}
```

This requires exposing the system prompt text. In `LlmVariantClassifier.Prompt.cs`, change:

```csharp
// Change from:
private static readonly string SystemPrompt = $"""
// To:
internal static readonly string SystemPromptText = $"""
```

And update the reference in `LlmVariantClassifier.cs`:

```csharp
// ClassifyOne method, line 66:
var response = await WithRetry(() => _client.CompleteChat<ClassifierResponse>(SystemPromptText, userPrompt, ct), ct);
```

**Step 4: Run test to verify it passes**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit --filter "Should_generate_valid_jsonl_line_for_pair" --no-restore`
Expected: PASS

**Step 5: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.ML/Services/BatchLabeler.cs \
       AIOMarketMaker/AIOMarketMaker.ML/Services/LlmVariantClassifier.Prompt.cs \
       AIOMarketMaker/AIOMarketMaker.ML/Services/LlmVariantClassifier.cs \
       AIOMarketMaker/AIOMarketMaker.Tests.Unit/Services/BatchLabeler_UnitTests.cs
git commit -m "feat: add BatchLabeler with JSONL line generation for OpenAI Batch API"
```

---

### Task 2: Add CSV-to-JSONL File Generation

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.ML/Services/BatchLabeler.cs`
- Test: `AIOMarketMaker/AIOMarketMaker.Tests.Unit/Services/BatchLabeler_UnitTests.cs`

**Step 1: Write the failing test**

```csharp
[Test]
public async Task Should_generate_jsonl_file_from_csv_rows()
{
    var csvContent = """
        anchor_id,neighbor_id,job_id,product_name,anchor_title,neighbor_title,anchor_desc,neighbor_desc,label,confidence,reasoning,source
        111,222,1,PS5 Console,Sony PS5 Digital,PlayStation 5 Digital Edition,Brand new PS5,New PS5 console,1,high,Both PS5 digital,v5_original
        333,444,2,iPhone 15,iPhone 15 Pro Max,Apple iPhone 15 Pro Max,Good condition,Excellent condition,0,high,Different condition,v7_mined
        """;

    var csvPath = Path.GetTempFileName();
    var outputPath = Path.ChangeExtension(Path.GetTempFileName(), ".jsonl");
    await File.WriteAllTextAsync(csvPath, csvContent);

    try
    {
        var count = await BatchLabeler.GenerateBatchInput(csvPath, outputPath);

        Assert.That(count, Is.EqualTo(2));
        var lines = await File.ReadAllLinesAsync(outputPath);
        Assert.That(lines, Has.Length.EqualTo(2));

        // Verify custom_ids are sequential
        var doc0 = JsonDocument.Parse(lines[0]);
        var doc1 = JsonDocument.Parse(lines[1]);
        Assert.That(doc0.RootElement.GetProperty("custom_id").GetString(), Is.EqualTo("pair-0"));
        Assert.That(doc1.RootElement.GetProperty("custom_id").GetString(), Is.EqualTo("pair-1"));
    }
    finally
    {
        File.Delete(csvPath);
        File.Delete(outputPath);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit --filter "Should_generate_jsonl_file_from_csv_rows"`
Expected: FAIL — `GenerateBatchInput` method does not exist

**Step 3: Write minimal implementation**

Add to `BatchLabeler.cs`:

```csharp
/// <summary>
/// Reads a v8 CSV file and writes a JSONL batch input file.
/// Returns the number of pairs written.
/// </summary>
public static async Task<int> GenerateBatchInput(string csvPath, string outputPath)
{
    var count = 0;
    using var reader = new StreamReader(csvPath);
    using var writer = new StreamWriter(outputPath);

    // Skip header
    var header = await reader.ReadLineAsync();
    if (header is null)
    {
        return 0;
    }

    var columnIndices = ParseHeader(header);

    while (await reader.ReadLineAsync() is { } line)
    {
        var fields = ParseCsvLine(line);
        if (fields.Length < columnIndices.NeighborDesc + 1)
        {
            continue;
        }

        var pair = new ClassifyPairRequest(
            TitleA: fields[columnIndices.AnchorTitle],
            DescriptionA: CleanField(fields[columnIndices.AnchorDesc]),
            TitleB: fields[columnIndices.NeighborTitle],
            DescriptionB: CleanField(fields[columnIndices.NeighborDesc]));

        var jsonLine = BuildBatchRequestLine($"pair-{count}", pair);
        await writer.WriteLineAsync(jsonLine);
        count++;
    }

    return count;
}

private record CsvColumnIndices(
    int AnchorTitle,
    int NeighborTitle,
    int AnchorDesc,
    int NeighborDesc);

private static CsvColumnIndices ParseHeader(string header)
{
    var columns = header.Split(',');
    return new CsvColumnIndices(
        AnchorTitle: Array.IndexOf(columns, "anchor_title"),
        NeighborTitle: Array.IndexOf(columns, "neighbor_title"),
        AnchorDesc: Array.IndexOf(columns, "anchor_desc"),
        NeighborDesc: Array.IndexOf(columns, "neighbor_desc"));
}

private static string[] ParseCsvLine(string line)
{
    var fields = new List<string>();
    var inQuotes = false;
    var field = new System.Text.StringBuilder();

    for (var i = 0; i < line.Length; i++)
    {
        var c = line[i];
        if (c == '"')
        {
            if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
            {
                field.Append('"');
                i++;
            }
            else
            {
                inQuotes = !inQuotes;
            }
        }
        else if (c == ',' && !inQuotes)
        {
            fields.Add(field.ToString());
            field.Clear();
        }
        else
        {
            field.Append(c);
        }
    }
    fields.Add(field.ToString());
    return fields.ToArray();
}

private static string CleanField(string field)
{
    if (string.IsNullOrEmpty(field) || field == "nan")
    {
        return "";
    }
    return field;
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit --filter "Should_generate_jsonl_file_from_csv_rows"`
Expected: PASS

**Step 5: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.ML/Services/BatchLabeler.cs \
       AIOMarketMaker/AIOMarketMaker.Tests.Unit/Services/BatchLabeler_UnitTests.cs
git commit -m "feat: add CSV-to-JSONL generation for batch labeling pipeline"
```

---

### Task 3: Add Batch Submission and Polling

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.ML/Services/BatchLabeler.cs`

**Step 1: Add batch submission, polling, and result download methods**

These methods wrap the OpenAI SDK's `BatchClient` and `OpenAIFileClient`. They are not unit-testable (external API) so we write them directly and test via integration.

Add to `BatchLabeler.cs`:

```csharp
using OpenAI;
using OpenAI.Batch;
using OpenAI.Files;
using Microsoft.Extensions.Logging;

// Add constructor and instance methods to BatchLabeler
// (keep existing static methods)

public class BatchLabeler
{
    private const string Model = "gpt-5-mini";
    private const string StateFileName = "batch_state.json";

    private readonly OpenAIClient _openAiClient;
    private readonly ILogger<BatchLabeler> _logger;

    public BatchLabeler(string apiKey, ILogger<BatchLabeler> logger)
    {
        _openAiClient = new OpenAIClient(apiKey);
        _logger = logger;
    }

    // ... existing static methods (BuildBatchRequestLine, GenerateBatchInput, etc.) ...

    /// <summary>
    /// Uploads the JSONL file and submits a batch job.
    /// Saves batch_id to a state file for resumability.
    /// </summary>
    public async Task<string> SubmitBatch(string jsonlPath, string workingDir)
    {
        var fileClient = _openAiClient.GetOpenAIFileClient();
        var batchClient = _openAiClient.GetBatchClient();

        _logger.LogInformation("Uploading {Path} to OpenAI Files API...", jsonlPath);
        var fileResult = await fileClient.UploadFileAsync(jsonlPath, FileUploadPurpose.Batch);
        var fileId = fileResult.Value.Id;
        _logger.LogInformation("Uploaded file: {FileId}", fileId);

        _logger.LogInformation("Creating batch job...");
        var batch = await batchClient.CreateBatchAsync(
            fileId,
            "/v1/chat/completions",
            new BatchCreationOptions { Metadata = { ["description"] = "v10 training relabel" } });

        var batchId = batch.Value.Id;
        _logger.LogInformation("Batch created: {BatchId}", batchId);

        // Save state for resumability
        var statePath = Path.Combine(workingDir, StateFileName);
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new { batchId, fileId }));

        return batchId;
    }

    /// <summary>
    /// Checks batch status once (non-blocking). Used by the 'status' CLI command.
    /// </summary>
    public async Task<BatchStatusResult> GetBatchStatus(string batchId)
    {
        var batchClient = _openAiClient.GetBatchClient();
        var batch = await batchClient.GetBatchAsync(batchId);

        var status = batch.Value.Status.ToString();
        var completed = batch.Value.RequestCounts?.CompletedCount ?? 0;
        var failed = batch.Value.RequestCounts?.FailedCount ?? 0;
        var total = batch.Value.RequestCounts?.TotalCount ?? 0;
        var isTerminal = batch.Value.Status is BatchStatus.Completed
            or BatchStatus.Failed or BatchStatus.Expired or BatchStatus.Cancelled;
        var outputFileId = batch.Value.OutputFileId;

        return new BatchStatusResult(status, completed, failed, total, isTerminal, outputFileId);
    }

    public record BatchStatusResult(
        string Status,
        int Completed,
        int Failed,
        int Total,
        bool IsTerminal,
        string? OutputFileId);

    /// <summary>
    /// Downloads the batch output JSONL file.
    /// </summary>
    public async Task<string> DownloadResults(string outputFileId, string outputPath)
    {
        var fileClient = _openAiClient.GetOpenAIFileClient();

        _logger.LogInformation("Downloading results file {FileId}...", outputFileId);
        var content = await fileClient.DownloadFileAsync(outputFileId);

        await using var fileStream = File.Create(outputPath);
        await content.Value.ToStream().CopyToAsync(fileStream);

        _logger.LogInformation("Results saved to {Path}", outputPath);
        return outputPath;
    }
}
```

**Step 2: Build to verify compilation**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.ML --no-restore`
Expected: Build succeeded

Note: The exact `BatchClient` and `OpenAIFileClient` API surface may differ slightly from what's shown above. Adjust method signatures based on IntelliSense / compilation errors. The key methods to find:
- `OpenAIClient.GetBatchClient()` or `new BatchClient(apiKey)`
- `OpenAIClient.GetOpenAIFileClient()` or `new OpenAIFileClient(apiKey)`
- `UploadFileAsync` with `FileUploadPurpose.Batch`
- `CreateBatchAsync` with input file ID and endpoint
- `GetBatchAsync` returning status/counts
- `DownloadFileAsync` returning file content

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.ML/Services/BatchLabeler.cs
git commit -m "feat: add batch submission, polling, and result download"
```

---

### Task 4: Add Result Merging and Analysis

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.ML/Services/BatchLabeler.cs`
- Test: `AIOMarketMaker/AIOMarketMaker.Tests.Unit/Services/BatchLabeler_UnitTests.cs`

**Step 1: Write the failing test for response parsing**

```csharp
[Test]
public void Should_parse_batch_output_line()
{
    var outputLine = """
        {"id":"batch_req_abc","custom_id":"pair-7","response":{"status_code":200,"body":{"choices":[{"message":{"content":"{\"reason\":\"Same product and condition\",\"verdict\":\"same\"}"}}]}}}
        """;

    var result = BatchLabeler.ParseBatchOutputLine(outputLine);

    Assert.Multiple(() =>
    {
        Assert.That(result.CustomId, Is.EqualTo("pair-7"));
        Assert.That(result.Index, Is.EqualTo(7));
        Assert.That(result.Verdict, Is.EqualTo("same"));
        Assert.That(result.Reason, Is.EqualTo("Same product and condition"));
        Assert.That(result.Error, Is.Null);
    });
}

[Test]
public void Should_handle_failed_batch_output_line()
{
    var outputLine = """
        {"id":"batch_req_abc","custom_id":"pair-3","response":{"status_code":429,"body":{"error":{"message":"Rate limit exceeded"}}}}
        """;

    var result = BatchLabeler.ParseBatchOutputLine(outputLine);

    Assert.Multiple(() =>
    {
        Assert.That(result.CustomId, Is.EqualTo("pair-3"));
        Assert.That(result.Index, Is.EqualTo(3));
        Assert.That(result.Verdict, Is.Null);
        Assert.That(result.Error, Does.Contain("Rate limit"));
    });
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit --filter "Should_parse_batch_output_line|Should_handle_failed_batch_output_line"`
Expected: FAIL

**Step 3: Write implementation**

Add to `BatchLabeler.cs`:

```csharp
public record BatchOutputResult(
    string CustomId,
    int Index,
    string? Verdict,
    string? Reason,
    string? Error);

public static BatchOutputResult ParseBatchOutputLine(string line)
{
    var doc = JsonDocument.Parse(line);
    var root = doc.RootElement;

    var customId = root.GetProperty("custom_id").GetString()!;
    var index = int.Parse(customId.Replace("pair-", ""));

    var response = root.GetProperty("response");
    var statusCode = response.GetProperty("status_code").GetInt32();

    if (statusCode != 200)
    {
        var errorMsg = response.GetProperty("body")
            .GetProperty("error")
            .GetProperty("message")
            .GetString();
        return new BatchOutputResult(customId, index, null, null, errorMsg);
    }

    var content = response.GetProperty("body")
        .GetProperty("choices")[0]
        .GetProperty("message")
        .GetProperty("content")
        .GetString()!;

    var parsed = JsonSerializer.Deserialize<ClassifierResponse>(content);
    var verdict = parsed?.Verdict switch
    {
        ML.Services.Verdict.Same => "same",
        ML.Services.Verdict.Different => "different",
        ML.Services.Verdict.Uncertain => "uncertain",
        _ => null
    };

    return new BatchOutputResult(customId, index, verdict, parsed?.Reason, null);
}

/// <summary>
/// Merges batch output with original CSV to produce labeled_pairs_v10.csv.
/// </summary>
public static async Task<MergeResult> MergeResults(
    string originalCsvPath, string batchOutputPath, string outputCsvPath)
{
    // Parse all batch output lines
    var results = new Dictionary<int, BatchOutputResult>();
    foreach (var line in await File.ReadAllLinesAsync(batchOutputPath))
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }
        var result = ParseBatchOutputLine(line);
        results[result.Index] = result;
    }

    var agreed = 0;
    var disagreed = 0;
    var errors = 0;
    var total = 0;

    using var reader = new StreamReader(originalCsvPath);
    await using var writer = new StreamWriter(outputCsvPath);

    // Write output header
    await writer.WriteLineAsync(
        "anchor_id,neighbor_id,job_id,product_name,anchor_title,neighbor_title,anchor_desc,neighbor_desc,label,confidence,reasoning,source,onnx_label");

    // Skip input header
    var header = await reader.ReadLineAsync();
    if (header is null)
    {
        return new MergeResult(0, 0, 0, 0);
    }

    var columnIndices = ParseHeader(header);
    var allColumns = header.Split(',');
    var labelIdx = Array.IndexOf(allColumns, "label");

    var rowIndex = 0;
    while (await reader.ReadLineAsync() is { } line)
    {
        var fields = ParseCsvLine(line);

        if (results.TryGetValue(rowIndex, out var result) && result.Verdict is not null)
        {
            var llmLabel = result.Verdict == "same" ? 1 : 0;
            var onnxLabel = int.TryParse(fields[labelIdx], out var v) ? v : 0;
            var confidence = result.Verdict == "uncertain" ? "low" : "high";

            if (llmLabel == onnxLabel) { agreed++; } else { disagreed++; }

            // Write merged row: keep original fields, replace label/confidence/reasoning/source, add onnx_label
            var anchorId = fields[Array.IndexOf(allColumns, "anchor_id")];
            var neighborId = fields[Array.IndexOf(allColumns, "neighbor_id")];
            var jobId = fields[Array.IndexOf(allColumns, "job_id")];
            var productName = CsvEscape(fields[Array.IndexOf(allColumns, "product_name")]);
            var anchorTitle = CsvEscape(fields[columnIndices.AnchorTitle]);
            var neighborTitle = CsvEscape(fields[columnIndices.NeighborTitle]);
            var anchorDesc = CsvEscape(CleanField(fields[columnIndices.AnchorDesc]));
            var neighborDesc = CsvEscape(CleanField(fields[columnIndices.NeighborDesc]));
            var reasoning = CsvEscape(result.Reason ?? "");

            await writer.WriteLineAsync(
                $"{anchorId},{neighborId},{jobId},{productName},{anchorTitle},{neighborTitle},{anchorDesc},{neighborDesc},{llmLabel},{confidence},{reasoning},llm_gpt5mini_batch,{onnxLabel}");
        }
        else
        {
            errors++;
        }

        rowIndex++;
        total++;
    }

    return new MergeResult(total, agreed, disagreed, errors);
}

public record MergeResult(int Total, int Agreed, int Disagreed, int Errors);

private static string CsvEscape(string field)
{
    if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
    {
        return $"\"{field.Replace("\"", "\"\"")}\"";
    }
    return field;
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit --filter "Should_parse_batch_output"`
Expected: PASS

**Step 5: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.ML/Services/BatchLabeler.cs \
       AIOMarketMaker/AIOMarketMaker.Tests.Unit/Services/BatchLabeler_UnitTests.cs
git commit -m "feat: add batch output parsing and CSV merge for v10 training data"
```

---

### Task 5: Add Disagreement Analysis

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.ML/Services/BatchLabeler.cs`

**Step 1: Add analysis method**

```csharp
/// <summary>
/// Reads the merged CSV and prints disagreement statistics by product category.
/// </summary>
public static async Task AnalyzeDisagreements(string mergedCsvPath)
{
    var byCategory = new Dictionary<string, (int Agreed, int Disagreed, int OnnxSame, int LlmSame)>();
    var totalAgreed = 0;
    var totalDisagreed = 0;

    using var reader = new StreamReader(mergedCsvPath);
    var header = await reader.ReadLineAsync();
    if (header is null) { return; }

    var columns = header.Split(',');
    var productIdx = Array.IndexOf(columns, "product_name");
    var labelIdx = Array.IndexOf(columns, "label");
    var onnxIdx = Array.IndexOf(columns, "onnx_label");

    while (await reader.ReadLineAsync() is { } line)
    {
        var fields = ParseCsvLine(line);
        if (fields.Length <= onnxIdx) { continue; }

        var product = fields[productIdx];
        var llmLabel = int.TryParse(fields[labelIdx], out var l) ? l : 0;
        var onnxLabel = int.TryParse(fields[onnxIdx], out var o) ? o : 0;

        if (!byCategory.ContainsKey(product))
        {
            byCategory[product] = (0, 0, 0, 0);
        }

        var cat = byCategory[product];
        if (llmLabel == onnxLabel)
        {
            byCategory[product] = (cat.Agreed + 1, cat.Disagreed, cat.OnnxSame + (onnxLabel == 1 ? 1 : 0), cat.LlmSame + (llmLabel == 1 ? 1 : 0));
            totalAgreed++;
        }
        else
        {
            byCategory[product] = (cat.Agreed, cat.Disagreed + 1, cat.OnnxSame + (onnxLabel == 1 ? 1 : 0), cat.LlmSame + (llmLabel == 1 ? 1 : 0));
            totalDisagreed++;
        }
    }

    Console.WriteLine($"\nOverall: {totalAgreed}/{totalAgreed + totalDisagreed} agreed ({100.0 * totalAgreed / (totalAgreed + totalDisagreed):F1}%)");
    Console.WriteLine($"\nTop disagreement categories:");
    Console.WriteLine($"{"Category",-35} {"Agree",-8} {"Disagree",-10} {"Rate",-8} {"ONNX=Same",-10} {"LLM=Same",-10}");
    Console.WriteLine(new string('-', 81));

    foreach (var (cat, stats) in byCategory
        .OrderByDescending(x => x.Value.Disagreed)
        .Take(30))
    {
        var total = stats.Agreed + stats.Disagreed;
        var rate = 100.0 * stats.Agreed / total;
        Console.WriteLine($"{cat,-35} {stats.Agreed,-8} {stats.Disagreed,-10} {rate,-7:F1}% {stats.OnnxSame,-10} {stats.LlmSame,-10}");
    }
}
```

**Step 2: Build to verify compilation**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.ML --no-restore`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.ML/Services/BatchLabeler.cs
git commit -m "feat: add disagreement analysis by product category"
```

---

### Task 6: Add --batch-label Mode to ETL Program.cs

Two commands only — no long-running scripts:
- `--batch-label start` — generate JSONL, upload, submit batch, save state, exit immediately
- `--batch-label status` — check batch progress once, if complete prompt user to download + merge + analyze

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Program.cs`

**Step 1: Add the command-line handler**

Add after the existing `--clean-descriptions` block (around line 248):

```csharp
if (args.Contains("--batch-label"))
{
    await RunBatchLabel(host, args);
    return;
}
```

Add the implementation method at the bottom of Program.cs:

```csharp
static async Task RunBatchLabel(IHost host, string[] args)
{
    var configuration = host.Services.GetRequiredService<IConfiguration>();
    var apiKey = configuration.GetValue<string>("OpenAi:ApiKey")
        ?? throw new InvalidOperationException("OpenAi:ApiKey is required");
    var logger = host.Services.GetRequiredService<ILogger<BatchLabeler>>();
    var labeler = new BatchLabeler(apiKey, logger);

    var csvPath = GetStringArg(args, "--csv")
        ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AIOMarketMaker.ML", "Training", "data", "labeled_pairs_v8.csv");
    var workingDir = GetStringArg(args, "--output-dir")
        ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AIOMarketMaker.ML", "Training", "data");

    Directory.CreateDirectory(workingDir);
    var statePath = Path.Combine(workingDir, "batch_state.json");
    var inputJsonl = Path.Combine(workingDir, "batch_input.jsonl");
    var outputJsonl = Path.Combine(workingDir, "batch_output.jsonl");
    var mergedCsv = Path.Combine(workingDir, "labeled_pairs_v10.csv");

    var subcommand = args.FirstOrDefault(a => a is "start" or "status") ?? "status";

    switch (subcommand)
    {
        case "start":
        {
            if (File.Exists(statePath))
            {
                Console.WriteLine($"Batch already in progress (state file exists at {statePath}).");
                Console.WriteLine("Run 'status' to check progress, or delete batch_state.json to start fresh.");
                return;
            }

            Console.WriteLine("Generating JSONL from v8 CSV...");
            var count = await BatchLabeler.GenerateBatchInput(csvPath, inputJsonl);
            Console.WriteLine($"Generated {count:N0} batch requests to {inputJsonl}");

            Console.WriteLine("Submitting batch to OpenAI...");
            var batchId = await labeler.SubmitBatch(inputJsonl, workingDir);
            Console.WriteLine($"Batch submitted: {batchId}");
            Console.WriteLine("Run '--batch-label status' to check progress.");
            break;
        }

        case "status":
        {
            if (!File.Exists(statePath))
            {
                Console.WriteLine("No batch in progress. Run '--batch-label start' first.");
                return;
            }

            var stateJson = await File.ReadAllTextAsync(statePath);
            var state = JsonSerializer.Deserialize<JsonElement>(stateJson);
            var batchId = state.GetProperty("batchId").GetString()!;

            var status = await labeler.GetBatchStatus(batchId);
            Console.WriteLine($"Batch {batchId}:");
            Console.WriteLine($"  Status:    {status.Status}");
            Console.WriteLine($"  Completed: {status.Completed:N0} / {status.Total:N0}");
            Console.WriteLine($"  Failed:    {status.Failed:N0}");

            if (!status.IsTerminal)
            {
                var pct = status.Total > 0 ? 100.0 * status.Completed / status.Total : 0;
                Console.WriteLine($"  Progress:  {pct:F1}%");
                Console.WriteLine("\nBatch still running. Check back later.");
                return;
            }

            if (status.Status != "Completed")
            {
                Console.WriteLine($"\nBatch ended with status: {status.Status}");
                return;
            }

            Console.WriteLine("\nBatch complete! Download and merge results? (y/n)");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is not "y" and not "yes")
            {
                Console.WriteLine("Skipped. Run '--batch-label status' again when ready.");
                return;
            }

            Console.WriteLine("Downloading results...");
            await labeler.DownloadResults(status.OutputFileId!, outputJsonl);

            Console.WriteLine("Merging with original CSV...");
            var mergeResult = await BatchLabeler.MergeResults(csvPath, outputJsonl, mergedCsv);
            Console.WriteLine($"Merged {mergeResult.Total:N0} pairs: {mergeResult.Agreed:N0} agreed, {mergeResult.Disagreed:N0} disagreed, {mergeResult.Errors:N0} errors");

            Console.WriteLine("\nDisagreement analysis:");
            await BatchLabeler.AnalyzeDisagreements(mergedCsv);

            Console.WriteLine($"\nOutput: {mergedCsv}");
            break;
        }
    }
}

static string? GetStringArg(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}
```

This requires a new `GetBatchStatus` method on `BatchLabeler` (non-polling — single check):

```csharp
public record BatchStatusResult(
    string Status,
    int Completed,
    int Failed,
    int Total,
    bool IsTerminal,
    string? OutputFileId);

public async Task<BatchStatusResult> GetBatchStatus(string batchId)
{
    var batchClient = _openAiClient.GetBatchClient();
    var batch = await batchClient.GetBatchAsync(batchId);

    var status = batch.Value.Status.ToString();
    var completed = batch.Value.RequestCounts?.CompletedCount ?? 0;
    var failed = batch.Value.RequestCounts?.FailedCount ?? 0;
    var total = batch.Value.RequestCounts?.TotalCount ?? 0;
    var isTerminal = batch.Value.Status is BatchStatus.Completed
        or BatchStatus.Failed or BatchStatus.Expired or BatchStatus.Cancelled;
    var outputFileId = batch.Value.OutputFileId;

    return new BatchStatusResult(status, completed, failed, total, isTerminal, outputFileId);
}
```

**Step 2: Build to verify compilation**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl --no-restore`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Program.cs
git commit -m "feat: add --batch-label start/status commands to ETL"
```

---

### Task 7: Integration Test — Generate + Verify JSONL

**Files:**
- Test: `AIOMarketMaker/AIOMarketMaker.Tests.Integration/BatchLabeler_IntegrationTests.cs`

**Step 1: Write integration test that generates JSONL from the real v8 CSV**

```csharp
using AIOMarketMaker.ML.Services;

namespace AIOMarketMaker.Tests.Integration;

[TestFixture]
[Category("Integration")]
public class BatchLabeler_IntegrationTests
{
    [Test]
    [Explicit("Generates a 200MB+ JSONL file from real data")]
    public async Task Should_generate_jsonl_from_real_v8_csv()
    {
        var csvPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "AIOMarketMaker.ML", "Training", "data", "labeled_pairs_v8.csv");

        if (!File.Exists(csvPath))
        {
            Assert.Ignore($"v8 CSV not found at {Path.GetFullPath(csvPath)}");
        }

        var outputPath = Path.Combine(Path.GetTempPath(), "batch_input_test.jsonl");

        try
        {
            var count = await BatchLabeler.GenerateBatchInput(csvPath, outputPath);

            Assert.That(count, Is.EqualTo(143075));
            Assert.That(File.Exists(outputPath));

            // Verify first and last lines are valid JSON
            var lines = File.ReadLines(outputPath).Take(3).ToList();
            foreach (var line in lines)
            {
                Assert.DoesNotThrow(() => System.Text.Json.JsonDocument.Parse(line));
            }

            var fileInfo = new FileInfo(outputPath);
            TestContext.WriteLine($"Generated {count:N0} lines, file size: {fileInfo.Length / 1024 / 1024}MB");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }
}
```

**Step 2: Run integration test**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Integration --filter "Should_generate_jsonl_from_real_v8_csv"`
Expected: PASS, prints file size

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests.Integration/BatchLabeler_IntegrationTests.cs
git commit -m "test: add integration test for batch JSONL generation from v8 CSV"
```

---

### Task 8: Run All Unit Tests and Verify

**Step 1: Run full unit test suite**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit`
Expected: All tests pass (336+ existing + 3 new BatchLabeler tests)

**Step 2: Run full solution build**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`
Expected: Build succeeded, 0 errors

**Step 3: Commit any fixes if needed**

---

## Usage

After implementation, the pipeline is run with two commands:

```bash
# 1. Kick off the batch (generates JSONL, uploads, submits, exits immediately)
dotnet run --project AIOMarketMaker/AIOMarketMaker.Etl -- --batch-label start

# 2. Check progress (run whenever you want — no long-running process)
dotnet run --project AIOMarketMaker/AIOMarketMaker.Etl -- --batch-label status
# If complete, prompts: "Download and merge results? (y/n)"
# On "y": downloads, merges, prints disagreement analysis
```

Output: `AIOMarketMaker.ML/Training/data/labeled_pairs_v10.csv` — ready for `train.py`.
