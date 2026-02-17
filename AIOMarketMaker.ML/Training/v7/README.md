# V7: Spec-Aware Hard Negative Mining

## Evolution from V6

V6 achieved F1=0.903 but fails on spec-sensitive products. Analysis showed only ~2% of training pairs are "hard negatives" — pairs where two listings share the same base product but differ in functional specs (RAM, storage, CPU, screen size). The model learns "same product name = probably comparable" but never gets enough practice on "same product, different spec = NOT comparable."

V7 fixes this by mining hard negatives directly from the database using spec fingerprinting.

## Approach

Two-pronged data augmentation targeting the weakness:

### Prong 1: Regex Spec Mining (free, instant)

For spec-heavy products (~60% of categories — electronics, tools, appliances):

1. **Extract spec fingerprints** from every listing title using universal regex patterns:
   - Capacity: `(\d+)\s*(GB|TB|MB)`
   - Speed: `(\d+\.?\d*)\s*(GHz|MHz)`
   - CPU: `(i[3579]|M[1-4]|Ryzen\s*\d|A\d{2})`
   - Screen: `(\d+\.?\d*)\s*("|inch|mm)`
   - RAM: `(\d+)\s*GB\s*(RAM|DDR)`
   - Size: `(XS|S|M|L|XL|UK\s*\d+|US\s*\d+|EU\s*\d+)`
   - Generation: `(Gen\s*\d|Mark\s*\d|v\d+|[2-9]th\s*Gen)`
   - Power: `(\d+)\s*W\b`
   - Quantity: `(pair|set\s*of\s*\d+|bundle|lot)`

2. **Group listings by category + fingerprint**
3. **Generate pairs by fingerprint distance**:
   - Fingerprints differ by 1-2 tokens → **hard negative** (label=0)
   - Same fingerprint → **hard positive** (label=1)
   - Differ by 3+ tokens → skip (easy negative, already well-covered)
4. **Label directly** — no GPT needed, differences are unambiguous
5. **Cap**: 500 hard negatives + 200 hard positives per category

Target: ~15-20K new hard pairs across all spec-sensitive categories.

### Prong 2: GPT-Labeled Hard Negatives (future, ~$10-15)

For text-variant products (~40% — collectibles, fashion, luxury):
- Variant differences are in words, not numbers (Charizard vs Pikachu, Arizona vs Boston)
- Regex can't extract these — need GPT to identify the differences
- Use embedding similarity (0.85-0.92 range) to find borderline pairs, send only those to GPT
- Target: ~8K additional pairs

## Data Pipeline

```
mine_hard_pairs.py
  ├── Queries LocalDB for all listings grouped by ScrapeJob
  ├── Extracts spec fingerprints via regex
  ├── Groups by category + fingerprint
  ├── Generates cross-group pairs (hard negatives)
  ├── Generates within-group pairs (hard positives)
  └── Outputs: hard_pairs_v7.csv

merge_v7.py
  ├── Reads v6/labeled_pairs_v6_merged.csv (113K)
  ├── Reads hard_pairs_v7.csv (~15-20K)
  ├── Deduplicates by (anchor_id, neighbor_id)
  └── Outputs: labeled_pairs_v7.csv (~130-135K)
```

## Training

Same script and hyperparameters as v6 (roberta-large, LR=1e-5, bf16, batch_size=16, max_length=256). The improvement comes entirely from better data composition, not model changes.

## Expected Impact

| Metric | V6 | V7 (target) |
|--------|-----|-------------|
| Hard negatives (% of dataset) | ~2% | ~15% |
| F1 macro (overall) | 0.903 | 0.92+ |
| Spec-sensitive category accuracy | Poor (Mac Mini, laptops) | Strong |
| Text-variant category accuracy | Moderate | Moderate (Prong 2 needed) |

## Key Files

| File | Purpose |
|------|---------|
| `mine_hard_pairs.py` | Spec fingerprint mining from LocalDB |
| `merge_v7.py` | Merge mined pairs with v6 dataset |
| `README.md` | This file |
