# Training Conventions

## Directory Structure

```
Training/
├── data/                    # All datasets, results, and artifacts (gitignored)
│   ├── labeled_pairs_v8.csv # Final merged dataset per version
│   ├── hard_pairs_v8.csv    # Intermediate mining outputs
│   ├── benchmarks/          # Evaluation result CSVs
│   └── ...
├── v8/                      # One directory per model version
│   ├── README.md            # Required: approach, results, key files
│   ├── collect.py           # Generate/mine new training pairs
│   ├── merge.py             # Combine new pairs with previous dataset
│   ├── train.py             # Train the model
│   ├── export.py            # Export to ONNX for production
│   └── eval.py              # Evaluate model quality
├── .gitignore               # Keeps large files out of git
└── CONVENTIONS.md           # This file
```

## Standard Script Names

Every version folder uses the same script names. Not all are required — only include what the version needs.

| Script | Purpose | Reads from | Writes to |
|--------|---------|------------|-----------|
| `collect.py` | Generate or mine new training pairs | Database, existing data | `../data/<artifact>.csv` |
| `merge.py` | Combine new + previous dataset, deduplicate | `../data/` | `../data/labeled_pairs_vN.csv` |
| `train.py` | Train the model | `../data/labeled_pairs_vN.csv` | Model on E: drive |
| `export.py` | Export trained model to ONNX | Model on E: drive | ONNX on E: drive |
| `eval.py` | Evaluate model, benchmark against baselines | `../data/`, model | `../data/benchmarks/` |

Additional scripts follow the pattern `eval_<variant>.py` (e.g., `eval_cot.py`, `eval_live.py`) or go in a subfolder like `augment/` for category-specific pair generators.

## Data References

Scripts reference data via `../data/` — never duplicate CSVs into version folders.

```python
# Good
pairs = pd.read_csv("../data/labeled_pairs_v8.csv")

# Bad — creates duplicates
pairs = pd.read_csv("labeled_pairs_v8.csv")
```

Output datasets are named with the version they belong to: `labeled_pairs_v9.csv`, `hard_pairs_v9.csv`.

## README Template

Every version folder must have a `README.md` with these sections:

```markdown
# VN: <Short Description>

## Goal
What this version improves over the previous one.

## Approach
Technical approach — model architecture, data strategy, key changes.

## Data Pipeline
How data flows from collection → merge → training dataset.

## Results
| Metric | Previous | This Version |
|--------|----------|--------------|
| F1 macro | X.XXX | X.XXX |

## Key Files
| File | Purpose |
|------|---------|
| collect.py | ... |

## Limitations
What this version doesn't solve.
```

## What Gets Committed

The `.gitignore` keeps large artifacts out of git:
- **Committed**: `.py`, `.md`, `.txt`, `.gitignore`
- **Gitignored**: `*.csv`, `*.json`, `*.pt`, `*.log`, `__pycache__/`

## Trained Models

Trained model weights live on the E: drive, not in the repo:
- PyTorch: `E:/Dev/ml-training/variant-classifier/vN/pytorch/`
- ONNX: `E:/Dev/ml-training/variant-classifier/vN/onnx/`

## Starting a New Version

```bash
mkdir Training/v10
cp Training/v8/README.md Training/v10/README.md  # Start from template
# Edit README, create collect.py, etc.
```
