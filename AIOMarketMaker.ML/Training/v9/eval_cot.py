"""
Benchmark GPT-5-mini with chain-of-thought structured outputs.

Forces model to reason about each dimension (product, condition, bundle)
BEFORE producing the final label. Field order in Pydantic schema controls
generation order.

Usage:
    python benchmark_gpt5mini_cot.py [--dry-run] [--limit N]
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


# Structured chain-of-thought schema.
# CRITICAL: sub-analysis fields BEFORE label so model reasons first.
class ClassificationResult(BaseModel):
    anchor_product: str = Field(description="What product is the ANCHOR? Include model, storage, connectivity, size. Note if title says accessory (e.g. 'Disc Drive') vs full product.")
    neighbor_product: str = Field(description="What product is the NEIGHBOR? Same details.")
    same_product: bool = Field(description="Are these the exact same product model/variant/generation? False if different storage, connectivity, edition, or if one is an accessory and the other is the full product.")
    product_reasoning: str = Field(description="Brief explanation of product match decision")
    anchor_condition: str = Field(description="Condition of ANCHOR from title+description: any damage, grade, battery health, 'Read Description' warnings, refurbished status. Say 'unknown' if not stated.")
    neighbor_condition: str = Field(description="Condition of NEIGHBOR from title+description: same details.")
    similar_condition: bool = Field(description="Are both in a similar condition tier? False if one is damaged/cracked/smashed and the other is working, or Grade A vs Grade C, or refurbished vs generic used, or NEW vs USED.")
    condition_reasoning: str = Field(description="Brief explanation of condition comparison")
    anchor_completeness: str = Field(description="What's included with ANCHOR? Controller, cables, box, accessories, keyboard, pencil, etc. Note if 'console only' or missing standard items.")
    neighbor_completeness: str = Field(description="What's included with NEIGHBOR? Same details.")
    similar_completeness: bool = Field(description="Similar bundle/completeness? False if one includes expensive accessories the other doesn't (keyboard, pencil, extra controller) or one is missing standard items (no controller for a console).")
    completeness_reasoning: str = Field(description="Brief explanation of completeness comparison")
    label: int = Field(description="FINAL VERDICT: 1 if comparable (same product AND similar condition AND similar completeness), 0 if ANY dimension fails")
    reasoning: str = Field(description="One-sentence summary of the classification decision")


SYSTEM_PROMPT = """You are a pricing comparability classifier for eBay listings.

Given two listings (ANCHOR and NEIGHBOR), determine if the neighbor is a valid pricing comparable for the anchor. Both listings are already in the same top-level eBay condition category (e.g., both "Used"), but condition can vary significantly within that category.

You will analyze THREE dimensions, then give a final verdict:
1. **Product match** — same model, generation, storage, connectivity, screen size?
2. **Condition match** — similar condition within their eBay category?
3. **Completeness match** — similar accessories and bundle contents?

label=1 (COMPARABLE) only if ALL THREE dimensions pass. label=0 if ANY dimension fails.

## Product matching rules

- Trust the TITLE over the description for product identity — eBay sellers use generic template descriptions
- "PS5 Disc Drive" or "Disc Drive Digital Slim" in the title (without "Console"/"System") = the ~£80 add-on accessory, NOT a ~£400 console
- Different storage (128GB vs 256GB), connectivity (Wi-Fi vs Cellular), CPU = different product
- Different model generation or reference number = different product
- Disc Edition vs Digital Edition = different product
- Color differences (Space Grey vs Silver) are OK for electronics
- Luxury bags: different pattern (Damier Ebene vs Monogram) or size (PM vs MM vs GM) = different product
- Chanel: Classic Flap vs Boy Bag vs Clutch on Chain = different product
- Watches: different reference number or dial color creating distinct markets (116613LN vs 116613LB) = different product
- Watches with SIMILAR custom modifications (both have custom blue bezels + diamond dials) = same product tier. Only flag different when modification LEVEL differs (stock vs heavily modified, diamond lugs vs none).
- Luxury jewelry: metal color (yellow gold vs white gold vs rose gold) = different product

## Condition matching rules

- Scan descriptions carefully for: "smashed", "cracked", "broken", "battered", "damaged", "dead pixels", "fault", "peeling", "deteriorated"
- Working vs for-parts/broken/cracked = NOT similar
- "Excellent"/"Grade A"/"Mint" vs "Grade C"/"Fair"/"scratched"/"damaged" = NOT similar
- Apple-certified refurbished vs generic used = NOT similar
- Different battery health tiers when stated (100% vs 82%) = NOT similar
- "Read Description" / "AS-IS" in title = hidden condition issues, standard-condition comps are NOT valid
- NEW or OPENED_NEVER_USED vs USED = NOT similar (10-30% price gap)
- When condition text is ambiguous or missing, default to similar if specs match
- Suspected counterfeit (luxury item at fraction of market rate) = NOT similar

## Completeness matching rules

- iPad + Magic Keyboard or Apple Pencil vs bare iPad = NOT similar
- Console without controller vs console with controller = NOT similar (£50-60 difference)
- Bike + rack + accessories vs bare bike = NOT similar
- Watch missing original bracelet (on aftermarket strap) vs original bracelet = NOT similar
- Trivial items (HDMI cable, charger, drum sticks, headphones, manual, box) = OK to differ
- Fitness equipment: shoes, weights, mounts = material (£50-100+ value)"""

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
    same_product: bool
    similar_condition: bool
    similar_completeness: bool


async def classify_pair(row: dict) -> BenchmarkResult:
    """Call GPT-5-mini to classify a single pair using CoT structured outputs."""
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
            same_product = parsed.same_product
            similar_condition = parsed.similar_condition
            similar_completeness = parsed.similar_completeness
        except Exception as e:
            print(f"  ERROR on pair ({row['anchor_id']}, {row['neighbor_id']}): {e}", file=sys.stderr)
            model_label = -1
            model_reasoning = f"ERROR: {e}"
            same_product = False
            similar_condition = False
            similar_completeness = False

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
            same_product=same_product,
            similar_condition=similar_condition,
            similar_completeness=similar_completeness,
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
    print(f"Benchmark: GPT-5-mini CoT vs {total} human-labeled v9 pairs")
    print(f"Model: {MODEL}")
    print(f"Concurrency: {MAX_CONCURRENT}")
    print(f"Structured outputs: YES (Pydantic CoT)")
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

    # Dimension analysis — where does the model say it fails?
    dim_product_fail = sum(1 for r in valid if not r.same_product)
    dim_condition_fail = sum(1 for r in valid if not r.similar_condition)
    dim_completeness_fail = sum(1 for r in valid if not r.similar_completeness)

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
    print("Dimension analysis (model's self-reported failures):")
    print(f"  Product mismatch:      {dim_product_fail}")
    print(f"  Condition mismatch:    {dim_condition_fail}")
    print(f"  Completeness mismatch: {dim_completeness_fail}")
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
            dims = []
            if not r.same_product:
                dims.append("PROD")
            if not r.similar_condition:
                dims.append("COND")
            if not r.similar_completeness:
                dims.append("COMP")
            dim_str = ",".join(dims) if dims else "ALL_PASS"
            print(f"  [{direction}] ({r.anchor_id}, {r.neighbor_id}) {r.product_name} dims_failed=[{dim_str}]")
            print(f"    Human: label={r.human_label} -- {r.human_reasoning[:120]}")
            print(f"    Model: label={r.model_label} -- {r.model_reasoning[:120]}")
            print()
        if len(disagreements) > 40:
            print(f"  ... and {len(disagreements) - 40} more disagreements")

    # Save full results to CSV
    output_path = Path(__file__).parent / "benchmark_gpt5mini_cot_results.csv"
    with open(output_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["anchor_id", "neighbor_id", "product_name", "human_label", "model_label",
                         "correct", "same_product", "similar_condition", "similar_completeness",
                         "human_reasoning", "model_reasoning"])
        for r in results:
            writer.writerow([r.anchor_id, r.neighbor_id, r.product_name, r.human_label, r.model_label,
                             r.correct, r.same_product, r.similar_condition, r.similar_completeness,
                             r.human_reasoning, r.model_reasoning])
    print(f"\nFull results saved to: {output_path}")


if __name__ == "__main__":
    asyncio.run(main())
