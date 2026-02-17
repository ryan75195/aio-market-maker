# Evaluator Training Pipeline Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build the full pipeline to fine-tune a Qwen3-8B evaluator model that audits ONNX cross-encoder decisions on production listing pairs.

**Architecture:** Extract production pairs from `ListingRelationships`, audit them with GPT-5-mini for ground truth labels, fine-tune Qwen3-8B via Unsloth QLoRA, validate against held-out test set, and run batch inference on new production data.

**Tech Stack:** Python 3.12, pyodbc, openai (AsyncOpenAI + Pydantic structured outputs), Unsloth + QLoRA, Qwen3-8B, sklearn

**Design doc:** `docs/plans/2026-02-17-evaluator-training-pipeline-design.md`

---

### Task 1: Create Directory Structure and README

**Files:**
- Create: `AIOMarketMaker.ML/Training/evaluator/README.md`

**Step 1: Create the evaluator directory and README**

```markdown
# Evaluator: ONNX Cross-Encoder QA

## Goal

Fine-tuned Qwen3-8B model that audits RoBERTa ONNX cross-encoder decisions
on production listing pairs. Flags misclassifications so corrected labels
can be fed back into cross-encoder training.

## Pipeline

```
collect.py → audit.py → train.py → eval.py → run.py
   │             │           │          │          │
   │             │           │          │          └─ Batch inference on new pairs
   │             │           │          └─ Validate against test set
   │             │           └─ Unsloth QLoRA fine-tune
   │             └─ GPT-5-mini audit (ground truth)
   └─ Extract production pairs from DB
```

## Quick Start

```bash
# 1. Extract pairs (free, ~30s)
py -3.12 collect.py --dry-run        # preview counts
py -3.12 collect.py                  # write CSV

# 2. GPT audit (~$10-15, ~5 min)
py -3.12 audit.py --dry-run          # preview cost
py -3.12 audit.py                    # run audit

# 3. Train (~1-2 hours on RTX 5070 Ti)
py -3.12 train.py

# 4. Evaluate
py -3.12 eval.py

# 5. Run on new production data
py -3.12 run.py
```

## Model Location

- LoRA adapter: `E:/Dev/ml-training/evaluator/v1/lora_adapter/`
- Merged model: `E:/Dev/ml-training/evaluator/v1/merged/`

## Error Types

| error_type | Description |
|-----------|-------------|
| `bundle_inflation` | Bare product matched to product + expensive accessory |
| `condition_mismatch` | Different condition tiers within same eBay category |
| `wrong_variant` | Different specs (storage, connectivity, size) |
| `accessory_vs_product` | Peripheral matched to main product |
| `price_outlier` | Penny auction or extreme price anomaly |
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.ML/Training/evaluator/README.md
git commit -m "docs: add evaluator training pipeline README"
```

---

### Task 2: Install Python Dependencies

**Context:** The project has no requirements.txt — dependencies are installed ad-hoc. Unsloth is new and requires specific installation.

**Step 1: Install Unsloth and dependencies**

```bash
py -3.12 -m pip install "unsloth[cu124-torch260] @ https://unsloth.ai/whl/0.2/cu124-torch260"
py -3.12 -m pip install pydantic openai pyodbc scikit-learn pandas
```

> **Note:** The Unsloth install URL may change. Check https://docs.unsloth.ai/get-started/installing-unsloth for the latest wheel matching your CUDA version (12.4) and PyTorch version. If the above URL fails, use: `pip install unsloth` and let it resolve dependencies.

**Step 2: Verify Unsloth loads**

```bash
py -3.12 -c "from unsloth import FastLanguageModel; print('Unsloth OK')"
```

Expected: `Unsloth OK` (may print some warnings on first run — that's fine).

**Step 3: Verify GPU access**

```bash
py -3.12 -c "import torch; print(f'CUDA: {torch.cuda.is_available()}, GPU: {torch.cuda.get_device_name(0)}')"
```

Expected: `CUDA: True, GPU: NVIDIA GeForce RTX 5070 Ti`

> **If Unsloth doesn't support Qwen3-8B yet:** Fall back to vanilla HuggingFace PEFT:
> ```bash
> py -3.12 -m pip install peft bitsandbytes accelerate
> ```
> The train.py script should try Unsloth first and fall back to PEFT.

---

### Task 3: collect.py — Extract Production Pairs

**Files:**
- Create: `AIOMarketMaker.ML/Training/evaluator/collect.py`

**Step 1: Write collect.py**

This script queries `ListingRelationships` joined with `Listings` and `ScrapeJobs` to extract pairs with stratified sampling across confidence tiers and categories.

```python
"""
Extract production listing pairs from ListingRelationships for evaluator training.

Stratified sampling:
  Tier 1: Low-confidence comparables (SimilarityScore 0.50-0.75)
  Tier 2: Known weak categories (watches, luxury bags, cycling, etc.)
  Tier 3: High-confidence random sample (calibration)

Usage:
    py -3.12 collect.py                    # full extraction
    py -3.12 collect.py --dry-run          # show counts only
    py -3.12 collect.py --tier1 3000       # override tier sizes
"""

import argparse
import csv
import random
import sys
import io
from pathlib import Path

import pyodbc

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

DB_CONN = (
    "DRIVER={ODBC Driver 17 for SQL Server};"
    "SERVER=(localdb)\\MSSQLLocalDB;"
    "DATABASE=AIOMarketMaker;"
    "Trusted_Connection=yes;"
)

DATA_DIR = Path(__file__).parent.parent / "data"
OUTPUT_CSV = DATA_DIR / "evaluator_pairs_raw.csv"
DESC_LIMIT = 500

# Categories with v8 F1 < 0.85 — oversample these
WEAK_CATEGORIES = [
    "Rolex", "Omega", "Cartier", "Breitling",  # watches
    "Louis Vuitton", "Chanel", "Hermes",         # luxury bags
    "Specialized", "Trek", "Brompton",            # cycling
    "Birkenstock",                                 # footwear
    "Vintage Levi",                                # vintage clothing
    "Dyson",                                       # appliances
    "Yamaha P-125", "Roland TD",                   # instruments
]


def parse_args():
    parser = argparse.ArgumentParser(description="Extract evaluator training pairs")
    parser.add_argument("--dry-run", action="store_true", help="Show counts only")
    parser.add_argument("--tier1", type=int, default=2000, help="Low-confidence pairs")
    parser.add_argument("--tier2", type=int, default=2000, help="Weak-category pairs")
    parser.add_argument("--tier3", type=int, default=1000, help="High-confidence random")
    parser.add_argument("--seed", type=int, default=42, help="Random seed")
    return parser.parse_args()


def query_tier(conn, where_clause, limit, desc=""):
    """Query pairs from ListingRelationships with given filter."""
    sql = f"""
    SELECT TOP {limit}
        lr.ListingIdA, lr.ListingIdB,
        lr.IsComparable, lr.SimilarityScore,
        a.Title AS TitleA,
        REPLACE(REPLACE(LEFT(ISNULL(a.Description,''), {DESC_LIMIT}), CHAR(10), ' '), CHAR(13), ' ') AS DescA,
        b.Title AS TitleB,
        REPLACE(REPLACE(LEFT(ISNULL(b.Description,''), {DESC_LIMIT}), CHAR(10), ' '), CHAR(13), ' ') AS DescB,
        sj.SearchTerm
    FROM ListingRelationships lr
    INNER JOIN Listings a ON a.Id = lr.ListingIdA
    INNER JOIN Listings b ON b.Id = lr.ListingIdB
    INNER JOIN ScrapeJobs sj ON sj.Id = a.ScrapeJobId
    WHERE {where_clause}
    ORDER BY NEWID()
    """
    cursor = conn.cursor()
    cursor.execute(sql)
    rows = cursor.fetchall()
    columns = [col[0] for col in cursor.description]
    print(f"  {desc}: {len(rows)} pairs")
    return [dict(zip(columns, row)) for row in rows]


def collect_pairs(args):
    conn = pyodbc.connect(DB_CONN)

    print("Extracting evaluator training pairs...")
    print(f"  Target: tier1={args.tier1}, tier2={args.tier2}, tier3={args.tier3}")
    print()

    # Tier 1: Low-confidence comparable pairs
    tier1 = query_tier(
        conn,
        "lr.IsComparable = 1 AND lr.SimilarityScore BETWEEN 0.50 AND 0.75",
        args.tier1,
        "Tier 1 (low confidence 0.50-0.75)",
    )

    # Tier 2: Known weak categories (any confidence)
    weak_terms = " OR ".join(
        f"sj.SearchTerm LIKE '%{cat}%'" for cat in WEAK_CATEGORIES
    )
    tier2 = query_tier(
        conn,
        f"lr.IsComparable = 1 AND ({weak_terms})",
        args.tier2,
        "Tier 2 (weak categories)",
    )

    # Tier 3: High-confidence random sample (calibration)
    tier3 = query_tier(
        conn,
        "lr.IsComparable = 1 AND lr.SimilarityScore > 0.75",
        args.tier3,
        "Tier 3 (high confidence >0.75)",
    )

    conn.close()

    # Deduplicate across tiers (same pair might appear in tier1 and tier2)
    seen = set()
    all_pairs = []
    for pair in tier1 + tier2 + tier3:
        key = (pair["ListingIdA"], pair["ListingIdB"])
        if key not in seen:
            seen.add(key)
            all_pairs.append(pair)

    print(f"\nTotal unique pairs: {len(all_pairs)}")

    # Category distribution
    categories = {}
    for p in all_pairs:
        cat = p["SearchTerm"]
        categories[cat] = categories.get(cat, 0) + 1
    print("\nCategory distribution:")
    for cat in sorted(categories, key=categories.get, reverse=True)[:20]:
        print(f"  {cat}: {categories[cat]}")
    if len(categories) > 20:
        print(f"  ... and {len(categories) - 20} more")

    return all_pairs


def write_csv(pairs, output_path):
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with open(output_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow([
            "listing_id_a", "listing_id_b",
            "title_a", "desc_a", "title_b", "desc_b",
            "onnx_label", "similarity_score", "search_term",
        ])
        for p in pairs:
            writer.writerow([
                p["ListingIdA"], p["ListingIdB"],
                p["TitleA"], p["DescA"], p["TitleB"], p["DescB"],
                1 if p["IsComparable"] else 0, p["SimilarityScore"],
                p["SearchTerm"],
            ])
    print(f"\nWrote {len(pairs)} pairs to {output_path}")


def main():
    args = parse_args()
    random.seed(args.seed)
    pairs = collect_pairs(args)

    if args.dry_run:
        print("\n[DRY RUN] No CSV written.")
        return

    write_csv(pairs, OUTPUT_CSV)


if __name__ == "__main__":
    main()
```

**Step 2: Test with dry run**

```bash
cd AIOMarketMaker/AIOMarketMaker.ML/Training/evaluator
py -3.12 collect.py --dry-run
```

Expected output: tier counts and category distribution. Verify:
- Tier 1 returns pairs (if <2000, that's fine — it means fewer low-confidence pairs exist)
- Tier 2 returns pairs for weak categories
- Tier 3 returns random high-confidence pairs
- No SQL errors

**Step 3: Run full extraction**

```bash
py -3.12 collect.py
```

Expected: writes `../data/evaluator_pairs_raw.csv` with ~5K rows.

**Step 4: Commit**

```bash
cd ../../../
git add AIOMarketMaker.ML/Training/evaluator/collect.py
git commit -m "feat(evaluator): add collect.py for stratified pair extraction"
```

---

### Task 4: audit.py — GPT-5-mini Ground Truth Labels

**Files:**
- Create: `AIOMarketMaker.ML/Training/evaluator/audit.py`

**Context:** This script calls the OpenAI API and costs ~$10-15. It has a `--dry-run` mode and a `--limit` flag for testing with small batches before committing to the full run.

**Step 1: Write audit.py**

```python
"""
Audit ONNX cross-encoder decisions using GPT-5-mini.

Sends each production pair + ONNX label to GPT for ground truth.
Produces training data for the evaluator fine-tune.

Usage:
    py -3.12 audit.py --dry-run          # preview, no API calls
    py -3.12 audit.py --limit 10         # test with 10 pairs (~$0.02)
    py -3.12 audit.py                    # full run (~$10-15 for 5K pairs)
"""

import asyncio
import csv
import json
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

from pydantic import BaseModel, Field

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")

# Load API key (same pattern as v9 eval scripts)
SETTINGS_PATH = (
    Path(__file__).parent.parent.parent.parent
    / "AIOMarketMaker.Etl"
    / "local.settings.json"
)
with open(SETTINGS_PATH) as f:
    _settings = json.load(f)
API_KEY = _settings["Values"]["OpenAi:ApiKey"]

from openai import AsyncOpenAI

client = AsyncOpenAI(api_key=API_KEY)

MODEL = "gpt-5-mini"
MAX_CONCURRENT = 10
SEMAPHORE = asyncio.Semaphore(MAX_CONCURRENT)

DATA_DIR = Path(__file__).parent.parent / "data"
INPUT_CSV = DATA_DIR / "evaluator_pairs_raw.csv"
OUTPUT_CSV = DATA_DIR / "evaluator_audit_gpt.csv"


# Structured output schema
class AuditResult(BaseModel):
    verdict: str = Field(
        description="'correct' if the classifier's decision is right, "
        "'misclassification' if wrong"
    )
    correct_label: int = Field(
        description="The correct label: 1 if comparable, 0 if not comparable"
    )
    error_type: Optional[str] = Field(
        default=None,
        description="If misclassification: 'bundle_inflation', 'condition_mismatch', "
        "'wrong_variant', 'accessory_vs_product', or 'price_outlier'. "
        "null if verdict is 'correct'.",
    )
    reasoning: str = Field(description="Brief explanation of the audit decision")


SYSTEM_PROMPT = """You are auditing a product comparison classifier's decisions on eBay listings.

The classifier labeled two listings as COMPARABLE (meaning a buyer would pay roughly the same for either, so they're valid pricing comparables). Your job: is this decision correct?

Answer verdict="correct" if the classifier got it right. Answer verdict="misclassification" if the classifier got it wrong.

## CORRECT classification (verdict="correct")

The classifier was right to call them comparable. All of these are true:
- Same product model, generation, storage, connectivity, screen size
- Similar condition within their eBay category (both working with normal wear, or both damaged)
- Similar completeness (both bare product, or both with similar accessories)
- Color differences (Space Grey vs Silver) are fine for electronics
- Trivial accessories (cables, charger, box, headphones) don't matter

## MISCLASSIFICATION (verdict="misclassification")

The classifier was wrong. Any ONE of these error types:

**wrong_variant** — Different specs:
- Different storage (128GB vs 256GB), connectivity (Wi-Fi vs Cellular), CPU, screen size
- Different model generation or reference number
- Disc Edition vs Digital Edition

**accessory_vs_product** — Peripheral matched to main product:
- "PS5 Disc Drive" (~£80 add-on) matched to "PS5 Console" (~£400)
- "Apple Pencil" matched to "iPad"
- Trust the TITLE over description for product identity

**condition_mismatch** — Different condition tiers within same eBay category:
- Working unit vs for-parts/broken/cracked screen
- "Excellent"/"Grade A"/"Mint" vs "Grade C"/"Fair"/"scratched"/"damaged"
- NEW or OPENED_NEVER_USED vs USED (10-30% price gap)
- Professionally refurbished vs generic used
- Scan descriptions for: "smashed", "cracked", "broken", "battered", "damaged", "fault"

**bundle_inflation** — Expensive accessory difference:
- iPad + Magic Keyboard vs bare iPad
- Console + game bundle vs bare console
- Console with controller vs "console only" (missing ~£50-60 controller)
- Multi-item lot vs single item

**price_outlier** — Extreme price anomaly:
- Penny auction result paired with normal listing
- Suspected counterfeit at fraction of market rate

## Category-specific rules

- Luxury bags: different pattern (Monogram vs Damier) or size (PM vs MM vs GM) = wrong_variant
- Watches: different reference number or dial color = wrong_variant. Missing original bracelet = condition_mismatch
- Watches with similar custom mods (both diamond bezels) = correct
- Luxury jewelry: metal color (yellow gold vs white gold vs rose gold) = wrong_variant
- Fitness equipment: shoes, weights, mounts are material accessories (~£50-100+) = bundle_inflation"""

USER_TEMPLATE = """The classifier labeled this pair as {label_text} (confidence: {confidence:.2f}).

LISTING A:
Title: {title_a}
Description: {desc_a}

LISTING B:
Title: {title_b}
Description: {desc_b}

Is the classifier's decision correct?"""


@dataclass
class AuditRow:
    listing_id_a: int
    listing_id_b: int
    title_a: str
    desc_a: str
    title_b: str
    desc_b: str
    onnx_label: int
    similarity_score: float
    search_term: str
    # GPT results
    verdict: str = ""
    correct_label: int = -1
    error_type: str = ""
    reasoning: str = ""


async def audit_pair(row: dict) -> AuditRow:
    """Call GPT-5-mini to audit a single ONNX decision."""
    onnx_label = int(row["onnx_label"])
    similarity = float(row["similarity_score"])
    label_text = "COMPARABLE" if onnx_label == 1 else "NOT COMPARABLE"

    async with SEMAPHORE:
        user_msg = USER_TEMPLATE.format(
            label_text=label_text,
            confidence=similarity,
            title_a=row["title_a"],
            desc_a=row["desc_a"][:800],
            title_b=row["title_b"],
            desc_b=row["desc_b"][:800],
        )

        result = AuditRow(
            listing_id_a=int(row["listing_id_a"]),
            listing_id_b=int(row["listing_id_b"]),
            title_a=row["title_a"],
            desc_a=row["desc_a"],
            title_b=row["title_b"],
            desc_b=row["desc_b"],
            onnx_label=onnx_label,
            similarity_score=similarity,
            search_term=row["search_term"],
        )

        try:
            response = await client.chat.completions.parse(
                model=MODEL,
                messages=[
                    {"role": "system", "content": SYSTEM_PROMPT},
                    {"role": "user", "content": user_msg},
                ],
                response_format=AuditResult,
            )
            parsed = response.choices[0].message.parsed
            if parsed is None:
                raise ValueError("Parsed response is None")
            result.verdict = parsed.verdict
            result.correct_label = parsed.correct_label
            result.error_type = parsed.error_type or ""
            result.reasoning = parsed.reasoning
        except Exception as e:
            print(f"  ERROR ({row['listing_id_a']}, {row['listing_id_b']}): {e}",
                  file=sys.stderr)
            result.verdict = "error"
            result.reasoning = str(e)

        return result


async def main():
    dry_run = "--dry-run" in sys.argv
    limit = None
    for i, arg in enumerate(sys.argv):
        if arg == "--limit" and i + 1 < len(sys.argv):
            limit = int(sys.argv[i + 1])

    # Load extracted pairs
    with open(INPUT_CSV, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        rows = list(reader)

    if limit:
        rows = rows[:limit]

    total = len(rows)
    print(f"GPT Audit: {total} pairs with {MODEL}")
    print(f"Concurrency: {MAX_CONCURRENT}")
    print(f"Estimated cost: ~${total * 0.003:.2f}")
    print()

    if dry_run:
        print("[DRY RUN] Would audit these pairs:")
        for row in rows[:5]:
            print(f"  ({row['listing_id_a']}, {row['listing_id_b']}) "
                  f"score={row['similarity_score']} — {row['search_term']}")
        if total > 5:
            print(f"  ... and {total - 5} more")
        return

    start = time.time()
    tasks = [audit_pair(row) for row in rows]
    results = await asyncio.gather(*tasks)
    elapsed = time.time() - start

    # Stats
    errors = [r for r in results if r.verdict == "error"]
    correct = [r for r in results if r.verdict == "correct"]
    misclass = [r for r in results if r.verdict == "misclassification"]

    print(f"\nCompleted in {elapsed:.1f}s ({elapsed/total:.2f}s per pair)")
    print(f"  Correct:          {len(correct)}")
    print(f"  Misclassification: {len(misclass)}")
    print(f"  Errors:           {len(errors)}")

    if misclass:
        # Error type breakdown
        error_types = {}
        for r in misclass:
            et = r.error_type or "unknown"
            error_types[et] = error_types.get(et, 0) + 1
        print("\nMisclassification breakdown:")
        for et, count in sorted(error_types.items(), key=lambda x: -x[1]):
            print(f"  {et}: {count}")

    # Write results
    OUTPUT_CSV.parent.mkdir(parents=True, exist_ok=True)
    with open(OUTPUT_CSV, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow([
            "listing_id_a", "listing_id_b",
            "title_a", "desc_a", "title_b", "desc_b",
            "onnx_label", "similarity_score", "search_term",
            "verdict", "correct_label", "error_type", "reasoning",
        ])
        for r in results:
            writer.writerow([
                r.listing_id_a, r.listing_id_b,
                r.title_a, r.desc_a, r.title_b, r.desc_b,
                r.onnx_label, r.similarity_score, r.search_term,
                r.verdict, r.correct_label, r.error_type, r.reasoning,
            ])
    print(f"\nResults saved to {OUTPUT_CSV}")


if __name__ == "__main__":
    asyncio.run(main())
```

**Step 2: Test with small batch**

```bash
cd AIOMarketMaker/AIOMarketMaker.ML/Training/evaluator
py -3.12 audit.py --dry-run
py -3.12 audit.py --limit 5
```

Verify: 5 results returned, check that verdicts and error_types are reasonable. Review the CSV output manually.

**Step 3: Pre-run checklist before full audit**

Before running the full audit (~$10-15), verify:
- [ ] `evaluator_pairs_raw.csv` has the expected number of rows (~5K)
- [ ] `--limit 5` test returned sensible results
- [ ] API key is valid and has sufficient credit
- [ ] Description truncation at 800 chars is acceptable (matches v9 eval pattern)
- [ ] Cost estimate is acceptable (~$0.003 per pair)

**Step 4: Run full audit**

```bash
py -3.12 audit.py
```

Expected: ~5K pairs audited, `evaluator_audit_gpt.csv` written. Review misclassification rate — expect ~20-30% misclassification for low-confidence tiers, ~5-10% for high-confidence.

**Step 5: Commit**

```bash
cd ../../../
git add AIOMarketMaker.ML/Training/evaluator/audit.py
git commit -m "feat(evaluator): add audit.py for GPT ground truth labeling"
```

---

### Task 5: Split Train/Test and Prepare Training Data

**Context:** After the GPT audit produces `evaluator_audit_gpt.csv`, split it 90/10 into train and test sets. This is a short script that can be inlined into `train.py` but is useful as a separate step for reproducibility.

**Step 1: Add split logic to the top of train.py (next task)**

The train script will handle the split internally. But first, verify the audit data:

```bash
py -3.12 -c "
import csv
from pathlib import Path
from collections import Counter

data = Path('../data/evaluator_audit_gpt.csv')
with open(data, encoding='utf-8') as f:
    rows = list(csv.DictReader(f))

verdicts = Counter(r['verdict'] for r in rows)
error_types = Counter(r['error_type'] for r in rows if r['verdict'] == 'misclassification')
print(f'Total: {len(rows)}')
print(f'Verdicts: {dict(verdicts)}')
print(f'Error types: {dict(error_types)}')
print(f'Error rate: {verdicts[\"misclassification\"] / len(rows):.1%}')
"
```

Expected: see the distribution of verdicts and error types. Verify no "error" verdicts (API failures). If there are errors, re-run audit with `--limit` on those specific pairs.

---

### Task 6: train.py — Unsloth QLoRA Fine-tune

**Files:**
- Create: `AIOMarketMaker.ML/Training/evaluator/train.py`

**Step 1: Write train.py**

```python
"""
Fine-tune Qwen3-8B as an evaluator for ONNX cross-encoder decisions.

Uses Unsloth QLoRA for efficient fine-tuning on RTX 5070 Ti (16GB VRAM).
Falls back to vanilla HuggingFace PEFT if Unsloth doesn't support the model.

Usage:
    py -3.12 train.py                     # full training
    py -3.12 train.py --dry-run            # load model, show data stats, exit
    py -3.12 train.py --epochs 1           # quick test run
    py -3.12 train.py --output-dir E:/Dev/ml-training/evaluator/v2/lora_adapter
"""

import argparse
import csv
import json
import os
import random
import sys
from pathlib import Path

# Redirect caches to E: drive
os.environ.setdefault("HF_HOME", "E:/DevCaches/huggingface")
os.environ.setdefault("TORCH_HOME", "E:/DevCaches/torch")

import torch
from sklearn.model_selection import train_test_split

# Try Unsloth first, fall back to vanilla PEFT
try:
    from unsloth import FastLanguageModel
    USE_UNSLOTH = True
    print("Using Unsloth for fine-tuning")
except ImportError:
    from transformers import AutoModelForCausalLM, AutoTokenizer
    from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training
    USE_UNSLOTH = False
    print("Unsloth not available, using vanilla PEFT")

from transformers import TrainingArguments, Trainer
from torch.utils.data import Dataset

DATA_DIR = Path(__file__).parent.parent / "data"
AUDIT_CSV = DATA_DIR / "evaluator_audit_gpt.csv"
DEFAULT_OUTPUT = "E:/Dev/ml-training/evaluator/v1/lora_adapter"
DEFAULT_MERGED = "E:/Dev/ml-training/evaluator/v1/merged"

BASE_MODEL = "Qwen/Qwen3-8B"
MAX_SEQ_LENGTH = 512
LORA_RANK = 16
LORA_ALPHA = 32


def parse_args():
    parser = argparse.ArgumentParser(description="Train evaluator model")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--epochs", type=int, default=3)
    parser.add_argument("--batch-size", type=int, default=4)
    parser.add_argument("--lr", type=float, default=2e-4)
    parser.add_argument("--output-dir", type=str, default=DEFAULT_OUTPUT)
    parser.add_argument("--merged-dir", type=str, default=DEFAULT_MERGED)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--test-size", type=float, default=0.1)
    return parser.parse_args()


def load_audit_data(test_size=0.1, seed=42):
    """Load GPT audit CSV and split into train/test."""
    with open(AUDIT_CSV, newline="", encoding="utf-8") as f:
        rows = [r for r in csv.DictReader(f) if r["verdict"] != "error"]

    print(f"Loaded {len(rows)} audited pairs (excluding errors)")

    train_rows, test_rows = train_test_split(
        rows, test_size=test_size, random_state=seed,
        stratify=[r["verdict"] for r in rows],
    )

    # Save test set for eval.py
    test_path = DATA_DIR / "evaluator_test.csv"
    with open(test_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=rows[0].keys())
        writer.writeheader()
        writer.writerows(test_rows)
    print(f"Test set ({len(test_rows)} pairs) saved to {test_path}")

    train_path = DATA_DIR / "evaluator_train.csv"
    with open(train_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=rows[0].keys())
        writer.writeheader()
        writer.writerows(train_rows)
    print(f"Train set ({len(train_rows)} pairs) saved to {train_path}")

    return train_rows, test_rows


def format_chat(row):
    """Format a row into the chat template for training."""
    onnx_label = int(row["onnx_label"])
    label_text = "COMPARABLE" if onnx_label == 1 else "NOT COMPARABLE"
    confidence = float(row["similarity_score"])

    user_msg = (
        f"The classifier labeled this pair as {label_text} "
        f"(confidence: {confidence:.2f}).\n\n"
        f"LISTING A:\nTitle: {row['title_a']}\n"
        f"Description: {row['desc_a'][:500]}\n\n"
        f"LISTING B:\nTitle: {row['title_b']}\n"
        f"Description: {row['desc_b'][:500]}\n\n"
        f"Is the classifier's decision correct?"
    )

    assistant_msg = json.dumps({
        "verdict": row["verdict"],
        "correct_label": int(row["correct_label"]),
        "error_type": row["error_type"] if row["error_type"] else None,
        "reasoning": row["reasoning"],
    }, ensure_ascii=False)

    return user_msg, assistant_msg


class EvaluatorDataset(Dataset):
    def __init__(self, rows, tokenizer, max_length=MAX_SEQ_LENGTH):
        self.examples = []
        for row in rows:
            user_msg, assistant_msg = format_chat(row)
            # Use chat template
            messages = [
                {"role": "user", "content": user_msg},
                {"role": "assistant", "content": assistant_msg},
            ]
            text = tokenizer.apply_chat_template(
                messages, tokenize=False, add_generation_prompt=False
            )
            encoded = tokenizer(
                text, truncation=True, max_length=max_length,
                padding="max_length", return_tensors="pt",
            )
            input_ids = encoded["input_ids"].squeeze()
            attention_mask = encoded["attention_mask"].squeeze()
            # Labels = input_ids (causal LM), mask padding with -100
            labels = input_ids.clone()
            labels[attention_mask == 0] = -100
            self.examples.append({
                "input_ids": input_ids,
                "attention_mask": attention_mask,
                "labels": labels,
            })

    def __len__(self):
        return len(self.examples)

    def __getitem__(self, idx):
        return self.examples[idx]


def main():
    args = parse_args()
    random.seed(args.seed)
    torch.manual_seed(args.seed)

    # Load and split data
    train_rows, test_rows = load_audit_data(args.test_size, args.seed)

    from collections import Counter
    train_verdicts = Counter(r["verdict"] for r in train_rows)
    print(f"\nTrain distribution: {dict(train_verdicts)}")
    test_verdicts = Counter(r["verdict"] for r in test_rows)
    print(f"Test distribution:  {dict(test_verdicts)}")

    if args.dry_run:
        print("\n[DRY RUN] Would load model and train. Exiting.")
        return

    # Load model
    print(f"\nLoading {BASE_MODEL}...")
    if USE_UNSLOTH:
        model, tokenizer = FastLanguageModel.from_pretrained(
            BASE_MODEL,
            max_seq_length=MAX_SEQ_LENGTH,
            load_in_4bit=True,
            dtype=torch.bfloat16,
        )
        model = FastLanguageModel.get_peft_model(
            model,
            r=LORA_RANK,
            lora_alpha=LORA_ALPHA,
            target_modules=["q_proj", "k_proj", "v_proj", "o_proj",
                            "gate_proj", "up_proj", "down_proj"],
            lora_dropout=0.05,
            bias="none",
            use_gradient_checkpointing="unsloth",
        )
    else:
        from transformers import BitsAndBytesConfig
        bnb_config = BitsAndBytesConfig(
            load_in_4bit=True,
            bnb_4bit_quant_type="nf4",
            bnb_4bit_compute_dtype=torch.bfloat16,
        )
        model = AutoModelForCausalLM.from_pretrained(
            BASE_MODEL, quantization_config=bnb_config,
            device_map="auto", torch_dtype=torch.bfloat16,
        )
        tokenizer = AutoTokenizer.from_pretrained(BASE_MODEL)
        model = prepare_model_for_kbit_training(model)
        lora_config = LoraConfig(
            r=LORA_RANK, lora_alpha=LORA_ALPHA,
            target_modules=["q_proj", "k_proj", "v_proj", "o_proj",
                            "gate_proj", "up_proj", "down_proj"],
            lora_dropout=0.05, bias="none", task_type="CAUSAL_LM",
        )
        model = get_peft_model(model, lora_config)

    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    model.print_trainable_parameters()

    # Build datasets
    print("Tokenizing train set...")
    train_dataset = EvaluatorDataset(train_rows, tokenizer)
    print(f"Train examples: {len(train_dataset)}")

    # Training
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    training_args = TrainingArguments(
        output_dir=str(output_dir),
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch_size,
        gradient_accumulation_steps=4,
        learning_rate=args.lr,
        bf16=True,
        logging_steps=10,
        save_strategy="epoch",
        warmup_ratio=0.1,
        weight_decay=0.01,
        seed=args.seed,
        report_to="none",
    )

    trainer = Trainer(
        model=model,
        args=training_args,
        train_dataset=train_dataset,
    )

    print(f"\nStarting training: {args.epochs} epochs, batch={args.batch_size}, lr={args.lr}")
    trainer.train()

    # Save LoRA adapter
    model.save_pretrained(str(output_dir))
    tokenizer.save_pretrained(str(output_dir))
    print(f"\nLoRA adapter saved to {output_dir}")

    # Merge and save full model for inference
    merged_dir = Path(args.merged_dir)
    merged_dir.mkdir(parents=True, exist_ok=True)
    print(f"Merging LoRA weights into base model...")
    if USE_UNSLOTH:
        model.save_pretrained_merged(str(merged_dir), tokenizer)
    else:
        merged_model = model.merge_and_unload()
        merged_model.save_pretrained(str(merged_dir))
        tokenizer.save_pretrained(str(merged_dir))
    print(f"Merged model saved to {merged_dir}")


if __name__ == "__main__":
    main()
```

**Step 2: Test with dry run**

```bash
cd AIOMarketMaker/AIOMarketMaker.ML/Training/evaluator
py -3.12 train.py --dry-run
```

Expected: loads audit CSV, shows train/test split stats, exits without loading model.

**Step 3: Quick training test (1 epoch)**

```bash
py -3.12 train.py --epochs 1 --batch-size 2
```

Expected: model loads, trains for 1 epoch (verify VRAM usage stays under 16GB), saves LoRA adapter. This validates the full pipeline works before committing to 3 epochs.

**Step 4: Full training run**

```bash
py -3.12 train.py
```

Expected: ~1-2 hours on RTX 5070 Ti. LoRA adapter saved to `E:/Dev/ml-training/evaluator/v1/lora_adapter/`, merged model to `E:/Dev/ml-training/evaluator/v1/merged/`.

**Step 5: Commit**

```bash
cd ../../../
git add AIOMarketMaker.ML/Training/evaluator/train.py
git commit -m "feat(evaluator): add train.py for Unsloth QLoRA fine-tuning"
```

---

### Task 7: eval.py — Validate Against Test Set

**Files:**
- Create: `AIOMarketMaker.ML/Training/evaluator/eval.py`

**Step 1: Write eval.py**

```python
"""
Evaluate fine-tuned evaluator model against held-out test set.

Loads the merged model and runs inference on test pairs,
comparing predictions against GPT ground truth.

Usage:
    py -3.12 eval.py
    py -3.12 eval.py --model-dir E:/Dev/ml-training/evaluator/v2/merged
"""

import argparse
import csv
import json
import sys
import time
import os
from pathlib import Path

os.environ.setdefault("HF_HOME", "E:/DevCaches/huggingface")
os.environ.setdefault("TORCH_HOME", "E:/DevCaches/torch")

import torch
from sklearn.metrics import (
    accuracy_score, classification_report, confusion_matrix,
    precision_score, recall_score, f1_score,
)

DATA_DIR = Path(__file__).parent.parent / "data"
TEST_CSV = DATA_DIR / "evaluator_test.csv"
DEFAULT_MODEL_DIR = "E:/Dev/ml-training/evaluator/v1/merged"
OUTPUT_CSV = DATA_DIR / "benchmarks" / "evaluator_v1_results.csv"


def parse_args():
    parser = argparse.ArgumentParser(description="Evaluate evaluator model")
    parser.add_argument("--model-dir", type=str, default=DEFAULT_MODEL_DIR)
    parser.add_argument("--limit", type=int, default=None)
    return parser.parse_args()


def load_model(model_dir):
    """Load fine-tuned model for inference."""
    try:
        from unsloth import FastLanguageModel
        model, tokenizer = FastLanguageModel.from_pretrained(
            model_dir,
            max_seq_length=512,
            load_in_4bit=True,
            dtype=torch.bfloat16,
        )
        FastLanguageModel.for_inference(model)
    except ImportError:
        from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig
        bnb_config = BitsAndBytesConfig(
            load_in_4bit=True,
            bnb_4bit_compute_dtype=torch.bfloat16,
        )
        model = AutoModelForCausalLM.from_pretrained(
            model_dir, quantization_config=bnb_config,
            device_map="auto", torch_dtype=torch.bfloat16,
        )
        tokenizer = AutoTokenizer.from_pretrained(model_dir)
        model.eval()

    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    return model, tokenizer


def predict(model, tokenizer, row):
    """Run inference on a single pair and parse the JSON response."""
    onnx_label = int(row["onnx_label"])
    label_text = "COMPARABLE" if onnx_label == 1 else "NOT COMPARABLE"
    confidence = float(row["similarity_score"])

    user_msg = (
        f"The classifier labeled this pair as {label_text} "
        f"(confidence: {confidence:.2f}).\n\n"
        f"LISTING A:\nTitle: {row['title_a']}\n"
        f"Description: {row['desc_a'][:500]}\n\n"
        f"LISTING B:\nTitle: {row['title_b']}\n"
        f"Description: {row['desc_b'][:500]}\n\n"
        f"Is the classifier's decision correct?"
    )

    messages = [{"role": "user", "content": user_msg}]
    text = tokenizer.apply_chat_template(
        messages, tokenize=False, add_generation_prompt=True
    )
    inputs = tokenizer(text, return_tensors="pt").to(model.device)

    with torch.no_grad():
        outputs = model.generate(
            **inputs, max_new_tokens=256, temperature=0.1,
            do_sample=False, pad_token_id=tokenizer.pad_token_id,
        )

    # Decode only the generated tokens (not the prompt)
    generated = outputs[0][inputs["input_ids"].shape[1]:]
    response_text = tokenizer.decode(generated, skip_special_tokens=True).strip()

    # Parse JSON response
    try:
        parsed = json.loads(response_text)
        return parsed.get("verdict", "unknown"), parsed.get("error_type", ""), response_text
    except json.JSONDecodeError:
        # Try to extract verdict from text
        if "misclassification" in response_text.lower():
            return "misclassification", "", response_text
        elif "correct" in response_text.lower():
            return "correct", "", response_text
        return "parse_error", "", response_text


def main():
    args = parse_args()

    # Load test data
    with open(TEST_CSV, newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    if args.limit:
        rows = rows[:args.limit]

    print(f"Evaluating {len(rows)} test pairs")
    print(f"Model: {args.model_dir}")

    # Load model
    model, tokenizer = load_model(args.model_dir)

    # Run inference
    predictions = []
    ground_truth = []
    results = []

    start = time.time()
    for i, row in enumerate(rows):
        pred_verdict, pred_error_type, raw_response = predict(model, tokenizer, row)
        true_verdict = row["verdict"]

        predictions.append(pred_verdict)
        ground_truth.append(true_verdict)
        results.append({
            **row,
            "pred_verdict": pred_verdict,
            "pred_error_type": pred_error_type,
            "raw_response": raw_response[:500],
        })

        if (i + 1) % 50 == 0:
            elapsed = time.time() - start
            print(f"  {i+1}/{len(rows)} ({elapsed:.0f}s, "
                  f"{elapsed/(i+1)*1000:.0f}ms/pair)")

    elapsed = time.time() - start
    print(f"\nInference complete: {elapsed:.1f}s ({elapsed/len(rows)*1000:.0f}ms/pair)")

    # Metrics
    # Binary: correct=1, misclassification=0
    y_true = [1 if v == "correct" else 0 for v in ground_truth]
    y_pred = [1 if v == "correct" else 0 for v in predictions]
    valid_mask = [p in ("correct", "misclassification") for p in predictions]
    y_true_valid = [y for y, m in zip(y_true, valid_mask) if m]
    y_pred_valid = [y for y, m in zip(y_pred, valid_mask) if m]
    parse_errors = sum(1 for p in predictions if p not in ("correct", "misclassification"))

    print(f"\n{'='*60}")
    print("RESULTS")
    print(f"{'='*60}")
    print(f"Total pairs:    {len(rows)}")
    print(f"Valid predictions: {len(y_true_valid)}")
    print(f"Parse errors:   {parse_errors}")
    print(f"Speed:          {elapsed/len(rows)*1000:.0f}ms/pair")
    print()
    print(f"Overall accuracy: {accuracy_score(y_true_valid, y_pred_valid):.1%}")
    print()
    print("Classification report (0=misclassification, 1=correct):")
    print(classification_report(y_true_valid, y_pred_valid,
                                target_names=["misclassification", "correct"]))
    print("Confusion matrix:")
    print(confusion_matrix(y_true_valid, y_pred_valid))

    # Misclassification-specific metrics (the important ones)
    misclass_precision = precision_score(y_true_valid, y_pred_valid, pos_label=0)
    misclass_recall = recall_score(y_true_valid, y_pred_valid, pos_label=0)
    misclass_f1 = f1_score(y_true_valid, y_pred_valid, pos_label=0)
    print(f"\nMisclassification detection:")
    print(f"  Precision: {misclass_precision:.3f} (of flagged pairs, how many were truly wrong)")
    print(f"  Recall:    {misclass_recall:.3f} (of truly wrong pairs, how many were caught)")
    print(f"  F1:        {misclass_f1:.3f}")

    # Save results
    OUTPUT_CSV.parent.mkdir(parents=True, exist_ok=True)
    with open(OUTPUT_CSV, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=results[0].keys())
        writer.writeheader()
        writer.writerows(results)
    print(f"\nFull results saved to {OUTPUT_CSV}")


if __name__ == "__main__":
    main()
```

**Step 2: Run evaluation**

```bash
cd AIOMarketMaker/AIOMarketMaker.ML/Training/evaluator
py -3.12 eval.py
```

Expected output: accuracy, precision/recall for misclassification detection, confusion matrix, ms/pair latency. Check against success criteria:
- Accuracy within 5% of GPT
- Misclassification recall >80%
- Misclassification precision >80% (false alarm rate <20%)
- Speed <100ms/pair

**Step 3: Commit**

```bash
cd ../../../
git add AIOMarketMaker.ML/Training/evaluator/eval.py
git commit -m "feat(evaluator): add eval.py for model validation"
```

---

### Task 8: run.py — Batch Inference on Production Data

**Files:**
- Create: `AIOMarketMaker.ML/Training/evaluator/run.py`

**Step 1: Write run.py**

```python
"""
Run the fine-tuned evaluator on production comparable pairs.

Queries ListingRelationships for comparable pairs (optionally filtered
by confidence threshold), runs evaluator inference, and outputs
flagged misclassifications for cross-encoder retraining.

Usage:
    py -3.12 run.py                           # all comparable pairs
    py -3.12 run.py --max-confidence 0.80      # only low-confidence pairs
    py -3.12 run.py --job-id 42                # specific scrape job only
    py -3.12 run.py --limit 100 --dry-run      # preview
"""

import argparse
import csv
import json
import os
import sys
import time
from pathlib import Path

os.environ.setdefault("HF_HOME", "E:/DevCaches/huggingface")
os.environ.setdefault("TORCH_HOME", "E:/DevCaches/torch")

import pyodbc
import torch

DB_CONN = (
    "DRIVER={ODBC Driver 17 for SQL Server};"
    "SERVER=(localdb)\\MSSQLLocalDB;"
    "DATABASE=AIOMarketMaker;"
    "Trusted_Connection=yes;"
)

DATA_DIR = Path(__file__).parent.parent / "data"
DEFAULT_MODEL_DIR = "E:/Dev/ml-training/evaluator/v1/merged"
OUTPUT_CSV = DATA_DIR / "evaluator_corrections.csv"
DESC_LIMIT = 500


def parse_args():
    parser = argparse.ArgumentParser(description="Run evaluator on production data")
    parser.add_argument("--model-dir", type=str, default=DEFAULT_MODEL_DIR)
    parser.add_argument("--max-confidence", type=float, default=None,
                        help="Only evaluate pairs below this confidence")
    parser.add_argument("--job-id", type=int, default=None,
                        help="Only evaluate pairs from this scrape job")
    parser.add_argument("--limit", type=int, default=None)
    parser.add_argument("--dry-run", action="store_true")
    return parser.parse_args()


def fetch_pairs(args):
    """Query comparable pairs from production database."""
    conn = pyodbc.connect(DB_CONN)

    where_clauses = ["lr.IsComparable = 1"]
    if args.max_confidence:
        where_clauses.append(f"lr.SimilarityScore <= {args.max_confidence}")
    if args.job_id:
        where_clauses.append(f"a.ScrapeJobId = {args.job_id}")

    where = " AND ".join(where_clauses)
    limit = f"TOP {args.limit}" if args.limit else ""

    sql = f"""
    SELECT {limit}
        lr.ListingIdA, lr.ListingIdB,
        lr.IsComparable, lr.SimilarityScore,
        a.Title AS TitleA,
        REPLACE(REPLACE(LEFT(ISNULL(a.Description,''), {DESC_LIMIT}), CHAR(10), ' '), CHAR(13), ' ') AS DescA,
        b.Title AS TitleB,
        REPLACE(REPLACE(LEFT(ISNULL(b.Description,''), {DESC_LIMIT}), CHAR(10), ' '), CHAR(13), ' ') AS DescB,
        sj.SearchTerm
    FROM ListingRelationships lr
    INNER JOIN Listings a ON a.Id = lr.ListingIdA
    INNER JOIN Listings b ON b.Id = lr.ListingIdB
    INNER JOIN ScrapeJobs sj ON sj.Id = a.ScrapeJobId
    WHERE {where}
    ORDER BY lr.SimilarityScore ASC
    """

    cursor = conn.cursor()
    cursor.execute(sql)
    rows = cursor.fetchall()
    columns = [col[0] for col in cursor.description]
    conn.close()

    return [dict(zip(columns, row)) for row in rows]


def load_model(model_dir):
    """Load fine-tuned evaluator model."""
    try:
        from unsloth import FastLanguageModel
        model, tokenizer = FastLanguageModel.from_pretrained(
            model_dir, max_seq_length=512,
            load_in_4bit=True, dtype=torch.bfloat16,
        )
        FastLanguageModel.for_inference(model)
    except ImportError:
        from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig
        bnb_config = BitsAndBytesConfig(
            load_in_4bit=True, bnb_4bit_compute_dtype=torch.bfloat16,
        )
        model = AutoModelForCausalLM.from_pretrained(
            model_dir, quantization_config=bnb_config,
            device_map="auto", torch_dtype=torch.bfloat16,
        )
        tokenizer = AutoTokenizer.from_pretrained(model_dir)
        model.eval()

    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token
    return model, tokenizer


def predict(model, tokenizer, pair):
    """Run evaluator inference on a single pair."""
    onnx_label = 1 if pair["IsComparable"] else 0
    label_text = "COMPARABLE" if onnx_label == 1 else "NOT COMPARABLE"
    confidence = pair["SimilarityScore"]

    user_msg = (
        f"The classifier labeled this pair as {label_text} "
        f"(confidence: {confidence:.2f}).\n\n"
        f"LISTING A:\nTitle: {pair['TitleA']}\n"
        f"Description: {pair['DescA'][:500]}\n\n"
        f"LISTING B:\nTitle: {pair['TitleB']}\n"
        f"Description: {pair['DescB'][:500]}\n\n"
        f"Is the classifier's decision correct?"
    )

    messages = [{"role": "user", "content": user_msg}]
    text = tokenizer.apply_chat_template(
        messages, tokenize=False, add_generation_prompt=True
    )
    inputs = tokenizer(text, return_tensors="pt").to(model.device)

    with torch.no_grad():
        outputs = model.generate(
            **inputs, max_new_tokens=256, temperature=0.1,
            do_sample=False, pad_token_id=tokenizer.pad_token_id,
        )

    generated = outputs[0][inputs["input_ids"].shape[1]:]
    response_text = tokenizer.decode(generated, skip_special_tokens=True).strip()

    try:
        parsed = json.loads(response_text)
        return parsed.get("verdict", "unknown"), parsed.get("error_type", ""), \
               parsed.get("reasoning", ""), int(parsed.get("correct_label", onnx_label))
    except json.JSONDecodeError:
        if "misclassification" in response_text.lower():
            return "misclassification", "", response_text[:200], 1 - onnx_label
        return "parse_error", "", response_text[:200], onnx_label


def main():
    args = parse_args()

    print("Fetching production pairs...")
    pairs = fetch_pairs(args)
    print(f"Found {len(pairs)} pairs to evaluate")

    if not pairs:
        print("No pairs found. Exiting.")
        return

    if args.dry_run:
        print(f"\n[DRY RUN] Would evaluate {len(pairs)} pairs. Sample:")
        for p in pairs[:5]:
            print(f"  ({p['ListingIdA']}, {p['ListingIdB']}) "
                  f"score={p['SimilarityScore']:.2f} — {p['SearchTerm']}")
        return

    print(f"\nLoading model from {args.model_dir}...")
    model, tokenizer = load_model(args.model_dir)

    # Run inference
    corrections = []
    total_correct = 0
    total_misclass = 0

    start = time.time()
    for i, pair in enumerate(pairs):
        verdict, error_type, reasoning, correct_label = predict(model, tokenizer, pair)

        if verdict == "misclassification":
            total_misclass += 1
            corrections.append({
                "listing_id_a": pair["ListingIdA"],
                "listing_id_b": pair["ListingIdB"],
                "onnx_label": 1 if pair["IsComparable"] else 0,
                "corrected_label": correct_label,
                "similarity_score": pair["SimilarityScore"],
                "error_type": error_type,
                "reasoning": reasoning,
                "search_term": pair["SearchTerm"],
                "title_a": pair["TitleA"],
                "title_b": pair["TitleB"],
            })
        else:
            total_correct += 1

        if (i + 1) % 100 == 0:
            elapsed = time.time() - start
            print(f"  {i+1}/{len(pairs)} — {total_misclass} flagged "
                  f"({elapsed/(i+1)*1000:.0f}ms/pair)")

    elapsed = time.time() - start
    print(f"\nComplete: {elapsed:.1f}s ({elapsed/len(pairs)*1000:.0f}ms/pair)")
    print(f"  Correct:          {total_correct}")
    print(f"  Misclassification: {total_misclass} ({total_misclass/len(pairs):.1%})")

    if corrections:
        OUTPUT_CSV.parent.mkdir(parents=True, exist_ok=True)
        with open(OUTPUT_CSV, "w", newline="", encoding="utf-8") as f:
            writer = csv.DictWriter(f, fieldnames=corrections[0].keys())
            writer.writeheader()
            writer.writerows(corrections)
        print(f"\n{len(corrections)} corrections saved to {OUTPUT_CSV}")
        print("These can be merged into the cross-encoder training dataset.")

        # Error type breakdown
        from collections import Counter
        et_counts = Counter(c["error_type"] for c in corrections)
        print("\nError type breakdown:")
        for et, count in et_counts.most_common():
            print(f"  {et or 'unknown'}: {count}")
    else:
        print("\nNo misclassifications found.")


if __name__ == "__main__":
    main()
```

**Step 2: Test with small batch**

```bash
cd AIOMarketMaker/AIOMarketMaker.ML/Training/evaluator
py -3.12 run.py --limit 10 --dry-run
py -3.12 run.py --limit 10
```

Expected: evaluates 10 pairs, shows verdicts and any corrections.

**Step 3: Commit**

```bash
cd ../../../
git add AIOMarketMaker.ML/Training/evaluator/run.py
git commit -m "feat(evaluator): add run.py for production batch inference"
```

---

### Task 9: End-to-End Validation

Run the complete pipeline end-to-end and verify against success criteria.

**Step 1: Verify all files exist**

```bash
ls AIOMarketMaker.ML/Training/evaluator/
```

Expected: `README.md`, `collect.py`, `audit.py`, `train.py`, `eval.py`, `run.py`

**Step 2: Verify data files**

```bash
py -3.12 -c "
from pathlib import Path
data = Path('AIOMarketMaker/AIOMarketMaker.ML/Training/data')
for f in sorted(data.glob('evaluator*')):
    import os
    size = os.path.getsize(f)
    print(f'{f.name}: {size/1024:.0f} KB')
"
```

Expected: `evaluator_pairs_raw.csv`, `evaluator_audit_gpt.csv`, `evaluator_train.csv`, `evaluator_test.csv` all present.

**Step 3: Verify model files**

```bash
ls "E:/Dev/ml-training/evaluator/v1/lora_adapter/"
ls "E:/Dev/ml-training/evaluator/v1/merged/"
```

Expected: model weights, tokenizer files, adapter config.

**Step 4: Check success criteria from eval.py output**

| Criteria | Target | Actual |
|----------|--------|--------|
| Accuracy within 5% of GPT | >90%* | Check eval.py output |
| Misclassification recall | >80% | Check eval.py output |
| Misclassification precision | >80% | Check eval.py output |
| Inference speed | <100ms/pair | Check eval.py output |

*Assuming GPT baseline ~95% on this task.

**Step 5: Final commit**

```bash
git add AIOMarketMaker.ML/Training/evaluator/
git commit -m "feat(evaluator): complete evaluator training pipeline v1"
```
