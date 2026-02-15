# V5: Baseline Variant Classifier

## Approach

First production-quality variant classifier. Uses GPT-5-mini to label pairs of eBay listings as "same variant" or "different variant", then trains a BERT-base cross-encoder on the labeled data.

**Data collection** (`collect_v5.py`):
- Queries Pinecone for top-50 nearest neighbors per anchor listing
- Deduplicates across categories
- Sends pairs to GPT-5-mini with structured output (reasoning, label, confidence)
- 44+ categories, ~115 anchors per category, 5 neighbors per anchor
- Output: **24,260 labeled pairs**

**Training**:
- Model: `bert-base-uncased`
- Input: `"{title_a} | {desc_a}"` vs `"{title_b} | {desc_b}"` as sentence pair
- Binary classification (0=different, 1=same variant)

## Results

- **F1 = 0.871 macro**
- Good at obvious distinctions (PS5 vs Xbox, working vs parts-only)
- Weak on spec-level variants (same product, different RAM/storage)

## Key Files

| File | Purpose |
|------|---------|
| `collect_v5.py` | Data collection pipeline (Pinecone + GPT labeling) |
| `labeled_pairs_v5.csv` | 24,260 labeled pairs |
| `listings_v5_cache.csv` | Cached listing data from DB |
| `test_labeling_prompt.py` | Prompt validation with 50+ curated test cases |
| `batch_test.py` | Live evaluation: BERT vs cosine per category |
| `batch_test_v2.py` | Evaluation with actual titles for manual judgment |
| `live_test.py` | Single listing live inference test |
| `model_comparison.py` | GPT-5-mini vs GPT-5-nano agreement rate |
| `nano_prompt_tuning.py` | Prompt tuning to fix nano's over-splitting bias |
| `model_v3.pt` / `model_v4.pt` | Earlier MLP models (pre-cross-encoder, F1 ~0.698) |

## Limitations

- Only 24K pairs — thin coverage per category (~550 pairs each)
- Pairs sourced only from Pinecone neighbors (high similarity bias)
- No deliberate hard negative mining
- bert-base may lack capacity for fine-grained spec understanding

## Trained Model

`E:/Dev/ml-training/variant-classifier/model_v5/`
