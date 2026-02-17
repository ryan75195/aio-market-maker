# V9: GPT Benchmarking + Targeted Augmentation (In Progress)

## Goal

Two-pronged investigation:
1. **Benchmark GPT-5-mini** as a potential replacement or fallback for the ONNX classifier
2. **Targeted data augmentation** for weak categories identified by the evaluate-comps analysis

## Approach

### GPT Benchmarking

Tested three GPT-5-mini prompting strategies against 486 human-labeled pairs:
- **Direct** (`eval.py`): Single structured output call with Pydantic schema
- **Chain-of-thought** (`eval_cot.py`): Forces reasoning about product/condition/bundle before label
- **2-stage** (`eval_2stage.py`): Stage 1 (Analyzer) extracts facts, Stage 2 (Judge) decides

### Targeted Augmentation

Used evaluate-comps analysis to find categories where the classifier makes mistakes, then generated training pairs from the flagged comparisons:
- `augment/ps5.py` — PlayStation 5 variants
- `augment/rolex.py` — Rolex Submariner ref numbers
- `augment/luxury.py` — Luxury goods (Cartier, Omega, etc.)
- `augment/neverfull.py` — Louis Vuitton Neverfull sizes
- `augment/brompton.py` — Brompton bikes + Canada Goose
- `augment/electronics.py` — MacBook Pro, Sony A7 IV, iPhone 15, Peloton

### Label Fixes

`fix_labels.py` corrects 74 labels identified as wrong in the v9 dataset (69 false positives, 5 false negatives) based on disagreement analysis between model and human labels.

## Data Pipeline

```
collect.py
  ├── Reads evaluate-comps analysis for specific listings
  ├── Flagged comps → label=0, clean comps → label=1
  └── Outputs: ../data/labeled_pairs_v9.csv (initial seed)

augment/*.py
  ├── Each reads evaluate-comps for a specific category
  └── Appends to ../data/labeled_pairs_v9.csv

fix_labels.py
  ├── Reads ../data/labeled_pairs_v9.csv
  ├── Flips 74 identified wrong labels
  └── Outputs: ../data/labeled_pairs_v9.csv (corrected)
```

## Results

Benchmark results in `../data/benchmarks/`:

| Strategy | Accuracy | Notes |
|----------|----------|-------|
| Direct | See `gpt5mini_results.csv` | Baseline |
| CoT | See `gpt5mini_cot_results.csv` | Forced reasoning |
| 2-stage | See `gpt5mini_2stage_results.csv` | Analyzer + Judge |

## Key Files

| File | Purpose |
|------|---------|
| `collect.py` | Generate seed pairs from evaluate-comps |
| `fix_labels.py` | Correct 74 wrong labels |
| `eval.py` | GPT-5-mini direct benchmark |
| `eval_cot.py` | GPT-5-mini chain-of-thought benchmark |
| `eval_2stage.py` | GPT-5-mini 2-stage benchmark |
| `augment/*.py` | Category-specific pair generators |

## Limitations

- Training not yet run — dataset still being built
- Augmented pairs are from a small sample of evaluate-comps output
- No merge with v8 dataset yet
