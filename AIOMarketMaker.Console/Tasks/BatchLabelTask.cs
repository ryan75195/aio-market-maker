using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.ML.Services;

namespace AIOMarketMaker.Console.Tasks;

public class BatchLabelTask : ITask
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<BatchLabeler> _logger;

    public string Name => "batch-label";
    public string Description => "Submit/check OpenAI batch labeling jobs for training data";

    public BatchLabelTask(IConfiguration configuration, ILogger<BatchLabeler> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var apiKey = _configuration.GetValue<string>("OpenAi:ApiKey")
            ?? throw new InvalidOperationException("OpenAi:ApiKey is required");
        var labeler = new BatchLabeler(apiKey, _logger);

        var csvPath = CommandHelpers.GetStringArg(args, "--csv")
            ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AIOMarketMaker.ML", "Training", "data", "labeled_pairs_v8.csv");
        var workingDir = CommandHelpers.GetStringArg(args, "--output-dir")
            ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AIOMarketMaker.ML", "Training", "data");

        Directory.CreateDirectory(workingDir);
        var statePath = Path.Combine(workingDir, "batch_state.json");
        var mergedCsv = Path.Combine(workingDir, "labeled_pairs_v10.csv");

        var subcommand = args.FirstOrDefault(a => a is "start" or "status") ?? "status";

        switch (subcommand)
        {
            case "start":
            {
                if (File.Exists(statePath))
                {
                    System.Console.WriteLine($"Batch already in progress (state file exists at {statePath}).");
                    System.Console.WriteLine("Run 'status' to check progress, or delete batch_state.json to start fresh.");
                    return 0;
                }

                System.Console.WriteLine("Generating JSONL from v8 CSV...");
                var (chunkFiles, totalPairs) = await BatchLabeler.GenerateBatchInput(csvPath, workingDir);
                var chunkList = chunkFiles.ToList();
                System.Console.WriteLine($"Generated {totalPairs:N0} batch requests across {chunkList.Count} file(s)");

                var batches = new List<object>();
                for (var i = 0; i < chunkList.Count; i++)
                {
                    System.Console.WriteLine($"Submitting batch {i + 1}/{chunkList.Count}...");
                    var batchId = await labeler.SubmitBatch(chunkList[i], workingDir);
                    System.Console.WriteLine($"  Batch {i + 1}: {batchId}");
                    batches.Add(new { batchId, inputFile = Path.GetFileName(chunkList[i]) });
                }

                // Save state with all batch IDs (overwrite the single-batch state files)
                await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new { batches }, new JsonSerializerOptions { WriteIndented = true }), ct);
                System.Console.WriteLine($"\nAll {chunkList.Count} batches submitted. Run '--batch-label status' to check progress.");
                break;
            }

            case "status":
            {
                if (!File.Exists(statePath))
                {
                    System.Console.WriteLine("No batch in progress. Run '--batch-label start' first.");
                    return 0;
                }

                var stateJson = await File.ReadAllTextAsync(statePath, ct);
                var state = JsonSerializer.Deserialize<JsonElement>(stateJson);
                var batchArray = state.GetProperty("batches");

                var allComplete = true;
                var allStatuses = new List<(string BatchId, BatchStatusResult Status)>();

                var totalCompleted = 0;
                var totalRequests = 0;
                var totalFailed = 0;

                for (var i = 0; i < batchArray.GetArrayLength(); i++)
                {
                    var entry = batchArray[i];
                    var batchId = entry.GetProperty("batchId").GetString()!;
                    var status = await labeler.GetBatchStatus(batchId);
                    allStatuses.Add((batchId, status));

                    totalCompleted += status.Completed;
                    totalRequests += status.Total;
                    totalFailed += status.Failed;

                    System.Console.WriteLine($"Batch {i + 1}/{batchArray.GetArrayLength()} ({batchId}):");
                    System.Console.WriteLine($"  Status:    {status.Status}");
                    System.Console.WriteLine($"  Completed: {status.Completed:N0} / {status.Total:N0}");
                    if (status.Failed > 0)
                    {
                        System.Console.WriteLine($"  Failed:    {status.Failed:N0}");
                    }

                    if (!status.IsTerminal)
                    {
                        allComplete = false;
                    }
                    else if (status.Status != "completed")
                    {
                        System.Console.WriteLine($"  ** Batch ended with status: {status.Status} **");
                    }
                }

                System.Console.WriteLine();
                var pct = totalRequests > 0 ? 100.0 * totalCompleted / totalRequests : 0;
                System.Console.WriteLine($"Overall: {totalCompleted:N0} / {totalRequests:N0} ({pct:F1}%) completed, {totalFailed:N0} failed");

                if (!allComplete)
                {
                    System.Console.WriteLine("\nBatches still running. Check back later.");
                    return 0;
                }

                if (allStatuses.Any(s => s.Status.Status != "completed"))
                {
                    System.Console.WriteLine("\nSome batches failed. Check output above.");
                    return 1;
                }

                System.Console.WriteLine("\nAll batches complete! Download and merge results? (y/n)");
                var answer = System.Console.ReadLine()?.Trim().ToLowerInvariant();
                if (answer is not "y" and not "yes")
                {
                    System.Console.WriteLine("Skipped. Run '--batch-label status' again when ready.");
                    return 0;
                }

                var outputFiles = new List<string>();
                for (var i = 0; i < allStatuses.Count; i++)
                {
                    var (batchId, batchStatus) = allStatuses[i];
                    var outputPath = Path.Combine(workingDir, $"batch_output_{i}.jsonl");
                    System.Console.WriteLine($"Downloading batch {i + 1}/{allStatuses.Count}...");
                    await labeler.DownloadResults(batchStatus.OutputFileId!, outputPath);
                    outputFiles.Add(outputPath);
                }

                System.Console.WriteLine("Merging with original CSV...");
                var mergeResult = await BatchLabeler.MergeResults(csvPath, outputFiles, mergedCsv);
                System.Console.WriteLine($"Merged {mergeResult.Total:N0} pairs: {mergeResult.Agreed:N0} agreed, {mergeResult.Disagreed:N0} disagreed, {mergeResult.Errors:N0} errors");

                System.Console.WriteLine("\nDisagreement analysis:");
                await BatchLabeler.AnalyzeDisagreements(mergedCsv);

                System.Console.WriteLine($"\nOutput: {mergedCsv}");
                break;
            }
        }

        return 0;
    }
}
