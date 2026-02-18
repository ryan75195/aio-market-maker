# Variant Classifier v9 Experiment — Findings

**Date:** 2026-02-18
**Status:** Concluded — v8 remains production model

## Experiment Goal

Test whether longer context (512 tokens vs v8's 256) and 2K label corrections improve F1.

## Configuration

- Base: `FacebookAI/roberta-large`
- Max tokens: 512 (v8: 256)
- Batch size: 8 (v8: 16) — halved due to memory pressure from longer sequences
- Gradient accumulation: 2 (effective batch = 16)
- LR: 1e-5 (same as v8)
- Training data: 143K pairs (v8's 129K + 14K hard-mined, plus ~2K label corrections)
- Early stopping: patience=15, eval_steps=500

## Results

| Metric | v9 | v8 | Delta |
|--------|----|----|-------|
| Test F1 macro | 0.9053 | 0.913 | -0.008 |
| Test accuracy | 0.9232 | 0.929 | -0.006 |
| Best eval F1 | 0.9108 | — | — |
| Epochs completed | 3.39/5.0 | — | Early stopped |
| Training time | 275 min | — | — |

## Key Findings

### 1. Longer context hurts performance

512 tokens performed worse than 256. Likely causes:
- Extra description text introduces noise (boilerplate, seller templates)
- The key signals (model numbers, specs, sizes) are in the first 256 tokens
- Halved batch size may reduce training stability

### 2. Label noise is the ceiling, not architecture

Across v5–v9, F1 oscillates around 0.90–0.91 regardless of architecture changes.
Only direct label corrections (v7→v8: +14K hard-mined pairs) produced significant gains.

### 3. Disagreement mining reveals ~17% label noise

Running v8 model on the full 143K training set found 23,827 disagreements (16.7%).
Worst categories by disagreement rate:
- LG C3 OLED TV: 56.3%
- Brembo Brake Caliper: 46.0%
- Vintage Omega Speedmaster: 45.8%
- Gibson Les Paul Standard: 44.6%
- Samsung QN90C QLED TV: 44.0%

High-confidence disagreements are overwhelmingly correct — the labels are wrong:
- "20V drill" vs "18V drill" labeled same (clearly different)
- "Yeezy Slate" vs "Yeezy Onyx" labeled same (different colorways)
- "Dr Martens Black Smooth" vs "Dr Martens Victorian Purple Floral" labeled same

### 4. Category-specific patterns

- **PS5:** Most disagreements are label errors (model correctly identifies same-variant pairs mislabeled as different)
- **Nintendo Switch:** Disagreements are mostly model weakness — it can't distinguish special editions (Pokemon Edition vs standard) which sell at different prices
- **Watches/luxury goods:** High disagreement rates due to subtle reference number differences

## Recommended Next Step

Use LLM (gpt-4o-mini) to reclassify the 23,827 disagreements, then retrain as v10.
The disagreement mining script (`find_disagreements.py`) and output (`disagreements.csv`)
are preserved in this directory for this purpose.

## Deleted Experiment Scripts

The following throwaway scripts were created during prompt engineering for LLM-based
relationship validation and have been removed from the working directory:

- `test_nano_*.py` (6 files) — gpt-5-nano prompt iterations testing reasoning_effort levels
- `test_4omini_*.py` (5 files) — gpt-4o-mini prompt versions v4-v7 for relationship validation
- `analyze_relationships*.py` (3 files) — Bulk LLM runs validating listing relationships
- `analyze_description_noise.py` — Description noise pattern analysis
- `build_v10_from_analysis.py` — Dataset builder from LLM analysis results
- `sample_descriptions_500.txt` — Sample description output

Key learnings from those experiments (preserved in MEMORY.md):
- gpt-5-nano `reasoning_effort="minimal"` is unreliable; `"low"` is the sweet spot
- gpt-4o-mini is most reliable for structured JSON classification (0% parse errors)
- Prompt engineering: emphatic rules at top, explicit examples of what NOT to do
