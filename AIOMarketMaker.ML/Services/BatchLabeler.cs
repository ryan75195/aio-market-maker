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

public class BatchLabeler
{
    private const string Model = "gpt-5-mini";
    private const string StateFileName = "batch_state.json";

    private static readonly string ResponseSchema = JsonSchemaGenerator.Generate<ClassifierResponse>();

    private readonly OpenAIClient _openAiClient = null!;
    private readonly ILogger<BatchLabeler> _logger = null!;

    /// <summary>
    /// Constructor for instance methods that call the OpenAI Batch API.
    /// </summary>
    public BatchLabeler(string apiKey, ILogger<BatchLabeler> logger)
    {
        _openAiClient = new OpenAIClient(apiKey);
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
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

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
}
