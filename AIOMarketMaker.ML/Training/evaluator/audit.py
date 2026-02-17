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
