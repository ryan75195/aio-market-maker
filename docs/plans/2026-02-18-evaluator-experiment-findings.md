# Evaluator LLM Experiment — Findings & Lessons Learned

## Goal

Train a local LLM (Qwen3) to audit the production ONNX cross-encoder's (RoBERTa-large v8, F1=0.913) pair classification decisions. The evaluator would flag misclassifications for retraining, creating a self-improving loop without ongoing cloud API costs.

## What We Built

- **Data pipeline**: `collect.py` → `audit.py` → `train.py` → `eval.py` → `run.py`
- **GPT-5-mini audit**: 4,995 production pairs labeled (verdict: correct/misclassification + error_type + reasoning)
- **Dataset split**: 4,495 train / 500 test (stratified: ~65% correct, ~35% misclassification)
- **Training**: Unsloth QLoRA fine-tuning on RTX 5070 Ti (16GB VRAM)

## Models Trained

### v1: Qwen3-8B (generative)
- LoRA rank 16, alpha 32, 4-bit quantized, 3 epochs
- Saved at `E:/Dev/ml-training/evaluator/v1/merged`
- **Eval incomplete** — inference too slow at ~58s/pair (raw HuggingFace generate). Would take ~8 hours for 500 test pairs.

### v2: Qwen3-0.6B (generative)
- Same hyperparameters, 3 epochs, 59 minutes training time
- Final train loss: 1.63
- Saved at `E:/Dev/ml-training/evaluator/v2/merged` — **DISCARDED** (see results)

## v2 (0.6B) Results — 500 Test Pairs

| Metric | Value |
|--------|-------|
| Overall accuracy | 66.8% |
| Misclassification recall | **4.6%** (8/173) |
| Misclassification precision | 88.9% (8/9) |
| Misclassification F1 | 0.088 |
| Inference speed | 4,424ms/pair |

The model predicts "correct" for nearly every pair. It catches only 8 out of 173 actual misclassifications. This is marginally better than a naive baseline that always predicts "correct" (which would score 65.4%).

## Why the Generative LLM Approach Failed

### 1. Inference speed is fundamentally too slow
Generative LLMs produce output **token by token**. Each pair requires ~50-100 tokens of JSON response, each requiring a full forward pass. This makes generative inference 50-100x slower than a classifier that does a single forward pass.

| Runtime | 0.6B speed | 1M pairs |
|---------|-----------|----------|
| Raw HuggingFace (current) | 4.5s/pair | 52 days |
| llama.cpp GGUF (optimized) | ~150ms/pair | 42 hours |
| vLLM batched (best case) | ~30ms/pair | 8 hours |

Even the fastest realistic setup doesn't make this practical for production-scale auditing.

### 2. Small model (0.6B) can't learn the task
The 0.6B model lacks capacity for the nuanced reasoning required (condition grading, variant disambiguation, bundle detection). The 8B model may perform better but is proportionally slower.

### 3. The classifier already sees descriptions
The existing RoBERTa classifier receives `"title | description_first_200_chars"` as input. The evaluator's main hypothesized advantage — seeing descriptions — is not actually a gap.

## Key Insight: Classification Head vs Generative Output

A local LLM with a **classification head** (single forward pass, binary output) would be architecturally identical to the existing RoBERTa cross-encoder, just with a different backbone. The "world knowledge" advantage of a larger pretrained model is marginal when the existing 355M-param RoBERTa was also pretrained on large text corpora.

## Recommended Path Forward

### Immediate: Retrain v9 classifier with corrected labels
The GPT audit identified ~1,558 misclassified pairs with corrected labels. These are ready-made hard training examples:
- Add corrected pairs to the existing 143K training set
- Retrain RoBERTa-large v9
- This is the same hard-negative mining that boosted v7→v8 (F1 0.903→0.913)

### Ongoing: GPT batch API for periodic audits
- Cost: ~$0.002/pair via GPT-4o-mini batch API
- 10K pairs ≈ $20, 100K pairs ≈ $200
- Cheaper and more accurate than maintaining a local LLM
- Feed corrected labels back into training data for continuous improvement

### Active learning loop
```
Run classifier on new pairs
    → Sample low-confidence pairs (near decision boundary)
    → GPT-4o-mini batch API audit
    → Add corrected labels to training set
    → Retrain classifier
    → Repeat
```

## What We Keep

- **GPT audit data** (`evaluator_audit_gpt.csv`) — 4,995 labeled pairs, directly usable for v9 training
- **Training/eval scripts** — reusable infrastructure if we revisit with a classification-head approach
- **Benchmark results** (`evaluator_v2-06b_results.csv`) — documents the experiment outcome

## What We Discard

- **v2 model weights** (`E:/Dev/ml-training/evaluator/v2/`) — 0.6B model, 4.6% recall, not useful
- **v1 model weights** (`E:/Dev/ml-training/evaluator/v1/`) — 8B model, never fully evaluated, too slow for production

## Artifacts

| File | Purpose | Status |
|------|---------|--------|
| `Training/evaluator/collect.py` | Extract pairs from DB for auditing | Keep |
| `Training/evaluator/audit.py` | GPT-5-mini batch labeling | Keep |
| `Training/evaluator/train.py` | Unsloth QLoRA training | Keep |
| `Training/evaluator/eval.py` | Model evaluation | Keep |
| `Training/evaluator/run.py` | Production batch inference | Keep |
| `Training/data/evaluator_audit_gpt.csv` | 4,995 GPT-labeled pairs | Keep — input for v9 |
| `Training/data/evaluator_test.csv` | 500-pair test split | Keep |
| `Training/data/evaluator_train.csv` | 4,495-pair train split | Keep |
| `Training/data/benchmarks/evaluator_v2-06b_results.csv` | 0.6B eval results | Keep — documents experiment |
