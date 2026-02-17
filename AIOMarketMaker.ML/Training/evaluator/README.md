# Evaluator: ONNX Cross-Encoder QA

## Goal

Fine-tuned Qwen3-8B model that audits RoBERTa ONNX cross-encoder decisions
on production listing pairs. Flags misclassifications so corrected labels
can be fed back into cross-encoder training.

## Pipeline

```
collect.py → audit.py → train.py → eval.py → run.py
   │             │           │          │          │
   │             │           │          │          └─ Batch inference on new pairs
   │             │           │          └─ Validate against test set
   │             │           └─ Unsloth QLoRA fine-tune
   │             └─ GPT-5-mini audit (ground truth)
   └─ Extract production pairs from DB
```

## Quick Start

```bash
# 1. Extract pairs (free, ~30s)
py -3.12 collect.py --dry-run        # preview counts
py -3.12 collect.py                  # write CSV

# 2. GPT audit (~$10-15, ~5 min)
py -3.12 audit.py --dry-run          # preview cost
py -3.12 audit.py                    # run audit

# 3. Train (~1-2 hours on RTX 5070 Ti)
py -3.12 train.py

# 4. Evaluate
py -3.12 eval.py

# 5. Run on new production data
py -3.12 run.py
```

## Model Location

- LoRA adapter: `E:/Dev/ml-training/evaluator/v1/lora_adapter/`
- Merged model: `E:/Dev/ml-training/evaluator/v1/merged/`

## Error Types

| error_type | Description |
|-----------|-------------|
| `bundle_inflation` | Bare product matched to product + expensive accessory |
| `condition_mismatch` | Different condition tiers within same eBay category |
| `wrong_variant` | Different specs (storage, connectivity, size) |
| `accessory_vs_product` | Peripheral matched to main product |
| `price_outlier` | Penny auction or extreme price anomaly |
