# LLM Classifier Integration

**Date:** 2026-03-01
**Status:** Approved

## Context

Fine-tuned Qwen3-4B (LoRA) on 141K labeled pairs to replace the ONNX RoBERTa variant classifier. The RoBERTa model lacks diverse enough training examples to generalise — it can't distinguish language editions, product tiers (Premium vs Ultra-Premium), or accessories vs full products. The LLM leverages world knowledge taught through fine-tuning examples.

Training completed on RTX 5070 Ti. LoRA adapter saved to `AIOMarketMaker.ML/Training/output/qwen3-variant-classifier/`.

## Success Criteria

- 90%+ accuracy on the audit benchmark (50 pairs)
- Correct reasoning on known failure modes (Portuguese vs English, Premium vs Ultra-Premium, disc drive vs console)
- Stable inference via llama.cpp server on Windows with CUDA

## Phase 1: Evaluate the Model

### 1a. Audit Benchmark

Run `eval_finetuned.py --n 50` against the audit pairs. This is the go/no-go gate. If accuracy is below 90%, stop and investigate before proceeding.

### 1b. Targeted Stress Test

Write a Python script with ~20 hand-picked pairs covering known classifier failure modes:
- Portuguese vs English Pokemon boxes (language variant)
- Charizard Premium Collection vs Ultra-Premium Collection (product tier)
- PS5 disc drive vs full console (accessory vs product)
- Different storage/RAM/CPU configurations (128GB vs 256GB, i5 vs i7)
- Condition mismatches (Grade A vs Grade C)

Check that the model's reasoning catches the distinction, not just the verdict.

## Phase 2: Model Conversion & Serving

### 2a. Merge LoRA and Convert to GGUF

1. Load base Qwen3-4B + LoRA adapter with Unsloth
2. Merge adapter into base weights (`save_pretrained_merged`)
3. Convert merged model to GGUF using llama.cpp's convert script
4. Quantize to Q4_K_M (balance of speed and quality for 16GB VRAM)

### 2b. Run llama.cpp Server

```bash
llama-server -m qwen3-4b-variant-classifier.gguf \
  --host 0.0.0.0 --port 8080 \
  --n-gpu-layers 99 \
  --ctx-size 1024
```

Exposes OpenAI-compatible `/v1/chat/completions` endpoint.

### 2c. Verify GGUF Quality

Run the same stress test pairs from Phase 1b through the llama.cpp HTTP API. Compare verdicts and reasoning against the original Unsloth model to confirm GGUF conversion didn't degrade quality.

## Phase 3: C# Integration

### 3a. Update LlmVariantClassifier

The existing `LlmVariantClassifier` already implements `IVariantClassifierClient` and talks to an OpenAI-compatible API via `IChatClient`. Changes:

- Point base URL at `http://localhost:8080`
- Remove system prompt (fine-tuned model was trained without one)
- Update response parsing to match output format: `{"reason": "...", "verdict": "same/different"}`
- Confidence: 1.0 for clear same/different verdicts, 0.5 for uncertain

### 3b. Config-Driven Classifier Switch

Add a config flag in `local.settings.json` to choose between ONNX and LLM classifiers. Update DI registration in `Program.cs`:

```csharp
// Controlled by VariantClassifier:Provider = "Onnx" | "Llm"
if (provider == "Llm")
    services.AddSingleton<IVariantClassifierClient, LlmVariantClassifier>();
else
    services.AddSingleton<IVariantClassifierClient, VariantClassifier>();
```

### 3c. Re-classify High-Value Pairs

Console task that:
1. Finds active listings with existing predictions
2. Pulls their ListingRelationships
3. Re-classifies through the LLM
4. Updates IsComparable, ClassifierConfidence, and Explanation
5. Triggers prediction recomputation

## Architecture

```
llama.cpp server (port 8080, GPU)
       ^
       | HTTP /v1/chat/completions
       |
LlmVariantClassifier (C#)
       ^
       | IVariantClassifierClient
       |
ComparablesEtlService (batches of 256)
```

## What Changes

| Component | Changes? | Details |
|-----------|----------|---------|
| ComparablesEtlService | No | Calls IVariantClassifierClient as before |
| IVariantClassifierClient | No | Same interface |
| LlmVariantClassifier | Yes | Remove system prompt, update base URL, adjust parsing |
| VariantClassifier (ONNX) | No | Stays in codebase, switch back via config |
| Program.cs DI | Yes | Config flag to choose ONNX vs LLM |
| ListingRelationship | No | Same schema, Explanation gets richer reasoning |
| Pricing algorithm | No | Consumes IsComparable + Confidence as before |

## Risks

1. **GGUF conversion quality loss.** Q4_K_M quantization may degrade accuracy. Mitigated by Phase 2c verification.
2. **llama.cpp Qwen3 support.** Qwen3 is new. If llama.cpp lacks support, fall back to Unsloth FastAPI server.
3. **Batch timeout.** At ~2-4s/pair, a batch of 256 takes 8-17 minutes. C# HTTP client needs longer timeouts than the ONNX model's sub-second batches.

## Files

- `AIOMarketMaker.ML/Training/eval_finetuned.py` — Audit benchmark evaluation
- `AIOMarketMaker.ML/Training/stress_test.py` — Targeted failure mode tests (to create)
- `AIOMarketMaker.ML/Training/merge_and_convert.py` — LoRA merge + GGUF conversion (to create)
- `AIOMarketMaker.ML/Services/LlmVariantClassifier.cs` — C# classifier (existing, needs updates)
- `AIOMarketMaker.Api/Program.cs` — DI registration switch
