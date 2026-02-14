# Comparables Pipeline Optimization

## Goal

Optimize `ComparablesEtlService` so a full run across 195K listings completes in ~40 minutes instead of ~18 hours. Enable incremental results so predictions appear in the API while the pipeline is still running.

## Current Bottlenecks

| Bottleneck | Current | Impact |
|-----------|---------|--------|
| Pinecone queries for all listings (active + sold) | 195K API calls | ~65 min |
| Single-pair ONNX inference | 12ms/pair x ~4.9M pairs | ~16 hours |
| TopK=50 returns junk for niche categories | Wasted model inference | Variable |
| Sequential stages (query all → classify all → save all) | GPU idle during Pinecone, no incremental results | ~20 hours total |

## Design

### 1. Active-Only Pinecone Queries

Only query Pinecone for active listings (99.5K). Sold listings remain in the Pinecone index as neighbors but are never queried themselves.

- Load only active listings from DB
- Load all sold listing IDs into a HashSet for filtering
- Query Pinecone for each active listing's neighbors
- Discard candidate pairs where the neighbor is not sold

**Rationale**: We only care about "what will this active listing sell for?" — answered by finding comparable sold listings. Sold↔sold and active↔active pairs are wasted work.

### 2. Keep TopK=50, No Pinecone Threshold

Keep the existing `TopK=50` — the ONNX model is the real filter for product variant matching, not Pinecone similarity scores. No Pinecone similarity threshold needed.

- 50 neighbors is statistically sufficient for reliable price averaging (30+ comps gives ±£18 accuracy at 95% CI)
- After sold-only filtering + model classification, the actual number of confirmed comparables per listing is typically 10-30
- Diminishing returns past ~50: going from 50→100 neighbors barely improves prediction accuracy

**Rationale**: The model handles quality filtering. Pinecone just needs to return a reasonable candidate set.

### 3. Batched ONNX Inference

Change `OnnxVariantClassifier.Classify()` to run true batched inference with tensor shape `[N, 256]` instead of looping `[1, 256]`.

- Add `BatchSize` to `OnnxClassifierConfig` (default 128)
- Tokenize all pairs in the batch, stack into a single tensor
- Single `InferenceSession.Run()` call for the whole batch
- GPU processes all 128 pairs in parallel across its cores

**Expected speedup**: ~0.5ms/pair (batched) vs 12ms/pair (single) = ~24x throughput improvement.

### 4. Pipelined Producer-Consumer Architecture

Replace the three sequential stages with a Channel-based pipeline (same pattern as `FetchAndProcessDescriptions`):

```
[Pinecone producers]  →  Channel<CandidatePair>  →  [Batch collector]  →  [ONNX + DB writer]
   10 concurrent           unbounded                  collects 128          classify + save
                                                      or 500ms timeout
```

**Producer** (10 concurrent):
- For each active listing, query Pinecone for similar neighbors
- Filter to sold neighbors above similarity threshold
- Check against existing verdicts cache (skip already-evaluated pairs)
- Write candidate pairs to channel

**Consumer** (single, sequential for DbContext safety):
- Collect pairs from channel into batches of 128 (or flush on 500ms timeout)
- Classify batch via ONNX
- Save verdicts to `ListingRelationships`
- Recompute prediction for the active listing
- Update progress counters

**Benefits**:
- GPU works while Pinecone queries are still running
- Predictions appear in the API incrementally
- Resumable — already-saved verdicts are skipped via cache check
- No memory spike from holding millions of pairs

### 5. Incremental Predictions

After saving verdicts for an active listing, immediately compute its prediction (average sold price from comparable sold listings). This replaces the final bulk `ComputeAndStorePredictions()` step.

The `GetActiveListings` API endpoint picks up new predictions on each request — no restart needed.

## Configuration

All in `appsettings.json` / `local.settings.json` under existing config sections:

```json
{
  "Pinecone": {
    "TopK": 50
  },
  "VariantClassifier": {
    "BatchSize": 128
  }
}
```

## Estimated Runtime (Full Run)

| Step | Count | Time |
|------|-------|------|
| Load active listings from DB | 99.5K | ~2s |
| Pinecone queries (10 concurrent, overlapped with GPU) | 99.5K | ~35 min |
| ONNX classification (batch 128, overlapped with Pinecone) | ~1-2.5M pairs | ~10-15 min |
| DB writes + predictions (incremental) | ~1-2.5M verdicts | ~5 min |
| **Total (pipelined)** | | **~40 min** |

Pair count drops from ~4.9M to ~1-2.5M due to: active-only queries (halved), TopK=50 (kept reasonable), sold-only neighbor filter.

## What Changes

| Component | Change |
|-----------|--------|
| `OnnxVariantClassifier.Classify()` | Batched tensor inference `[N, 256]` |
| `OnnxClassifierConfig` | Add `BatchSize` field |
| `ComparablesEtlService.Run()` | Active-only loading, pipelined architecture |
| `ComparablesEtlService.FilterToIndexedListings()` | Removed (skip existence check, just query directly) |
| `ComparablesEtlService.QueryPineconeForCandidates()` | Threshold filtering, sold-only neighbors |
| `ComparablesEtlService.EvaluatePairsWithLlm()` | Replaced by channel consumer with batched classify |
| `ComparablesEtlService.ComputeAndStorePredictions()` | Replaced by incremental per-listing prediction |
| `ComparablesEtlResult` | Add timing fields for observability |

## What Doesn't Change

- `IListingComparisonService` / `ModelFirstComparisonService` — not used directly (batched classify bypasses the single-pair interface)
- `ISemanticSearchService` API — just called with different parameters
- `ListingRelationships` / `ListingPredictions` schema — same tables, same data
- `GetActiveListings` endpoint — reads predictions as before
- DI wiring — same services, same registration
