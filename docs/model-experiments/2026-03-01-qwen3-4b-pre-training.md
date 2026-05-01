# Local LLM Variant Classifier

**Date:** 2026-03-01
**Status:** Training in progress

## Problem

The production ONNX variant classifier (RoBERTa-large v10, F1=0.913) has systematic false positives caused by noisy training labels. The v10 dataset (143K pairs) was labeled with a simple GPT prompt ("are these the same variant?") that didn't capture nuanced classification rules:

- **Parts vs whole products** — a disc drive labeled as comparable to a full PS5 console
- **Template-polluted descriptions** — sellers reuse one description across different products, and the model sees matching text and labels "same"
- **Cross-category matches** — items with similar titles but different functional specs

These aren't limitations of the RoBERTa architecture — the model learns what it's taught. The training labels are the ceiling.

## Solution

Fine-tune a local LLM (Qwen3-4B) to serve as both a **production classifier** and a **training data generator**.

### Why not just use a better GPT prompt?

We wrote a detailed 75-line classification prompt (v9) covering condition matching, bundles, category-specific rules, and spec extraction. Qwen3-4B scores only 55% accuracy with this prompt zero-shot — it can't follow complex instructions reliably. Fine-tuning teaches the behavior through 141K examples instead.

### Why not just re-label with GPT API?

We could, but the GPT batch API costs money per run and creates a dependency on external APIs. A local model runs for free, can classify production pairs continuously, and generates training data as a side effect of normal operation.

## Architecture

```
Phase 1 (current): Fine-tune Qwen3-4B on 141K existing labels
Phase 2: Deploy as production classifier (replacing ONNX RoBERTa)
Phase 3: Accumulate high-quality labeled data from production classifications
Phase 4: Retrain RoBERTa on cleaner labels, swap back for speed
```

The LLM pulls double duty — production classifier *and* training data generator — until RoBERTa has enough clean data to take over.

### Speed tradeoff

| Model | Speed | Use case |
|-------|-------|----------|
| RoBERTa ONNX | ~6ms/pair (batched) | Final production target |
| Qwen3-4B (4-bit) | ~4-5s/pair | Interim production + labeling |

The LLM is ~800x slower but produces reasoning with every classification, making errors debuggable and generating labeled training data automatically.

## Training Setup

- **Model:** Qwen3-4B (dense, not MoE) — ranked #1 for classification fine-tuning in Distillabs benchmark
- **Method:** QLoRA via Unsloth (33M trainable params of 4B, 0.81%)
- **GPU:** RTX 5070 Ti, 16GB VRAM
- **Data:** 141,833 pairs from labeled_pairs_v10.csv (GPT-5-mini labels with reasoning)
  - 113,120 negative (different) / 29,955 positive (same)
  - Low-confidence labels filtered out (1,242 removed)
- **Format:** Chat format, no system prompt, reason-before-verdict JSON for chain-of-thought
- **Hyperparameters:** LR=1e-4, LoRA rank=16, alpha=32, batch_size=2, grad_accum=8 (effective=16), max_seq_len=512, 1 epoch
- **Duration:** ~9 hours estimated

### Training format

Input (user message):
```
Listing A:
Title: Sony PlayStation 5 Slim 1TB Console
Description: Brand new sealed PS5 slim...

Listing B:
Title: PS5 Digital Edition 825GB Console
Description: Used PS5 digital edition...
```

Output (assistant message):
```json
{"reason": "Different storage (1TB vs 825GB) and different editions (disc vs digital)", "verdict": "different"}
```

Reason is generated before verdict to unlock chain-of-thought — the model reasons through differences before committing to a decision.

## Early Results

- **Zero-shot baseline (Qwen3-4B):** 55% accuracy on 20 audit pairs
- **After 400 training examples:** 67% accuracy — already learning color-is-OK and spec-difference patterns
- **Expected after full training:** Significant improvement with 141K examples

## Integration Path

The existing `IVariantClassifierClient` interface returns `PairResult(bool IsComparable, float Confidence, string? Reason, float? LogitDiff)`. The LLM output maps directly:
- `verdict: "same"/"different"` → `IsComparable`
- `reason` → `Reason`
- Confidence can be derived from token probabilities

## Files

- `AIOMarketMaker.ML/Training/finetune_qwen3.py` — Fine-tuning script
- `AIOMarketMaker.ML/Training/eval_finetuned.py` — Evaluation against audit benchmark
- `AIOMarketMaker.ML/Training/experiment_local_llm.py` — Zero-shot baseline experiment
- `AIOMarketMaker.ML/Training/logs/` — Training logs with per-step metrics
- `AIOMarketMaker.ML/Training/output/qwen3-variant-classifier/` — Saved LoRA adapter
