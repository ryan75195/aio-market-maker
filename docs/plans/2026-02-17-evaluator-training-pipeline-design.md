# Evaluator Training Pipeline Design

## Problem

The ONNX cross-encoder (RoBERTa-large v8) classifies 738K comparable pairs across 124 scrape jobs. None of these decisions are validated automatically. Misclassifications — bundle inflation, condition mismatches, wrong variants, accessory-vs-product confusion — go undetected unless a human runs `/evaluate-comps` on a specific listing.

## Solution

Train a fine-tuned Qwen3-8B model that audits ONNX decisions on production pairs, flagging misclassifications. Corrections are bit-flipped labels fed back into the cross-encoder training dataset, creating a self-improving loop.

## Architecture

Two-model system:

```
RoBERTa ONNX (production classifier, real-time, 6ms/pair)
        │
        │ comparable pairs + SimilarityScore
        ▼
Qwen3-8B evaluator (offline pipeline, ~60ms/pair, 4-bit quantized)
        │
        ├── "correct" → no action
        └── "misclassification" + error_type + reasoning
                │
                ▼
        Bit-flip ONNX label → training correction
                │
                ▼
        Merge into cross-encoder dataset → retrain RoBERTa
```

No separate labeler model needed. When the evaluator flags a misclassification, the correct label is the opposite of what the ONNX model predicted.

## Data Pipeline

### Step 1: Extract ONNX Decisions (collect.py)

Query `ListingRelationships` joined with `Listings` to extract pairs the ONNX model classified as comparable. Stratified sampling to maximize training signal:

| Tier | Selection criteria | Target count | Rationale |
|------|-------------------|-------------|-----------|
| 1 | Low confidence (SimilarityScore 0.50–0.75) | ~2,000 | Highest error rate |
| 2 | Known weak categories (watches, luxury bags, cycling, Birkenstock, vintage Levi's) | ~2,000 | Categories with F1 < 0.85 in v8 eval |
| 3 | High confidence random sample | ~1,000 | Calibration — evaluator must learn most decisions are correct |

Total: ~5,000 pairs.

Output: `data/evaluator_pairs_raw.csv` with columns: `listing_id_a, listing_id_b, title_a, desc_a, title_b, desc_b, onnx_label, similarity_score, search_term`.

### Step 2: GPT Audit (audit.py)

Send each pair + ONNX decision to GPT-5-mini with structured output schema:

```python
class AuditResult(BaseModel):
    verdict: str       # "correct" or "misclassification"
    correct_label: int # 0 or 1
    error_type: str    # null, "bundle_inflation", "condition_mismatch",
                       # "wrong_variant", "accessory_vs_product", "price_outlier"
    reasoning: str     # Brief explanation
```

Uses existing API key pattern (loaded from `AIOMarketMaker.Etl/local.settings.json`). Async with `Semaphore(10)` for rate limiting.

Cost: ~$10–15 for 5K pairs.

Output: `data/evaluator_audit_gpt.csv`. Split 90/10 into train/test.

### Step 3: Fine-tune Qwen3-8B (train.py)

QLoRA fine-tune using Unsloth on RTX 5070 Ti (16GB VRAM):

| Parameter | Value |
|-----------|-------|
| Base model | Qwen/Qwen3-8B |
| Quantization | 4-bit (QLoRA) |
| LoRA rank | 16 |
| LoRA alpha | 32 |
| Learning rate | 2e-4 |
| Epochs | 3 |
| Max sequence length | 512 |
| VRAM usage | ~6GB model + ~4GB training overhead |

Input format (chat template):
```
<|user|>
The classifier labeled this pair as COMPARABLE (confidence: 0.73).

LISTING A:
Title: {title_a}
Description: {desc_a}

LISTING B:
Title: {title_b}
Description: {desc_b}

Is the classifier's decision correct?
<|assistant|>
{"verdict": "misclassification", "correct_label": 0, "error_type": "bundle_inflation", "reasoning": "Comp includes Magic Keyboard (~£300), active is tablet only"}
```

Output: LoRA adapter saved to `E:/Dev/ml-training/evaluator/v1/`, merged model exported for inference.

### Step 4: Validate (eval.py)

Run fine-tuned evaluator on held-out test split (~500 pairs). Compare against GPT ground truth.

Metrics:
- Overall accuracy on verdict (correct vs misclassification)
- Precision and recall per error_type
- Confusion matrix
- Disagreement analysis (where evaluator and GPT differ)

Output: `data/benchmarks/evaluator_v1_results.csv`.

### Step 5: Run on Production Data (run.py)

Batch inference on new comparable pairs after scrape completes. Load fine-tuned model, iterate over pairs, output flagged misclassifications.

Scope: comparable pairs only (IsComparable = 1), optionally filtered to low confidence.

Output: `data/evaluator_corrections.csv` — ready to merge into cross-encoder training dataset.

## Error Type Taxonomy

Maps directly to `/evaluate-comps` flags:

| error_type | Description | Example |
|-----------|-------------|---------|
| `bundle_inflation` | Matched bare product to product + expensive accessory | iPad vs iPad + Magic Keyboard |
| `condition_mismatch` | Matched different condition tiers within same eBay category | Grade A working vs cracked screen, both "Used" |
| `wrong_variant` | Matched different specs (storage, connectivity, size, reference number) | 128GB Wi-Fi vs 256GB Cellular |
| `accessory_vs_product` | Matched a peripheral/add-on to the main product | PS5 Disc Drive (~£80) vs PS5 Console (~£400) |
| `price_outlier` | Penny auction or extreme price anomaly paired normally | £5 sold vs £400 listing |

## File Structure

```
AIOMarketMaker.ML/Training/
├── evaluator/
│   ├── README.md
│   ├── collect.py      # Extract pairs from ListingRelationships + stratified sampling
│   ├── audit.py        # GPT-5-mini audit for ground truth labels
│   ├── train.py        # Unsloth QLoRA fine-tune of Qwen3-8B
│   ├── eval.py         # Validate against held-out test set
│   └── run.py          # Batch inference on new production data
├── data/
│   ├── evaluator_pairs_raw.csv
│   ├── evaluator_audit_gpt.csv
│   ├── evaluator_train.csv
│   ├── evaluator_test.csv
│   └── benchmarks/
│       └── evaluator_v1_results.csv
```

Follows CONVENTIONS.md. All CSVs gitignored.

## Tech Stack

| Component | Library |
|-----------|---------|
| Data extraction | pyodbc (existing pattern from v8/collect.py) |
| GPT audit | openai AsyncOpenAI + Pydantic structured outputs (existing v9 pattern) |
| Fine-tuning | Unsloth + QLoRA |
| Base model | Qwen/Qwen3-8B, 4-bit quantized |
| Inference | Unsloth or transformers (batch mode, no server) |
| Evaluation | sklearn metrics |

## Trained Model Location

```
E:/Dev/ml-training/evaluator/
├── v1/
│   ├── lora_adapter/    # LoRA weights
│   ├── merged/          # Full merged model (for inference)
│   └── quantized/       # 4-bit GGUF (optional, for llama.cpp)
```

## Inference Integration

**Phase 1 (this design):** Python batch script (`run.py`). Run after scrape completes. Results written to CSV for review and cross-encoder retraining.

**Future:** Local inference server (vllm/ollama) callable from .NET API for real-time evaluation during scrape runs.

## Success Criteria

1. Evaluator matches GPT-5-mini accuracy within 5% on held-out test set
2. Catches >80% of misclassifications GPT would catch (recall on "misclassification" verdict)
3. <20% false alarm rate (precision — don't flag correct decisions as wrong)
4. Inference speed <100ms per pair on RTX 5070 Ti
5. Full pipeline (collect → audit → train → eval → run) documented and reproducible

## Cost

| Item | Cost |
|------|------|
| GPT-5-mini audit of 5K pairs | ~$10–15 |
| Fine-tuning (local GPU) | $0 |
| Ongoing inference (local GPU) | $0 |
| Periodic GPT refresh (quarterly) | ~$10–15 |

## Risks

- **Qwen3-8B at 4-bit may struggle with subtle luxury goods distinctions** (watch reference numbers, Chanel sub-models). Mitigate: oversample these categories in training data, monitor per-category accuracy.
- **Feedback loop drift** if evaluator has systematic blind spots. Mitigate: periodic human spot-checks of evaluator "correct" verdicts, quarterly GPT refresh of training data.
- **Unsloth compatibility** with Qwen3-8B — verify before starting. Fallback: vanilla HuggingFace PEFT with gradient checkpointing.
