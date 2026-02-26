"""
Benchmark GPT-5-mini against the 486 human-labeled v9 pairs.

Uses structured outputs (Pydantic) for reliable JSON responses.

Usage:
    python benchmark_gpt5mini.py [--dry-run] [--limit N]
"""

import asyncio
import csv
import json
import sys
import time
from pathlib import Path
from dataclasses import dataclass
from pydantic import BaseModel, Field

# Force UTF-8 output on Windows
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")

# Load API key from local.settings.json
SETTINGS_PATH = Path(__file__).parent.parent.parent.parent / "AIOMarketMaker.Console" / "local.settings.json"
with open(SETTINGS_PATH) as f:
    _settings = json.load(f)
API_KEY = _settings["Values"]["OpenAi:ApiKey"]

from openai import AsyncOpenAI

client = AsyncOpenAI(api_key=API_KEY)

MODEL = "gpt-5-mini"
MAX_CONCURRENT = 10
SEMAPHORE = asyncio.Semaphore(MAX_CONCURRENT)


# Structured output schema
class ClassificationResult(BaseModel):
    label: int = Field(description="1 if comparable for pricing, 0 if not comparable")
    reasoning: str = Field(description="Brief explanation of the classification decision")


SYSTEM_PROMPT = """You are a pricing comparability classifier for eBay listings.

Given two listings (ANCHOR and NEIGHBOR), determine if the neighbor is a valid pricing comparable for the anchor. Both listings are already in the same top-level eBay condition category (e.g., both "Used"), but condition can vary significantly within that category.

Answer label=1 (COMPARABLE) when a buyer would pay roughly the same for either listing. Answer label=0 (NOT COMPARABLE) when the neighbor's price would mislead a pricing estimate for the anchor.

## Important: Descriptions reveal condition

Scan BOTH descriptions for damage keywords: "smashed", "cracked", "broken", "battered", "damaged", "dead pixels", "fault". A listing can have a normal-looking title but significant damage disclosed in the description.

For product identity, trust the TITLE over the description — eBay sellers often use generic template descriptions that don't match the actual item (e.g., title says "PS5 Disc Drive" but description talks about a "console").

## COMPARABLE (label=1)

All of these must be true:
- Same product model, generation, storage, connectivity, screen size
- Similar condition within their category (both working with normal wear, or both damaged)
- Similar completeness (both bare product, or both with similar accessories)
- Color is OK to differ (Space Grey vs Silver) for electronics
- Trivial accessories (drum sticks, headphones, HDMI cables, manuals) do NOT make listings incomparable
- "With box" vs "without box" is a minor difference — still comparable

## NOT COMPARABLE (label=0)

Any ONE of these makes them not comparable:

**Spec/model differences:**
- Different storage (128GB vs 256GB), connectivity (Wi-Fi vs Cellular), CPU, screen size
- Different model generation or reference number
- Disc Edition vs Digital Edition

**Accessory vs full product:**
- A peripheral/add-on listed separately is NOT the same as the main product. IMPORTANT EXAMPLE: "PS5 Disc Drive" or "Disc Drive Digital Slim" (the ~£80 add-on that attaches to a Digital Edition) is NOT a "PS5 Console" (~£350-450). If the title says "Disc Drive" without "Console"/"System", it is the accessory.
- Similarly: "PS5 Controller" ≠ PS5 Console, "Apple Pencil" ≠ iPad, "Magic Keyboard" ≠ MacBook.

**Within-category condition differences (from title/description text):**
- Working unit vs for-parts/broken/cracked screen/smashed (huge price gap within "Used")
- "Excellent" / "Grade A" / "Mint" vs "Grade C" / "Fair" / "scratched" / "damaged" / "cracked handles" / "peeling interior"
- Professionally refurbished/graded/Apple-certified vs generic ungraded used
- Different battery health tiers when explicitly stated (100% vs 82%)
- Title warnings like "Read Description", "AS-IS", "Spares or Repair" signal hidden condition issues — standard-condition comps are NOT valid for these anchors

**eBay condition tier boundaries:**
- NEW or OPENED_NEVER_USED vs USED is a systematic 10-30% price difference — NOT comparable
- For-parts/not-working vs Used is NOT comparable
- These are formal eBay condition categories, not just seller descriptions

**Bundle/completeness differences:**
- Product + expensive accessory (iPad + Magic Keyboard, iPad + Apple Pencil, bike + rack + accessories) vs bare product
- Product + game bundle vs bare product
- Aftermarket modification that adds significant value (custom diamond dial on watch)
- Multi-item lot vs single item
- Missing standard controller for consoles: PS5 "console only" (no controller) ≠ PS5 "with controller and cables" — a missing controller (~£50-60) affects pricing
- BUT: trivial items (cables, charger, HDMI cable, drum sticks, headphones, manual) are NOT bundle differences

**Category-specific:**
- Luxury bags: different pattern (Damier Ebene vs Damier Azur vs Monogram), different size (PM vs MM vs GM)
- Chanel bags: Classic Flap, Boy Bag, Clutch on Chain, and Single Flap are different models — not interchangeable
- Watches: different reference number, different dial color creating distinct markets (116613LN vs 116613LB), men's vs women's, aftermarket diamond bezels/dials vs factory
- Watches: missing original bracelet (replaced with aftermarket rubber/leather strap) significantly affects value — NOT comparable to examples with original bracelet
- Watches with custom modifications: two watches with SIMILAR aftermarket modifications (e.g., both have custom blue bezels and diamond dials) ARE comparable — they occupy the same customization tier and price range. Only flag as not comparable when the modification LEVEL is different (stock vs heavily modified, or diamond lugs vs no diamond lugs).
- Luxury jewelry (Cartier, Tiffany, Bulgari): metal color (yellow gold vs white gold vs rose gold) is a significant price differentiator, NOT a minor cosmetic difference
- Electronics: storage, RAM, CPU, connectivity differences

## Key judgment calls

1. Cables/charger/trivial accessories alone are negligible (COMPARABLE)
2. Expensive add-ons ARE bundles: iPad "with Magic Keyboard" ≠ bare iPad (NOT COMPARABLE)
3. Within "Used", a cracked-screen iPad and a mint iPad are NOT comparable even though both are "Used"
4. When condition text is ambiguous or missing, lean toward comparable if specs match
5. Suspected counterfeit (luxury item at fraction of market rate with no explanation, generic AI-generated description) = NOT COMPARABLE
6. Read the FULL description carefully for damage keywords: "smashed", "cracked", "broken", "battered", "damaged camera/lens", "dead pixels". These override a seemingly-normal title.
7. Fitness equipment (Peloton, etc.): shoes, weights, bottles, phone mounts ARE material accessories that inflate price (~£50-100+). Different model years (2021 vs 2024) may represent different generations.
8. Omega watches: sub-references within the same family (e.g., 596.152 vs 596.1505 — same Ladies Seamaster 28mm with different dial color) ARE comparable if same size/era. But different generation references (e.g., 596.1xx vs 2224.xx or 2285.xx) are NOT comparable."""

USER_TEMPLATE = """ANCHOR listing (being priced):
Title: {anchor_title}
Description: {anchor_desc}

NEIGHBOR listing (potential comparable sale):
Title: {neighbor_title}
Description: {neighbor_desc}"""


@dataclass
class BenchmarkResult:
    anchor_id: int
    neighbor_id: int
    human_label: int
    model_label: int
    human_reasoning: str
    model_reasoning: str
    correct: bool
    product_name: str


async def classify_pair(row: dict) -> BenchmarkResult:
    """Call GPT-5-mini to classify a single pair using structured outputs."""
    async with SEMAPHORE:
        user_msg = USER_TEMPLATE.format(
            anchor_title=row["anchor_title"],
            anchor_desc=row["anchor_desc"][:800],
            neighbor_title=row["neighbor_title"],
            neighbor_desc=row["neighbor_desc"][:800],
        )

        try:
            response = await client.chat.completions.parse(
                model=MODEL,
                messages=[
                    {"role": "system", "content": SYSTEM_PROMPT},
                    {"role": "user", "content": user_msg},
                ],
                response_format=ClassificationResult,
            )

            parsed = response.choices[0].message.parsed
            if parsed is None:
                raise ValueError("Parsed response is None (possible refusal)")
            model_label = parsed.label
            model_reasoning = parsed.reasoning
        except Exception as e:
            print(f"  ERROR on pair ({row['anchor_id']}, {row['neighbor_id']}): {e}", file=sys.stderr)
            model_label = -1
            model_reasoning = f"ERROR: {e}"

        human_label = int(row["label"])
        return BenchmarkResult(
            anchor_id=int(row["anchor_id"]),
            neighbor_id=int(row["neighbor_id"]),
            human_label=human_label,
            model_label=model_label,
            human_reasoning=row.get("reasoning", ""),
            model_reasoning=model_reasoning,
            correct=(model_label == human_label),
            product_name=row.get("product_name", ""),
        )


async def main():
    dry_run = "--dry-run" in sys.argv
    limit = None
    for i, arg in enumerate(sys.argv):
        if arg == "--limit" and i + 1 < len(sys.argv):
            limit = int(sys.argv[i + 1])

    # Load v9 labeled pairs
    csv_path = Path(__file__).parent / "labeled_pairs_v9.csv"
    with open(csv_path, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        rows = list(reader)

    if limit:
        rows = rows[:limit]

    total = len(rows)
    print(f"Benchmark: GPT-5-mini vs {total} human-labeled v9 pairs")
    print(f"Model: {MODEL}")
    print(f"Concurrency: {MAX_CONCURRENT}")
    print(f"Structured outputs: YES (Pydantic)")
    print()

    if dry_run:
        print("[DRY RUN] Would classify these pairs:")
        for row in rows[:5]:
            print(f"  ({row['anchor_id']}, {row['neighbor_id']}) human={row['label']} - {row['product_name']}")
        print(f"  ... and {total - 5} more")
        return

    start = time.time()
    tasks = [classify_pair(row) for row in rows]
    results = await asyncio.gather(*tasks)
    elapsed = time.time() - start

    # Filter out errors
    valid = [r for r in results if r.model_label != -1]
    errors = [r for r in results if r.model_label == -1]

    # Overall accuracy
    correct = sum(1 for r in valid if r.correct)
    accuracy = correct / len(valid) if valid else 0

    # Confusion matrix
    tp = sum(1 for r in valid if r.human_label == 1 and r.model_label == 1)
    tn = sum(1 for r in valid if r.human_label == 0 and r.model_label == 0)
    fp = sum(1 for r in valid if r.human_label == 0 and r.model_label == 1)
    fn = sum(1 for r in valid if r.human_label == 1 and r.model_label == 0)

    precision = tp / (tp + fp) if (tp + fp) > 0 else 0
    recall = tp / (tp + fn) if (tp + fn) > 0 else 0
    f1 = 2 * precision * recall / (precision + recall) if (precision + recall) > 0 else 0

    # Per-category breakdown
    categories = {}
    for r in valid:
        cat = r.product_name
        if cat not in categories:
            categories[cat] = {"correct": 0, "total": 0, "fp": 0, "fn": 0}
        categories[cat]["total"] += 1
        if r.correct:
            categories[cat]["correct"] += 1
        if r.human_label == 0 and r.model_label == 1:
            categories[cat]["fp"] += 1
        if r.human_label == 1 and r.model_label == 0:
            categories[cat]["fn"] += 1

    # Print results
    print("=" * 70)
    print("RESULTS")
    print("=" * 70)
    print(f"Total pairs:     {total}")
    print(f"Valid responses:  {len(valid)}")
    print(f"Errors:           {len(errors)}")
    print(f"Time:             {elapsed:.1f}s ({elapsed/total:.2f}s per pair)")
    print()
    print(f"Overall accuracy: {accuracy:.1%} ({correct}/{len(valid)})")
    print()
    print("Confusion matrix:")
    print(f"  TP (both say comparable):     {tp}")
    print(f"  TN (both say not comparable): {tn}")
    print(f"  FP (model says comp, human says not): {fp}")
    print(f"  FN (model says not comp, human says comp): {fn}")
    print()
    print(f"Precision: {precision:.3f}")
    print(f"Recall:    {recall:.3f}")
    print(f"F1:        {f1:.3f}")
    print()
    print("Per-category breakdown:")
    print(f"  {'Category':<30} {'Acc':>6} {'Correct':>8} {'Total':>6} {'FP':>4} {'FN':>4}")
    print(f"  {'-'*30} {'-'*6} {'-'*8} {'-'*6} {'-'*4} {'-'*4}")
    for cat in sorted(categories.keys()):
        c = categories[cat]
        acc = c["correct"] / c["total"] if c["total"] > 0 else 0
        print(f"  {cat:<30} {acc:>5.1%} {c['correct']:>8} {c['total']:>6} {c['fp']:>4} {c['fn']:>4}")

    # Print disagreements for analysis
    disagreements = [r for r in valid if not r.correct]
    if disagreements:
        print()
        print(f"DISAGREEMENTS ({len(disagreements)} pairs):")
        print("-" * 70)
        for r in disagreements[:40]:
            direction = "FP" if r.human_label == 0 and r.model_label == 1 else "FN"
            print(f"  [{direction}] ({r.anchor_id}, {r.neighbor_id}) {r.product_name}")
            print(f"    Human: label={r.human_label} -- {r.human_reasoning[:120]}")
            print(f"    Model: label={r.model_label} -- {r.model_reasoning[:120]}")
            print()
        if len(disagreements) > 40:
            print(f"  ... and {len(disagreements) - 40} more disagreements")

    # Save full results to CSV
    output_path = Path(__file__).parent / "benchmark_gpt5mini_results.csv"
    with open(output_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["anchor_id", "neighbor_id", "product_name", "human_label", "model_label", "correct", "human_reasoning", "model_reasoning"])
        for r in results:
            writer.writerow([r.anchor_id, r.neighbor_id, r.product_name, r.human_label, r.model_label, r.correct, r.human_reasoning, r.model_reasoning])
    print(f"\nFull results saved to: {output_path}")


if __name__ == "__main__":
    asyncio.run(main())
