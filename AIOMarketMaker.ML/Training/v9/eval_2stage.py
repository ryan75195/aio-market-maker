"""
Benchmark GPT-5-mini with 2-stage classification pipeline.

Stage 1 (Analyzer): Extract structured facts from both listings (no label).
Stage 2 (Judge): Read the analysis and make the holistic pricing decision.

This separates "reading comprehension" from "judgment", letting each
stage focus on what it's good at.

Usage:
    python benchmark_gpt5mini_2stage.py [--dry-run] [--limit N]
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


# ── Stage 1: Analyzer schema ──
class ListingAnalysis(BaseModel):
    anchor_product: str = Field(description="What is the ANCHOR product? Model, generation, storage, connectivity, size. If title says 'Disc Drive' without 'Console', it's the accessory not the console.")
    neighbor_product: str = Field(description="What is the NEIGHBOR product? Same details.")
    products_match: bool = Field(description="Exact same product model/variant? False if different storage, connectivity, edition, or accessory vs full product.")
    anchor_condition: str = Field(description="ANCHOR condition from title+description. Look for: damage (smashed/cracked/broken/battered), grades (A/B/C/Fair/Mint), battery %, refurbished status, 'Read Description' warnings. Say 'standard used' if nothing stated.")
    neighbor_condition: str = Field(description="NEIGHBOR condition. Same details.")
    anchor_accessories: str = Field(description="What's included with ANCHOR? List: controller, cables, box, keyboard, pencil, etc. Note if 'console only' or missing items.")
    neighbor_accessories: str = Field(description="What's included with NEIGHBOR? Same details.")
    key_differences: str = Field(description="List ALL material differences between the two listings. Be specific.")


ANALYZER_PROMPT = """You are a listing comparison analyst for eBay. Extract structured facts from both listings.

IMPORTANT:
- Trust the TITLE over the description for product identity. eBay sellers use generic template descriptions.
- "PS5 Disc Drive" or "Disc Drive Digital Slim" in the title = the ~£80 accessory add-on, NOT a ~£400 console
- Scan descriptions carefully for damage: "smashed", "cracked", "broken", "battered", "damaged", "fault", "peeling"
- Note battery health percentages, condition grades, refurbished status
- List all included accessories explicitly mentioned"""


# ── Stage 2: Judge schema ──
class JudgeResult(BaseModel):
    label: int = Field(description="1 if comparable for pricing, 0 if not comparable")
    reasoning: str = Field(description="Brief explanation of the pricing comparability decision")


JUDGE_PROMPT = """You are a pricing comparability judge for eBay listings.

Given a structured analysis of two listings, decide if the NEIGHBOR is a valid pricing comparable for the ANCHOR. Answer label=1 if a buyer would pay roughly the same for either. Answer label=0 if the neighbor's price would mislead a pricing estimate.

## COMPARABLE (label=1) — all must be true:
- Same product model/generation/storage/connectivity
- Similar condition (both working, or both damaged)
- Similar completeness (no major accessory difference)

## NOT COMPARABLE (label=0) — any ONE of these:
- Different product (storage, connectivity, edition, accessory vs full product)
- Significant condition gap: working vs broken, Grade A vs Grade C, mint vs damaged, new vs used
- One has expensive extras the other doesn't: keyboard, pencil, extra controller
- Missing standard items: console without controller vs with controller (~£50-60 difference)
- Apple-certified refurbished vs generic used
- "Read Description"/"AS-IS" anchor vs standard-condition comp
- Different luxury bag pattern (Monogram vs Damier) or size (PM vs MM)
- Different watch reference number or dial color market (116613LN vs 116613LB)
- Watch missing original bracelet
- Luxury jewelry: different metal color (yellow gold vs white gold)

## Judgment nuances:
- Color differences (Space Grey vs Silver) are OK for electronics
- Trivial accessories (cables, charger, HDMI, drum sticks, headphones, box) are OK
- Watches with SIMILAR custom modifications (both diamond/blue bezel) = comparable
- When condition is ambiguous or missing, lean comparable if product matches
- Suspected counterfeit (luxury item at tiny fraction of retail) = not comparable"""


JUDGE_TEMPLATE = """Here is the structured analysis of two eBay listings:

ANCHOR product: {anchor_product}
NEIGHBOR product: {neighbor_product}
Products match: {products_match}

ANCHOR condition: {anchor_condition}
NEIGHBOR condition: {neighbor_condition}

ANCHOR accessories: {anchor_accessories}
NEIGHBOR accessories: {neighbor_accessories}

Key differences: {key_differences}

Based on this analysis, is the neighbor a valid pricing comparable for the anchor?"""


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
    analysis_summary: str


async def classify_pair(row: dict) -> BenchmarkResult:
    """Two-stage classification: analyze then judge."""
    async with SEMAPHORE:
        user_msg = f"""ANCHOR listing:
Title: {row["anchor_title"]}
Description: {row["anchor_desc"][:800]}

NEIGHBOR listing:
Title: {row["neighbor_title"]}
Description: {row["neighbor_desc"][:800]}"""

        try:
            # ── Stage 1: Analyze ──
            analysis_response = await client.chat.completions.parse(
                model=MODEL,
                messages=[
                    {"role": "system", "content": ANALYZER_PROMPT},
                    {"role": "user", "content": user_msg},
                ],
                response_format=ListingAnalysis,
            )
            analysis = analysis_response.choices[0].message.parsed
            if analysis is None:
                raise ValueError("Analysis response is None")

            # ── Stage 2: Judge ──
            judge_msg = JUDGE_TEMPLATE.format(
                anchor_product=analysis.anchor_product,
                neighbor_product=analysis.neighbor_product,
                products_match=analysis.products_match,
                anchor_condition=analysis.anchor_condition,
                neighbor_condition=analysis.neighbor_condition,
                anchor_accessories=analysis.anchor_accessories,
                neighbor_accessories=analysis.neighbor_accessories,
                key_differences=analysis.key_differences,
            )

            judge_response = await client.chat.completions.parse(
                model=MODEL,
                messages=[
                    {"role": "system", "content": JUDGE_PROMPT},
                    {"role": "user", "content": judge_msg},
                ],
                response_format=JudgeResult,
            )
            judge = judge_response.choices[0].message.parsed
            if judge is None:
                raise ValueError("Judge response is None")

            model_label = judge.label
            model_reasoning = judge.reasoning
            analysis_summary = f"prod={analysis.products_match} | anchor_cond=[{analysis.anchor_condition[:60]}] | neighbor_cond=[{analysis.neighbor_condition[:60]}] | diffs=[{analysis.key_differences[:80]}]"

        except Exception as e:
            print(f"  ERROR on pair ({row['anchor_id']}, {row['neighbor_id']}): {e}", file=sys.stderr)
            model_label = -1
            model_reasoning = f"ERROR: {e}"
            analysis_summary = "ERROR"

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
            analysis_summary=analysis_summary,
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
    print(f"Benchmark: GPT-5-mini 2-Stage vs {total} human-labeled v9 pairs")
    print(f"Model: {MODEL}")
    print(f"Concurrency: {MAX_CONCURRENT}")
    print(f"Pipeline: Analyzer → Judge (2 API calls per pair)")
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
    print(f"API calls:        {total * 2} (2 per pair)")
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

    # Print disagreements
    disagreements = [r for r in valid if not r.correct]
    if disagreements:
        print()
        print(f"DISAGREEMENTS ({len(disagreements)} pairs):")
        print("-" * 70)
        for r in disagreements[:40]:
            direction = "FP" if r.human_label == 0 and r.model_label == 1 else "FN"
            print(f"  [{direction}] ({r.anchor_id}, {r.neighbor_id}) {r.product_name}")
            print(f"    Analysis: {r.analysis_summary[:140]}")
            print(f"    Human: label={r.human_label} -- {r.human_reasoning[:100]}")
            print(f"    Judge: label={r.model_label} -- {r.model_reasoning[:100]}")
            print()
        if len(disagreements) > 40:
            print(f"  ... and {len(disagreements) - 40} more disagreements")

    # Save full results
    output_path = Path(__file__).parent / "benchmark_gpt5mini_2stage_results.csv"
    with open(output_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["anchor_id", "neighbor_id", "product_name", "human_label", "model_label",
                         "correct", "analysis_summary", "human_reasoning", "model_reasoning"])
        for r in results:
            writer.writerow([r.anchor_id, r.neighbor_id, r.product_name, r.human_label, r.model_label,
                             r.correct, r.analysis_summary, r.human_reasoning, r.model_reasoning])
    print(f"\nFull results saved to: {output_path}")


if __name__ == "__main__":
    asyncio.run(main())
