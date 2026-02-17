# V9 Variant Classifier — Training Data Mining Design

**Date:** 2026-02-16
**Status:** Draft — awaiting decisions on scope and labeling approach

## Context

The v8 variant classifier (roberta-large, F1=0.913) was trained on 143K pairs. A comp evaluation of 50 listings across 10 categories revealed systematic classifier failures: accessory vs product confusion, condition grade blindness, pattern-blind matching, men's vs ladies conflation, and more.

We manually created 486 correction pairs (v9) from the comp evaluation. These are high-quality but too few to meaningfully shift a 143K-pair model (0.3% of dataset).

The database contains **764K listing relationships** across **210K listings in 124 categories** — a large untapped source of training data. Mining the decision boundary before retraining will produce a far more impactful v9 dataset.

## Current State

| Asset | Size | Quality |
|---|---|---|
| v8 training data | 143,075 pairs | Mixed — generated with earlier heuristics |
| v9 comp-eval corrections | 486 pairs | High — manually reviewed, 10 categories |
| DB listing relationships | 764,360 pairs | Model-predicted, unreviewed |

### v9 Correction Pairs by Category

| Script | Category | Pairs | Label 0 | Label 1 |
|---|---|---|---|---|
| generate_from_eval.py | iPad Pro | 49 | 17 | 32 |
| append_ps5_pairs.py | PlayStation 5 | 68 | 21 | 47 |
| append_rolex_pairs.py | Rolex Submariner | 65 | 32 | 33 |
| append_luxury_pairs.py | Cartier, Chanel, Hermes, Omega, Roland | 51 | 20 | 31 |
| append_neverfull_pairs.py | Louis Vuitton Neverfull | 160 | 62 | 98 |
| append_electronics_pairs.py | MacBook, Sony, iPhone, Peloton | 68 | 34 | 34 |
| append_brompton_pairs.py | Brompton, Canada Goose | 25 | 16 | 9 |
| **Total** | **10 categories** | **486** | **202** | **284** |

### Classifier Issues Identified (from comp evaluation)

1. **Accessory vs product** — PS5 Disc Drive matched to full consoles (5x overestimate)
2. **Electric vs non-electric** — Brompton C Line matched only to electric models
3. **Men's vs ladies** — Omega Seamaster ladies matched to men's watches
4. **Condition grade blindness** — MacBook Grade A/B/C treated as equivalent
5. **Canvas pattern blindness** — LV Damier Azur vs Ebene vs Monogram conflated
6. **Bundle detection failure** — Peloton/iPad/Roland with accessories matched to bare products
7. **Dial color variants** — Rolex 116613LN (black) vs 116613LB (blue) conflated
8. **Counterfeit contamination** — Canada Goose comps include suspected fakes at 10% of retail
9. **Reference number blindness** — Omega 596.152 (ladies) vs 2541.80 (men's) not distinguished
10. **Vintage year premiums** — Rolex 5513 from 1970s vs 1980s treated as equivalent

## Proposed Approach: Mine Decision Boundary Before Retraining

### Strategy

Mine pairs from the existing DB where the model is **uncertain** (similarity 0.80–0.90), label them, merge with v8+v9 data, then full retrain.

```
Mine boundary pairs → Label them → Human spot-check → Merge v8 + v9 + mined → Full retrain
```

### Why Mine the Boundary?

- Pairs with similarity 0.80–0.90 are where the model is least confident. These are the most informative for training — they're the cases the model struggles with.
- Pairs above 0.90 are mostly correct (model is confident and usually right).
- Pairs below 0.80 are mostly correct rejections.
- The boundary region is where false positives and false negatives concentrate.

### High-Leverage Categories to Mine

Categories with low comparable ratios suggest the model is aggressive and likely making errors:

| Category | Relationships | Comparable | Ratio | Likely Issue |
|---|---|---|---|---|
| iPad Pro | 21,355 | 2,717 | 12.7% | Storage/cellular/generation confusion |
| Omega Seamaster | 13,266 | 1,139 | 8.6% | Men's vs ladies, model reference confusion |
| Mac Mini | 14,437 | 1,725 | 12.0% | M1/M2/M3/M4 chip generation, RAM/storage |
| DeWalt Cordless Drill | 15,840 | 2,851 | 18.0% | Voltage, model number, kit vs bare tool |
| Louis Vuitton Neverfull | 13,204 | 5,125 | 38.8% | Pattern (Azur/Ebene/Monogram), size (PM/MM/GM) |
| New Balance 990v6 | 11,821 | 1,254 | 10.6% | Size, colorway, v5 vs v6 |
| Tiffany Elsa Peretti | 12,783 | 3,105 | 24.3% | Different jewelry pieces, sizes |
| Canon RF 50mm Lens | ~2,154 listings | — | — | f/1.2 vs f/1.8 (10x price difference) |

Categories already covered by v9 corrections (iPad, PS5, Rolex, Omega, LV, Brompton, Canada Goose) would still benefit from more boundary-mined pairs to complement the hand-labeled corrections.

### Mining Query Design

```sql
-- Pull boundary pairs (similarity 0.80-0.90) from target categories
SELECT lr.ListingIdA, lr.ListingIdB, lr.SimilarityScore, lr.IsComparable,
       a.Title AS TitleA, a.Description AS DescA, a.Price AS PriceA, a.Condition AS CondA,
       b.Title AS TitleB, b.Description AS DescB, b.Price AS PriceB, b.Condition AS CondB,
       sj.SearchTerm
FROM ListingRelationships lr
INNER JOIN Listings a ON a.Id = lr.ListingIdA
INNER JOIN Listings b ON b.Id = lr.ListingIdB
INNER JOIN ScrapeJobs sj ON sj.Id = a.ScrapeJobId
WHERE lr.SimilarityScore BETWEEN 0.80 AND 0.90
  AND sj.SearchTerm IN (<target categories>)
ORDER BY sj.SearchTerm, lr.SimilarityScore DESC
```

Sample ~200-500 pairs per category from this boundary region.

### Labeling Options

**Option 1: LLM-assisted labeling (Claude API)**
- Feed each pair (titles + descriptions + category) to Claude with category-specific prompts
- Category prompts encode variant knowledge (e.g., for Rolex: reference numbers, dial colors, aftermarket mods)
- Fast: can label 5K pairs in minutes
- Cost: ~$5-15 depending on prompt length and model
- Requires 10% human spot-check to validate quality

**Option 2: Rule-based labeling**
- Write regex/keyword rules per category (e.g., storage extraction for iPads, reference number matching for watches)
- Free but limited to patterns we can codify
- Misses nuanced cases (condition grades, bundles, vintage premiums)

**Option 3: Manual labeling only**
- Highest quality but slowest
- 5K pairs at ~10 seconds each = 14 hours of labeling
- Not practical for broad mining

**Recommendation:** Option 1 (LLM-assisted) with Option 2 rules as a pre-filter, and 10% manual spot-check.

### Target Dataset Size

| Source | Pairs | Purpose |
|---|---|---|
| v8 existing | 143,075 | Baseline model knowledge |
| v9 comp-eval corrections | 486 | Hand-labeled hard cases from comp evaluation |
| v9 boundary-mined | 2,000–5,000 | Decision boundary pairs from under-performing categories |
| **Total v9 merged** | **~145,500–148,500** | Full retrain dataset |

### Training Plan

Once data is assembled:

1. Merge all sources into `labeled_pairs_v9.csv` (or `labeled_pairs_v9_merged.csv`)
2. Full retrain using existing `train_v8.py` script with `--data` pointed at merged CSV
3. Same hyperparameters: roberta-large, LR=1e-5, bf16, batch_size=16, max_length=256, 3 epochs
4. Output to `model_v9/` directory
5. Run per-category F1 analysis with `--cross-product` flag
6. Compare v9 vs v8 on the specific failure categories identified in comp evaluation
7. Export to ONNX for production deployment

### Success Criteria

- Overall F1 >= 0.913 (match or beat v8)
- Improvement on weak categories from comp eval:
  - PS5 accessory vs product: should correctly reject disc drive → console matches
  - Omega men's vs ladies: should correctly reject cross-gender matches
  - LV pattern matching: Azur/Ebene/Monogram distinguished
  - MacBook condition grades: A/B/C treated as distinct pricing tiers
  - Brompton electric vs non-electric: correctly rejected

### Open Decisions

1. **Which categories to mine?** The weak ones from eval only, or broaden to untested categories (Mac Mini, DeWalt, Dyson, etc.)?
2. **Labeling method?** LLM-assisted (fast, ~$10) vs manual only (slow, free) vs hybrid?
3. **Mining budget?** How many pairs per category — 200 (conservative) or 500 (thorough)?

## Files

- v9 augment scripts: `AIOMarketMaker.ML/Training/v9/augment/`
- v9 CSV: `AIOMarketMaker.ML/Training/data/labeled_pairs_v9.csv` (486 pairs)
- v8 training script: `AIOMarketMaker.ML/Training/v8/train.py`
- v8 dataset: `AIOMarketMaker.ML/Training/data/labeled_pairs_v8.csv` (143K pairs)
- v8 model: `E:/Dev/ml-training/variant-classifier/v8/pytorch/`
- v8 ONNX: `E:/Dev/ml-training/variant-classifier/v8/onnx/`
