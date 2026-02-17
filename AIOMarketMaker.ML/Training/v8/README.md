# V8: Extended Hard Negative Mining (Production)

## Goal

Improve coverage of non-electronic product categories that v7 handled poorly. V7's regex spec mining targeted electronics (RAM, CPU, storage) but missed jewelry, golf clubs, cycling, luxury bags, vinyl, watches, and other text-variant products.

## Approach

Extended the P0/P1 regex hard negative mining with category-specific patterns:
- **Jewelry/Watches**: carat, metal purity (14K/18K/24K), stone type, case size (mm)
- **Golf**: flex (Regular/Stiff/X-Stiff), loft angle, shaft material
- **Cycling**: frame size, wheel size, groupset (Ultegra/105/Dura-Ace)
- **Luxury bags**: size variants (PM/MM/GM), material (Canvas/Leather)
- **Other**: LEGO set numbers, vinyl pressings, guitar body style, drum mesh/mylar

Same model architecture as v7 (roberta-large, LR=1e-5, bf16, batch_size=16, max_length=256). Improvement comes entirely from better data composition.

## Data Pipeline

```
collect.py (mine_hard_pairs)
  ├── Queries LocalDB for listings grouped by ScrapeJob
  ├── Extracts spec fingerprints via extended regex (P0 universal + P1 category-specific)
  ├── Generates cross-group pairs (hard negatives) + within-group pairs (hard positives)
  └── Outputs: ../data/hard_pairs_v8.csv (~14K pairs)

merge.py
  ├── Reads ../data/labeled_pairs_v7.csv (129K pairs)
  ├── Reads ../data/hard_pairs_v8.csv
  ├── Applies regex audit to flip incorrect GPT labels
  ├── Deduplicates by (anchor_id, neighbor_id)
  └── Outputs: ../data/labeled_pairs_v8.csv (143K pairs)
```

## Results

| Metric | V7 | V8 |
|--------|-----|-----|
| F1 macro | 0.920 | 0.913 |
| Accuracy | 93.4% | 92.9% |

F1 dropped slightly overall but category coverage improved dramatically:

| Category | V7 F1 | V8 F1 |
|----------|-------|-------|
| TaylorMade | 0.493 | 0.937 |
| Pandora | 0.687 | 0.926 |
| Roland TD-17 | 0.697 | 0.920 |
| Omega | 0.687 | 0.901 |
| Birkenstock | 0.656 | 0.838 |
| Cartier | 0.651 | 0.846 |

## Key Files

| File | Purpose |
|------|---------|
| `collect.py` | Extended hard pair mining with P0/P1 regex patterns |
| `merge.py` | Merge v7 dataset + v8 hard pairs with regex audit |
| `train.py` | Train roberta-large cross-encoder |
| `export.py` | Export to ONNX (use `dynamo=False` on torch 2.10+) |

## Limitations

- Overall F1 slightly lower than v7 (0.913 vs 0.920) — tradeoff for better category coverage
- Remaining weak categories (F1 < 0.80): Specialized Tarmac, Dyson V15, Yamaha P-125, Trek Domane, Vintage Levis 501
- Switch OLED vs Switch Lite not distinguished (known test gap)

## Trained Model

- PyTorch: `E:/Dev/ml-training/variant-classifier/v8/pytorch/`
- ONNX: `E:/Dev/ml-training/variant-classifier/v8/onnx/` (1.36GB, production)
