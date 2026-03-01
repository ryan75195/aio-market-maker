# Qwen3-4B Fine-Tuning Experiment: Post-Mortem

**Date:** 2026-03-01
**Outcome:** Failed. Model scored 62% on audit benchmark (target: 90%). Integration work not started.

## What We Were Trying to Do

The ONNX RoBERTa variant classifier was making real mistakes in production. It couldn't distinguish:
- A replacement PS5 disc drive (accessory, ~£80) from a full PS5 console (~£400)
- Portuguese Pokemon boxes from English ones (different market, different price)
- Charizard Premium Collection (~£70) from Ultra-Premium Collection (~£150)
- "Grade A" condition from "for parts/faulty"

These failures require world knowledge that RoBERTa doesn't have. A 4B-parameter LLM already knows what a disc drive is, what Portuguese looks like, and what "Ultra-Premium" means. The plan was to fine-tune Qwen3-4B with QLoRA so it learns the task format while keeping its world knowledge.

The fine-tuned model would also serve as a training data generator — producing cleaner labels to eventually retrain a faster RoBERTa.

## What Happened

### Training

- **Model:** Qwen3-4B with QLoRA (rank=16, alpha=32)
- **Data:** 141K labeled pairs from `labeled_pairs_v10.csv`
- **Hardware:** RTX 5070 Ti, 16GB VRAM
- **Time:** ~9 hours, 1 epoch
- **Final eval loss:** converged normally, no signs of training failure

### Evaluation

Ran the model against 50 human-audited pairs (balanced: 25 misclassifications, 25 correct predictions).

**Result: 62% accuracy.** 19 wrong out of 50.

Two dominant failure modes:
1. **Condition blindness** (7 errors) — Model said "same" for pairs like Grade A vs For Parts, Sealed vs Heavily Used
2. **"Absence = missing" fallacy** (7 errors) — Model said "different" because one listing didn't mention an accessory, even though both were the same product

### Root Cause Investigation

Traced the training data lineage through all version scripts (v5 through v10):

| Version | Source | Volume |
|---------|--------|--------|
| v5 | GPT-5-mini labels | 24K pairs |
| v6 | GPT-5-mini labels | 89K pairs |
| v7 | v6 + regex-mined hard pairs | +16K |
| v8 | v7 + more regex-mined pairs | +29K |
| v9 | v8 + manual corrections | +487 |
| v10 | v9 refreshed | 143K total |

**~79% of training labels (113K pairs) came from GPT-5-mini.**

The GPT-5-mini system prompt had two critical flaws:

1. **"Cosmetic condition differences are acceptable"** — This taught the model that condition doesn't matter. A sealed item and a broken one? Same product. This is wrong for pricing — condition is the biggest price driver after product identity.

2. **Binary completeness rule** — If one listing mentioned an accessory and the other didn't, GPT marked them as different. But sellers often omit details. "iPhone 15 Pro Max" and "iPhone 15 Pro Max with charger" are the same product.

These prompt flaws were propagated through every version of the dataset. The ONNX RoBERTa model was also trained on these labels, inheriting the same blind spots.

### Fix Attempts

**1. System prompt injection at inference time**

Added a corrective system prompt telling the model to treat condition differences as deal-breakers.

Result: 62% → 64%. Fixed 5 errors but introduced 3 regressions. The LoRA weights (trained on 141K examples) overpower inference-time instructions.

**2. Regex-based label correction**

Built an audit script scanning all label=1 pairs for detectable errors:

| Pattern | Corrections Found |
|---------|-------------------|
| Condition gap (2+ tiers) | 634 |
| Quantity mismatch (bundle vs single) | 438 |
| Accessory vs product | 181 |
| Storage mismatch (256GB vs 512GB) | 49 |
| CPU/chip mismatch (M3 vs M3 Pro) | 43 |
| Product tier (PS5 Slim vs original) | 18 |
| **Total unique** | **1,347** |

1,347 corrections out of 143K pairs is < 1%. The regex only catches cases where the signal is explicit in title text. The vast majority of condition-based mislabels are silent — no keywords, just different prices for different conditions.

**3. Continued LoRA on corrections (assessed, not attempted)**

~70 correction examples can't override patterns learned from thousands of gradient steps encoding the opposite behavior. Would need 5-10K correction examples minimum.

## Why It Failed

The model has the world knowledge we wanted. It knows what a disc drive is. It knows condition matters. But we trained it on 113K labels that explicitly said to ignore those distinctions.

**We suppressed the world knowledge we were trying to leverage.**

The fine-tuning route isn't wrong — the execution was. We skipped the most important step: validating the training labels before spending 9 hours on a GPU.

## Lessons Learned

1. **The labeling prompt IS the model.** When you use an LLM to generate training labels, the system prompt's assumptions become the fine-tuned model's assumptions. Every design decision in the prompt — what counts as "acceptable" variation — becomes a learned behavior.

2. **Post-hoc fixes can't undo systematic label errors.** System prompt injection, regex corrections, and continued training all hit walls. You can't patch your way out of a dataset-level problem.

3. **Data quality is the bottleneck, not model capacity.** Qwen3-4B has plenty of capacity for 120 product categories. The ceiling is set entirely by label quality.

4. **Know your data lineage.** We initially thought labels came from the ONNX model. They didn't — 79% were GPT-5-mini. Tracing provenance earlier would have caught the prompt flaw before training.

5. **Evaluation-first saves time.** The plan's 90% accuracy gate stopped us at Phase 1, before spending days on GGUF conversion, llama.cpp, and C# integration.

6. **Breadth vs depth tradeoff.** 120 categories is fine for model capacity, but it means the labeling prompt needs to handle 120 different domains correctly. One flawed rule corrupts labels across all categories.

7. **Validate a sample before bulk labeling.** Before running GPT on 143K pairs, we should have labeled 100 pairs, manually reviewed them, and caught the condition blindness issue. That would have cost $0.02 instead of $15 + 9 hours of GPU time.

## What's Still True

- The ONNX RoBERTa classifier still has the production failures we started with
- A fine-tuned LLM is still the right architecture for this task
- The audit script and regex patterns we built are reusable for validating any future dataset
- The training infrastructure (Unsloth, QLoRA, eval benchmark) works and is ready for a retry

## Paths Forward

**A. Fix the labeling prompt, re-label everything (~$10-20)**
Rewrite the GPT-5-mini prompt to treat condition, storage, and accessories as deal-breakers. Re-run on all 143K pairs. Fixes root cause at scale. Risk: GPT-5-mini might still make mistakes on subtle cases.

**B. Focus categories + GPT-4o labels (~$30-50)**
Pick 10-15 highest-value categories. Re-label just those with GPT-4o (better quality). Train on ~30-50K cleaner pairs. A model that's 95% accurate on top categories is more valuable than 75% across 120.

**C. Use GPT-4o directly as production classifier**
Skip distillation entirely. Pay per classification. Get accuracy now. Cost: ~$0.001/pair with GPT-4o-mini, ~$0.01/pair with GPT-4o.

**D. Hybrid cascade**
ONNX for high-confidence pairs (fast, free). GPT-4o for low-confidence or high-value pairs. Best of both worlds but more complex architecture.

## Files

| File | Purpose |
|------|---------|
| `AIOMarketMaker.ML/Training/finetune_qwen3.py` | Training script (QLoRA on Qwen3-4B) |
| `AIOMarketMaker.ML/Training/eval_finetuned.py` | Audit benchmark evaluation |
| `AIOMarketMaker.ML/Training/eval_results.txt` | Full eval output with reasoning |
| `AIOMarketMaker.ML/Training/v11/audit_v11.py` | Regex audit for label corrections |
| `AIOMarketMaker.ML/Training/v11/scan_patterns.py` | Pattern discovery across uncaught pairs |
| `AIOMarketMaker.ML/Training/data/labeled_pairs_v11.csv` | Copy of v10, corrections not applied |
| `AIOMarketMaker.ML/Training/output/qwen3-variant-classifier/` | Trained LoRA adapter (62% accuracy) |
| `docs/plans/2026-03-01-llm-classifier-integration.md` | Original integration plan (not executed) |
