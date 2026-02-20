#pragma warning disable OPENAI001 // Batch API is marked experimental in OpenAI SDK v2.x

using System.ClientModel;
using System.Text;
using System.Text.Json;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Utils;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Batch;
using OpenAI.Files;

namespace AIOMarketMaker.ML.Services;

public record BatchStatusResult(
    string Status,
    int Completed,
    int Failed,
    int Total,
    bool IsTerminal,
    string? OutputFileId);

public record BatchOutputResult(
    string CustomId,
    int Index,
    string? Verdict,
    string? Reason,
    string? Error);

public record MergeResult(int Total, int Agreed, int Disagreed, int Errors);

public class BatchLabeler
{
    private const string Model = "gpt-5-mini";
    private const string StateFileName = "batch_state.json";
    private const int MaxRequestsPerBatch = 50_000;

    private static readonly string ResponseSchema = JsonSchemaGenerator.Generate<ClassifierResponse>();

    private readonly OpenAIClient _openAiClient = null!;
    private readonly ILogger<BatchLabeler> _logger = null!;

    /// <summary>
    /// Constructor for instance methods that call the OpenAI Batch API.
    /// </summary>
    public BatchLabeler(string apiKey, ILogger<BatchLabeler> logger)
    {
        _openAiClient = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), new OpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromMinutes(10)
        });
        _logger = logger;
    }

    /// <summary>
    /// Uploads a JSONL file to OpenAI and creates a batch job.
    /// Saves batch_state.json for resumability. Returns the batch ID.
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
        var requestBody = BinaryContent.Create(BinaryData.FromObjectAsJson(new
        {
            input_file_id = fileId,
            endpoint = "/v1/chat/completions",
            completion_window = "24h",
            metadata = new { description = "v10 training relabel" }
        }));

        var batchOperation = await batchClient.CreateBatchAsync(requestBody, waitUntilCompleted: false);
        var batchResponse = batchOperation.GetRawResponse();
        var batchJson = JsonDocument.Parse(batchResponse.Content);
        var batchId = batchJson.RootElement.GetProperty("id").GetString()!;

        _logger.LogInformation("Batch created: {BatchId}", batchId);

        // Save state for resumability
        var statePath = Path.Combine(workingDir, StateFileName);
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new { batchId, fileId }));

        return batchId;
    }

    /// <summary>
    /// Single non-blocking status check for a batch job. Does NOT poll.
    /// </summary>
    public async Task<BatchStatusResult> GetBatchStatus(string batchId)
    {
        var batchClient = _openAiClient.GetBatchClient();

        var operation = await CreateBatchOperation.RehydrateAsync(batchClient, batchId);
        var response = await operation.GetBatchAsync(options: null);
        var json = JsonDocument.Parse(response.GetRawResponse().Content);

        var root = json.RootElement;
        var status = root.GetProperty("status").GetString()!;

        var completed = 0;
        var failed = 0;
        var total = 0;
        if (root.TryGetProperty("request_counts", out var counts))
        {
            completed = counts.TryGetProperty("completed", out var c) ? c.GetInt32() : 0;
            failed = counts.TryGetProperty("failed", out var f) ? f.GetInt32() : 0;
            total = counts.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
        }

        var isTerminal = status is "completed" or "failed" or "expired" or "cancelled";
        var outputFileId = root.TryGetProperty("output_file_id", out var ofi) ? ofi.GetString() : null;

        return new BatchStatusResult(status, completed, failed, total, isTerminal, outputFileId);
    }

    /// <summary>
    /// Downloads the batch output file to the specified path.
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

    /// <summary>
    /// Parses a single JSONL line from the OpenAI Batch API output file.
    /// </summary>
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
            Verdict.Same => "same",
            Verdict.Different => "different",
            Verdict.Uncertain => "uncertain",
            _ => null
        };

        return new BatchOutputResult(customId, index, verdict, parsed?.Reason, null);
    }

    /// <summary>
    /// Merges batch output results back into the original CSV, producing a new CSV
    /// with LLM labels alongside ONNX labels for comparison.
    /// </summary>
    public static async Task<MergeResult> MergeResults(
        string originalCsvPath, IEnumerable<string> batchOutputPaths, string outputCsvPath)
    {
        // Parse all batch output lines from all output files
        var results = new Dictionary<int, BatchOutputResult>();
        foreach (var outputPath in batchOutputPaths)
        {
            foreach (var line in await File.ReadAllLinesAsync(outputPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                var result = ParseBatchOutputLine(line);
                results[result.Index] = result;
            }
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
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            var fields = ParseCsvLine(line);

            if (results.TryGetValue(rowIndex, out var batchResult) && batchResult.Verdict is not null)
            {
                var llmLabel = batchResult.Verdict == "same" ? 1 : 0;
                var onnxLabel = int.TryParse(fields[labelIdx], out var v) ? v : 0;
                var confidence = batchResult.Verdict == "uncertain" ? "low" : "high";

                if (llmLabel == onnxLabel)
                {
                    agreed++;
                }
                else
                {
                    disagreed++;
                }

                var anchorId = fields[Array.IndexOf(allColumns, "anchor_id")];
                var neighborId = fields[Array.IndexOf(allColumns, "neighbor_id")];
                var jobId = fields[Array.IndexOf(allColumns, "job_id")];
                var productName = CsvEscape(fields[Array.IndexOf(allColumns, "product_name")]);
                var anchorTitle = CsvEscape(fields[columnIndices.AnchorTitle]);
                var neighborTitle = CsvEscape(fields[columnIndices.NeighborTitle]);
                var anchorDesc = CsvEscape(CleanField(fields[columnIndices.AnchorDesc]));
                var neighborDesc = CsvEscape(CleanField(fields[columnIndices.NeighborDesc]));
                var reasoning = CsvEscape(batchResult.Reason ?? "");

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
        if (header is null)
        {
            return;
        }

        var columns = header.Split(',');
        var productIdx = Array.IndexOf(columns, "product_name");
        var labelIdx = Array.IndexOf(columns, "label");
        var onnxIdx = Array.IndexOf(columns, "onnx_label");

        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            var fields = ParseCsvLine(line);
            if (fields.Length <= onnxIdx)
            {
                continue;
            }

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

    /// <summary>
    /// Reads a v8 CSV file and writes one or more JSONL batch input files.
    /// Splits into chunks of 50,000 requests (OpenAI Batch API limit).
    /// Returns the list of generated file paths and total pair count.
    /// </summary>
    public static async Task<(IEnumerable<string> Files, int TotalPairs)> GenerateBatchInput(string csvPath, string outputDir)
    {
        var count = 0;
        var chunkIndex = 0;
        var chunkCount = 0;
        var files = new List<string>();

        using var reader = new StreamReader(csvPath);

        var header = await reader.ReadLineAsync();
        if (header is null)
        {
            return (files, 0);
        }

        var columnIndices = ParseHeader(header);

        StreamWriter? writer = null;
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var fields = ParseCsvLine(line);
                if (fields.Length < columnIndices.NeighborDesc + 1)
                {
                    continue;
                }

                // Start new chunk file if needed
                if (writer is null || chunkCount >= MaxRequestsPerBatch)
                {
                    if (writer is not null)
                    {
                        await writer.DisposeAsync();
                    }
                    var chunkPath = Path.Combine(outputDir, $"batch_input_{chunkIndex}.jsonl");
                    files.Add(chunkPath);
                    writer = new StreamWriter(chunkPath);
                    chunkIndex++;
                    chunkCount = 0;
                }

                var pair = new ClassifyPairRequest(
                    TitleA: fields[columnIndices.AnchorTitle],
                    DescriptionA: CleanField(fields[columnIndices.AnchorDesc]),
                    TitleB: fields[columnIndices.NeighborTitle],
                    DescriptionB: CleanField(fields[columnIndices.NeighborDesc]));

                var jsonLine = BuildBatchRequestLine($"pair-{count}", pair);
                await writer.WriteLineAsync(jsonLine);
                count++;
                chunkCount++;
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync();
            }
        }

        return (files, count);
    }

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
        var field = new StringBuilder();

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

    private static string CsvEscape(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }
}
