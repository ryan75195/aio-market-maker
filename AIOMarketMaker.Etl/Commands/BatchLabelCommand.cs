using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.ML.Services;
namespace AIOMarketMaker.Etl.Commands;

public static class BatchLabelCommand
{
    public static async Task Run(IHost host, string[] args)
    {
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var apiKey = configuration.GetValue<string>("OpenAi:ApiKey")
            ?? throw new InvalidOperationException("OpenAi:ApiKey is required");
        var logger = host.Services.GetRequiredService<ILogger<BatchLabeler>>();
        var labeler = new BatchLabeler(apiKey, logger);

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
                    Console.WriteLine($"Batch already in progress (state file exists at {statePath}).");
                    Console.WriteLine("Run 'status' to check progress, or delete batch_state.json to start fresh.");
                    return;
                }

                Console.WriteLine("Generating JSONL from v8 CSV...");
                var (chunkFiles, totalPairs) = await BatchLabeler.GenerateBatchInput(csvPath, workingDir);
                var chunkList = chunkFiles.ToList();
                Console.WriteLine($"Generated {totalPairs:N0} batch requests across {chunkList.Count} file(s)");

                var batches = new List<object>();
                for (var i = 0; i < chunkList.Count; i++)
                {
                    Console.WriteLine($"Submitting batch {i + 1}/{chunkList.Count}...");
                    var batchId = await labeler.SubmitBatch(chunkList[i], workingDir);
                    Console.WriteLine($"  Batch {i + 1}: {batchId}");
                    batches.Add(new { batchId, inputFile = Path.GetFileName(chunkList[i]) });
                }

                // Save state with all batch IDs (overwrite the single-batch state files)
                await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new { batches }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"\nAll {chunkList.Count} batches submitted. Run '--batch-label status' to check progress.");
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

                    Console.WriteLine($"Batch {i + 1}/{batchArray.GetArrayLength()} ({batchId}):");
                    Console.WriteLine($"  Status:    {status.Status}");
                    Console.WriteLine($"  Completed: {status.Completed:N0} / {status.Total:N0}");
                    if (status.Failed > 0)
                    {
                        Console.WriteLine($"  Failed:    {status.Failed:N0}");
                    }

                    if (!status.IsTerminal)
                    {
                        allComplete = false;
                    }
                    else if (status.Status != "completed")
                    {
                        Console.WriteLine($"  ** Batch ended with status: {status.Status} **");
                    }
                }

                Console.WriteLine();
                var pct = totalRequests > 0 ? 100.0 * totalCompleted / totalRequests : 0;
                Console.WriteLine($"Overall: {totalCompleted:N0} / {totalRequests:N0} ({pct:F1}%) completed, {totalFailed:N0} failed");

                if (!allComplete)
                {
                    Console.WriteLine("\nBatches still running. Check back later.");
                    return;
                }

                if (allStatuses.Any(s => s.Status.Status != "completed"))
                {
                    Console.WriteLine("\nSome batches failed. Check output above.");
                    return;
                }

                Console.WriteLine("\nAll batches complete! Download and merge results? (y/n)");
                var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (answer is not "y" and not "yes")
                {
                    Console.WriteLine("Skipped. Run '--batch-label status' again when ready.");
                    return;
                }

                var outputFiles = new List<string>();
                for (var i = 0; i < allStatuses.Count; i++)
                {
                    var (batchId, status) = allStatuses[i];
                    var outputPath = Path.Combine(workingDir, $"batch_output_{i}.jsonl");
                    Console.WriteLine($"Downloading batch {i + 1}/{allStatuses.Count}...");
                    await labeler.DownloadResults(status.OutputFileId!, outputPath);
                    outputFiles.Add(outputPath);
                }

                Console.WriteLine("Merging with original CSV...");
                var mergeResult = await BatchLabeler.MergeResults(csvPath, outputFiles, mergedCsv);
                Console.WriteLine($"Merged {mergeResult.Total:N0} pairs: {mergeResult.Agreed:N0} agreed, {mergeResult.Disagreed:N0} disagreed, {mergeResult.Errors:N0} errors");

                Console.WriteLine("\nDisagreement analysis:");
                await BatchLabeler.AnalyzeDisagreements(mergedCsv);

                Console.WriteLine($"\nOutput: {mergedCsv}");
                break;
            }
        }
    }
}
