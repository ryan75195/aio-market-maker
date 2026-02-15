# V6: Banded Hard-Negative Strategy + roberta-large

## Evolution from V5

V5 had two problems: (1) all pairs came from high-similarity Pinecone neighbors, so the model never practiced on moderate-difficulty cases, and (2) bert-base lacked capacity. V6 fixes both.

## Approach

**Data collection** (`collect_v6.py`, piloted with `collect_v6_pilot.py`):

Four pair generation strategies to control difficulty:

| Source | Strategy | Pairs | Positive Rate |
|--------|----------|-------|---------------|
| `banded` | Pinecone neighbors in 3 similarity bands (0.90+, 0.80-0.90, 0.70-0.80) | 44,723 | 30.4% |
| `random_same_cat` | Random pairs from same category (not Pinecone neighbors) | 26,164 | 7.7% |
| `desc_overlap` | Listings with near-identical descriptions across categories | 17,500 | 8.4% |
| `cross_category` | Pinecone query without job filter, keep cross-category matches | 475 | 0.0% |
| `v5_original` | Carried forward from v5 | 24,260 | 29.7% |

Total: **113,122 labeled pairs** across 124 categories.

**Training** (`train_v5.py` — same script, different config):
- Model: `roberta-large` (355M params, up from bert-base 110M)
- LR: 1e-5 (2e-5 caused oscillation/crash after warmup)
- bf16 mixed precision (fp16 unstable with some architectures)
- batch_size=16, max_length=256

## Results

- **F1 = 0.903 macro, accuracy = 93.4%**
- Significant improvement over v5 (F1 0.871 → 0.903)
- 6 categories still below F1 0.70 (TaylorMade Driver, Birkenstock, Cartier, Pandora, Omega, Roland)

## Key Files

| File | Purpose |
|------|---------|
| `collect_v6_pilot.py` | Pilot run (5K pairs) to validate banded strategy |
| `collect_v6.py` | Full collection pipeline |
| `train_v5.py` | Training script (reused from v5, parameterized) |
| `labeled_pairs_v6.csv` | New v6 pairs only (~89K) |
| `labeled_pairs_v6_merged.csv` | V5 + V6 merged (**113K pairs — training input**) |
| `v6_pilot_pairs.csv` | Pilot batch for quality check |

## Limitations

- **Too few hard negatives**: Only ~2% of pairs are "same product, different spec" — the hardest case
- **Cross-category signal leakage**: Categories like AirPods (49% positive) teach "same name = comparable", overriding spec-level signal for categories like Mac Mini (8.6% positive)
- **No spec-aware pair generation**: Pairs selected by embedding similarity, not by spec differences
- Works well for commoditized products (PS5, AirPods) but unreliable for spec-heavy products (computers, laptops) and text-variant products (collectibles, fashion)

## Trained Models

| Path | Notes |
|------|-------|
| `E:/Dev/ml-training/variant-classifier/model_v6/` | LR=2e-5, oscillated |
| `E:/Dev/ml-training/variant-classifier/model_v6_pilot/` | Pilot subset |
| `E:/Dev/ml-training/variant-classifier/model_v6_lr1e5/` | **Best model, F1=0.903** |
| `E:/Dev/ml-training/variant-classifier/model_v6_onnx/` | ONNX export for production |

## Lessons Learned

- roberta-large needs LR=1e-5, not 2e-5
- DeBERTa fp16 causes NaN — use bf16 or avoid entirely
- Banded similarity strategy effectively controls pair difficulty
- 113K pairs is enough volume, but the difficulty distribution is wrong
