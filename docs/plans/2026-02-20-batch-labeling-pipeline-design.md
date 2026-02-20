# Batch Labeling Pipeline — Design

## Goal

Relabel the v8 training dataset (143,075 pairs) using gpt-5-mini via the OpenAI Batch API. This produces a cleaner training set for the v10 ONNX variant classifier.

## Why

The current ONNX model (v8, F1=0.913) was trained on labels from a bootstrapped chain of previous models (v5→v6→v7→v8). Each generation inherited errors from the last. Our diagnostic testing shows:

- ONNX agrees with gpt-5-mini on only 70-80% of pairs
- When they disagree, gpt-5-mini is correct ~87% of the time (7/9 in batch 3)
- Effective ONNX accuracy: ~77% vs LLM's ~95%
- All LLM errors are safely conservative (false negatives — calling comparable pairs "different")

Relabeling breaks the bootstrapping cycle with an independent, higher-accuracy judge.

## Cost

Calculated from exact token counts across all 143,075 pairs:

| Component | Tokens | Batch Rate | Cost |
|-----------|--------|------------|------|
| Cached input (system prompt + schema) | 93,857,200 | $0.00625/1M | $0.59 |
| Uncached input (user prompts) | 33,958,442 | $0.0625/1M | $2.12 |
| Output (reason + verdict JSON) | 5,723,000 | $0.50/1M | $2.86 |
| **Total** | | | **$5.57** |

## Approach: OpenAI Batch API

The Batch API processes requests asynchronously within a 24-hour window at 50% cost discount. No concurrency management, rate limiting, or crash recovery needed — OpenAI manages the job.

**Why not direct API calls:** 2x the cost (~$11), requires managing 143K concurrent requests over ~6 hours, crash risk loses progress.

## Architecture

### Project: `AIOMarketMaker.ML`

New service `BatchLabeler` alongside `LlmVariantClassifier`. Both share the same system prompt and structured output schema.

```
AIOMarketMaker.ML/Services/
├── LlmVariantClassifier.cs          (existing — real-time classification)
├── LlmVariantClassifier.Prompt.cs   (existing — shared system prompt)
├── BatchLabeler.cs                  (new — batch job orchestration)
└── OpenAiChatClient.cs              (existing — not used by batch)
```

### Runner: `AIOMarketMaker.Etl`

Two CLI commands in the existing ETL console app (no long-running processes):
- `--batch-label start` — generate JSONL, upload, submit batch, save `batch_state.json`, exit immediately
- `--batch-label status` — check batch progress once, if complete prompt user to download + merge + analyze

### Data Flow

```
labeled_pairs_v8.csv
    │
    ▼
[GenerateBatchInput]
    │  Read CSV, build JSONL with one chat completion per pair
    │  Each line: { custom_id, method, url, body: { model, messages, response_format } }
    ▼
batch_input.jsonl (local file)
    │
    ▼
[SubmitBatch]
    │  Upload file via OpenAI Files API
    │  Create batch via BatchClient
    ▼
batch_id (stored to resume file)
    │
    ▼
[PollBatch]
    │  Poll every 60s until completed/failed/expired
    │  Log progress (completed_count / total_count)
    ▼
[DownloadResults]
    │  Download output JSONL from OpenAI
    │  Parse each line: extract custom_id → row index, parse ClassifierResponse
    ▼
batch_output.jsonl (raw API responses)
    │
    ▼
[MergeResults]
    │  Join LLM labels back to original CSV by custom_id
    │  Output: anchor_id, neighbor_id, product_name, onnx_label, llm_verdict, llm_reason, agreement
    ▼
labeled_pairs_v10.csv
    │
    ▼
[AnalyzeDisagreements]
    │  Print summary: agreement rate, breakdown by product_name
    │  List top disagreement categories
    ▼
Console output + disagreements.csv
```

## BatchLabeler Service API

```csharp
public class BatchLabeler
{
    // Step 1: Generate JSONL batch input from v8 CSV
    Task<string> GenerateBatchInput(string csvPath, string outputPath);

    // Step 2: Upload file and submit batch job
    Task<string> SubmitBatch(string jsonlPath);

    // Step 3: Poll until complete (logs progress)
    Task<BatchStatus> PollUntilComplete(string batchId, CancellationToken ct);

    // Step 4: Download results and merge with original data
    Task<string> DownloadAndMerge(string batchId, string originalCsvPath, string outputPath);

    // Step 5: Print disagreement analysis
    Task AnalyzeDisagreements(string mergedCsvPath);
}
```

## JSONL Batch Input Format

Each line in the input JSONL follows OpenAI's batch request format:

```json
{
  "custom_id": "pair-0",
  "method": "POST",
  "url": "/v1/chat/completions",
  "body": {
    "model": "gpt-5-mini",
    "messages": [
      { "role": "system", "content": "<system prompt>" },
      { "role": "user", "content": "Listing A:\nTitle: ...\nDescription: ...\n\nListing B:\nTitle: ...\nDescription: ..." }
    ],
    "response_format": {
      "type": "json_schema",
      "json_schema": {
        "name": "ClassifierResponse",
        "strict": true,
        "schema": {
          "type": "object",
          "properties": {
            "reason": { "type": "string" },
            "verdict": { "type": "string", "enum": ["same", "different", "uncertain"] }
          },
          "required": ["reason", "verdict"],
          "additionalProperties": false
        }
      }
    }
  }
}
```

## Output CSV Format

`labeled_pairs_v10.csv`:

| Column | Description |
|--------|-------------|
| anchor_id | eBay listing ID (from v8 CSV) |
| neighbor_id | eBay listing ID (from v8 CSV) |
| job_id | Scrape job ID (from v8 CSV) |
| product_name | Product category (from v8 CSV) |
| anchor_title | Title A (from v8 CSV) |
| neighbor_title | Title B (from v8 CSV) |
| anchor_desc | Description A (from v8 CSV) |
| neighbor_desc | Description B (from v8 CSV) |
| label | LLM verdict mapped to 0/1 (same=1, different=0, uncertain=0) |
| confidence | "high" for same/different, "low" for uncertain |
| reasoning | LLM reason text |
| source | "llm_gpt5mini_batch" |
| onnx_label | Original v8 label for comparison |

## Resumability

The batch job takes up to 24 hours. The pipeline must handle:
- **Process restart:** Store `batch_id` to a local file (`batch_state.json`) after submission. On restart, detect existing batch and resume polling.
- **Partial failure:** If some requests in the batch fail, the output JSONL contains error entries. Log these and exclude from the output CSV.

## Configuration

Uses existing `local.settings.json` in `AIOMarketMaker.Etl`:

```json
{
  "Values": {
    "OpenAi:ApiKey": "sk-..."
  }
}
```

No new config values needed. Model name and file paths are command-line arguments.

## Phase 2 (Future)

After Phase 1 validates the approach:
1. Generate new pairs from DB listings not in v8 (~107K delta)
2. Label with the same batch pipeline
3. Merge with Phase 1 output for a 250K training set
4. Train v10 model using existing `train.py` pipeline

## What This Design Does NOT Cover

- v10 model training (existing Python scripts, minimal changes)
- ONNX export (existing `export.py`)
- Production deployment (drop-in model replacement)
- Phase 2 pair generation from database (separate design if needed)
