"""
Experiment: Top-down taxonomy construction.

Phase 1: GPT-5-nano defines a taxonomy skeleton (axes + values) for a product
Phase 2: Qwen3-8B locally extracts axis values from each listing title
Eval:    Compare cell quality (intra-cell CV, coverage) vs current bottom-up taxonomy

Usage:
    python experiment_topdown_taxonomy.py
    python experiment_topdown_taxonomy.py --job-id 1030 --search-term "Rolex Submariner"
    python experiment_topdown_taxonomy.py --skip-skeleton  # reuse saved skeleton
    python experiment_topdown_taxonomy.py --n 200          # limit titles for speed
"""

import argparse
import json
import math
import os
import time
from pathlib import Path

import numpy as np
import pyodbc
from sklearn.feature_extraction.text import TfidfVectorizer
from sklearn.metrics.pairwise import cosine_distances

# ── Config ──────────────────────────────────────────────────────────────────

SETTINGS_PATH = Path(__file__).parent.parent.parent / "AIOMarketMaker.Console" / "local.settings.json"
OUTPUT_DIR = Path(__file__).parent / "data" / "topdown_taxonomy"
OUTPUT_DIR.mkdir(parents=True, exist_ok=True)


def load_settings():
    with open(SETTINGS_PATH) as f:
        return json.load(f)


def get_db_connection(settings):
    conn_str = settings["SqlConnectionString"]
    # Convert LocalDB connection string to pyodbc format
    server = "(localdb)\\MSSQLLocalDB"
    return pyodbc.connect(
        f"DRIVER={{ODBC Driver 17 for SQL Server}};"
        f"SERVER={server};"
        f"DATABASE=AIOMarketMaker;"
        f"Trusted_Connection=yes;"
    )


# ── Diverse sampling ─────────────────────────────────────────────────────────


def sample_diverse(titles, n=250, seed=42):
    """Select n maximally diverse titles using greedy farthest-point sampling.

    Uses TF-IDF cosine distance so the sample covers the full variety of
    listings rather than clustering around the most common type.
    """
    if len(titles) <= n:
        return list(range(len(titles)))

    rng = np.random.RandomState(seed)
    vectorizer = TfidfVectorizer(analyzer="char_wb", ngram_range=(3, 5), max_features=5000)
    tfidf = vectorizer.fit_transform(titles)

    # Start from a random point
    selected = [rng.randint(len(titles))]
    # Track min distance from each point to the selected set
    min_dists = cosine_distances(tfidf, tfidf[selected[0]]).ravel()

    for _ in range(n - 1):
        # Pick the point farthest from any selected point
        next_idx = int(np.argmax(min_dists))
        selected.append(next_idx)
        # Update min distances
        new_dists = cosine_distances(tfidf, tfidf[next_idx]).ravel()
        min_dists = np.minimum(min_dists, new_dists)
        # Zero out already-selected so they can't be picked again
        for s in selected:
            min_dists[s] = 0.0

    return selected


# ── Phase 1: GPT-5-nano defines taxonomy skeleton ──────────────────────────

SKELETON_PROMPT = """You are a product taxonomy expert for eBay marketplace analysis.

Given a product search term and a diverse sample of listing titles, define a taxonomy that classifies listings into comparable groups for pricing.

## Product: {search_term}

## Sample titles ({sample_count} diverse titles from {total_count} total):
{sample_titles}

## Instructions

Define axes (dimensions) that distinguish PRODUCT VARIANTS — things that make two listings NOT directly comparable for pricing. Two listings with the same values on all axes should be the same product at potentially different prices.

Key principles:

WHAT TO INCLUDE:
- Axes that define the product variant: model/reference, size, capacity, color, generation, configuration
- Include a "reference" axis if the product has model numbers (e.g. "16610", "katana-100")
- Be EXHAUSTIVE with values — missing a value means listings won't be classified
- Include values from your product knowledge, not just from the sample titles

WHAT TO INCLUDE AS AXES (if relevant to the product):
- Bundle contents: included games, controllers, accessories — these significantly affect price
- Box/packaging: "boxed", "no box" — affects resale value
- Completeness: "console only", "with charger", "with all accessories"

WHAT TO EXCLUDE:
- Do NOT include listing condition (new/used/sealed/refurbished) — condition is tracked separately, not a variant axis
- Do NOT include seller-specific attributes (free shipping, warranty, returns)
- Do NOT include "retired" or "discontinued" status

VALUE FORMAT:
- Values must be lowercase tokens that can be found in eBay titles
- Use simple, flat values — NOT compound values. Use separate axes instead.
  BAD: reference values ["gen4-silver", "gen4-black"] (mixes generation + color)
  GOOD: separate "generation" axis ["gen3", "gen4"] and "color" axis ["silver", "black"]
- For values with numbers, use the format sellers actually write: "gen 3" not "gen3", "size 9" not "size9", "mk2" or "mkii" (include common variations)
- Keep values short (1-3 words max)

AXIS QUALITY:
- Each axis should represent ONE concept (don't mix material and color)
- Order axes by price impact (most impactful first)
- Aim for 3-6 axes per product (rarely more than 8)
- If the product has very few meaningful variants, use fewer axes

Return JSON:
{{
    "axes": [
        {{
            "name": "reference",
            "description": "Model reference number - primary price driver",
            "values": ["16610", "114060", "126610ln", "5513"],
            "extraction_hints": "Usually a 4-6 digit number, sometimes with letter suffix"
        }},
        {{
            "name": "material",
            "description": "Case material",
            "values": ["steel", "gold", "two-tone", "platinum"],
            "extraction_hints": "Look for SS, steel, gold, YG, WG, platinum"
        }}
    ]
}}"""


def generate_skeleton(search_term, sample_titles, total_count, settings):
    """Call GPT-5-nano to define taxonomy skeleton."""
    from openai import OpenAI

    client = OpenAI(api_key=settings["OpenAi"]["ApiKey"])

    titles_text = "\n".join(f"- {t}" for t in sample_titles)
    prompt = SKELETON_PROMPT.format(
        search_term=search_term,
        total_count=total_count,
        sample_count=len(sample_titles),
        sample_titles=titles_text,
    )

    print(f"Calling GPT-5-nano with {len(sample_titles)} diverse titles...")
    t0 = time.time()

    response = client.chat.completions.create(
        model="gpt-5-nano",
        messages=[
            {"role": "system", "content": "You are a product taxonomy expert. Return only valid JSON."},
            {"role": "user", "content": prompt},
        ],
        response_format={"type": "json_object"},
    )

    elapsed = time.time() - t0
    result = json.loads(response.choices[0].message.content)
    print(f"Skeleton generated in {elapsed:.1f}s — {len(result['axes'])} axes")

    for ax in result["axes"]:
        print(f"  {ax['name']}: {len(ax['values'])} values — {ax.get('description', '')[:60]}")

    return result


# ── Phase 2a: Keyword extraction (fast) ────────────────────────────────────

import re


def extract_axes_keyword(title, skeleton):
    """Extract axis values from title using simple keyword matching.

    Since the skeleton already defines all possible values, we just
    scan the title for each value. ~0.001s/title vs ~8s/title for LLM.
    """
    title_lower = title.lower()
    result = {}

    for ax in skeleton["axes"]:
        name = ax["name"]
        best_match = None
        best_len = 0

        # Try longest values first to prefer specific matches
        for value in sorted(ax["values"], key=len, reverse=True):
            val_lower = value.lower()
            # Word boundary match to avoid partial matches
            # e.g. "16610" shouldn't match inside "116610"
            pattern = r'(?<![a-z0-9])' + re.escape(val_lower) + r'(?![a-z0-9])'
            if re.search(pattern, title_lower):
                best_match = val_lower
                break  # Already sorted longest-first, take first match

        if best_match:
            result[name] = best_match

    return result


def extract_axes_keyword_batch(titles, skeleton, batch_label=""):
    """Extract axes for all titles using keyword matching."""
    results = []
    for i, title in enumerate(titles):
        extracted = extract_axes_keyword(title, skeleton)
        results.append(extracted)

        if (i + 1) % 500 == 0:
            print(f"  [{batch_label}] {i+1}/{len(titles)} keyword-extracted")

    return results


# ── Phase 2b: Qwen3-8B extracts axis values from titles ────────────────────

EXTRACT_PROMPT = """Extract product attributes from this eBay listing title.

CRITICAL RULES:
- ONLY extract values whose text appears verbatim in the title
- If a value is NOT written in the title, you MUST return null for that axis
- Do NOT use your product knowledge to fill in missing values
- Do NOT guess, infer, or assume defaults — only match words actually present
- When multiple values could match, pick the one whose FULL text best matches what's in the title
- Return values exactly as listed below (lowercase), not as they appear in the title

Axes to extract:
{axes_description}

Title: {title}

Return JSON with axis names as keys. Use null for any axis where no matching text appears in the title.
Example: {{"reference": "16610", "material": "steel", "bezel_color": null, "year": null}}

JSON:"""


def load_local_model(model_name="Qwen/Qwen3-8B"):
    """Load Qwen3-8B with 4-bit quantization."""
    import torch
    from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig

    print(f"Loading {model_name}...")
    t0 = time.time()

    tokenizer = AutoTokenizer.from_pretrained(model_name)
    quant_config = BitsAndBytesConfig(
        load_in_4bit=True,
        bnb_4bit_compute_dtype=torch.bfloat16,
        bnb_4bit_quant_type="nf4",
    )
    model = AutoModelForCausalLM.from_pretrained(
        model_name,
        torch_dtype=torch.bfloat16,
        device_map="auto",
        quantization_config=quant_config,
    )

    print(f"Model loaded in {time.time() - t0:.1f}s")
    return model, tokenizer


def extract_axes_local(model, tokenizer, title, skeleton):
    """Use Qwen3-8B to extract axis values from a single title."""
    import torch

    axes_desc = "\n".join(
        f"- {ax['name']}: {ax.get('description', '')}. Values: {', '.join(ax['values'][:15])}"
        for ax in skeleton["axes"]
    )

    prompt = EXTRACT_PROMPT.format(axes_description=axes_desc, title=title)

    messages = [
        {"role": "system", "content": "Extract product attributes from the title text ONLY. Never infer or guess values not written in the title. Return only valid JSON."},
        {"role": "user", "content": prompt},
    ]

    text = tokenizer.apply_chat_template(
        messages,
        tokenize=False,
        add_generation_prompt=True,
        enable_thinking=False,
    )

    inputs = tokenizer(text, return_tensors="pt").to(model.device)
    input_len = inputs["input_ids"].shape[1]

    with torch.no_grad():
        outputs = model.generate(
            **inputs,
            max_new_tokens=200,
            do_sample=False,
        )

    response = tokenizer.decode(outputs[0][input_len:], skip_special_tokens=True)

    try:
        start = response.index("{")
        end = response.rindex("}") + 1
        raw = json.loads(response[start:end])

        # Post-process: clean "null" strings and validate against title
        cleaned = {}
        title_lower = title.lower()
        for k, v in raw.items():
            if v is None or str(v).strip().lower() in ("null", "none", "n/a", ""):
                continue
            val = str(v).strip().lower()
            # Validate: at least part of the extracted value should appear in the title
            # Split on spaces and hyphens, check if any significant word (3+ chars)
            # has its stem present in the title
            val_words = [w for w in re.split(r'[\s\-]+', val) if len(w) > 2]
            if val_words and not any(w[:4] in title_lower for w in val_words):
                continue  # Hallucinated — no evidence in title
            cleaned[k] = val

        return cleaned if cleaned else None
    except (ValueError, json.JSONDecodeError):
        return None


def extract_axes_batch(model, tokenizer, titles, skeleton, batch_label=""):
    """Extract axes for all titles, with progress."""
    results = []
    parse_errors = 0
    times = []

    for i, title in enumerate(titles):
        t0 = time.time()
        extracted = extract_axes_local(model, tokenizer, title, skeleton)
        elapsed = time.time() - t0
        times.append(elapsed)

        if extracted is None:
            parse_errors += 1
            results.append({})
        else:
            # Normalize: lowercase, strip whitespace, None for null
            clean = {}
            for k, v in extracted.items():
                if v is not None and str(v).strip():
                    clean[k] = str(v).strip().lower()
            results.append(clean)

        if (i + 1) % 25 == 0 or i == 0:
            avg_t = sum(times) / len(times)
            remaining = (len(titles) - i - 1) * avg_t
            print(f"  [{batch_label}] {i+1}/{len(titles)} — {avg_t:.1f}s/title — ~{remaining/60:.0f}m remaining — {parse_errors} parse errors")

    return results, parse_errors


# ── Evaluation ──────────────────────────────────────────────────────────────

def build_cells(titles, assignments, statuses, prices, conditions):
    """Group listings into cells based on extracted axes + condition."""
    cells = {}

    for i, (assignment, status, price, condition) in enumerate(
        zip(assignments, statuses, prices, conditions)
    ):
        if not assignment:
            continue

        # Build cell key (same logic as CellPricingService)
        cell = dict(assignment)
        if condition:
            cell["condition"] = condition.lower()

        cell_key = " | ".join(f"{k}={v}" for k, v in sorted(cell.items()))

        if cell_key not in cells:
            cells[cell_key] = {"active": [], "sold": [], "titles": []}

        if status and status.lower() == "sold" and price:
            cells[cell_key]["sold"].append(price)
        elif price:
            cells[cell_key]["active"].append(price)

        cells[cell_key]["titles"].append(titles[i])

    return cells


def coefficient_of_variation(values):
    if len(values) < 2:
        return 0.0
    mean = sum(values) / len(values)
    if mean == 0:
        return 0.0
    variance = sum((v - mean) ** 2 for v in values) / len(values)
    return math.sqrt(variance) / mean


def evaluate_cells(cells, min_comps=3):
    """Compute quality metrics for cell groupings."""
    cvs = []
    cell_details = []
    total_assigned = sum(len(c["sold"]) + len(c["active"]) for c in cells.values())

    for cell_key, cell in sorted(cells.items(), key=lambda x: len(x[1]["sold"]), reverse=True):
        sold_count = len(cell["sold"])
        active_count = len(cell["active"])
        cv = coefficient_of_variation(cell["sold"]) if sold_count >= 2 else None

        if sold_count >= min_comps and cv is not None:
            cvs.append(cv)

        median_sold = sorted(cell["sold"])[sold_count // 2] if sold_count > 0 else None
        price_range = (min(cell["sold"]), max(cell["sold"])) if sold_count > 0 else None

        cell_details.append({
            "cell_key": cell_key,
            "sold": sold_count,
            "active": active_count,
            "cv": round(cv, 3) if cv is not None else None,
            "median_sold": round(median_sold, 2) if median_sold else None,
            "price_range": [round(p, 2) for p in price_range] if price_range else None,
        })

    avg_cv = sum(cvs) / len(cvs) if cvs else 0
    cells_above_threshold = sum(1 for cv in cvs if cv >= 0.5)

    return {
        "total_cells": len(cells),
        "cells_with_min_comps": len(cvs),
        "total_assigned": total_assigned,
        "avg_cv": round(avg_cv, 3),
        "cells_with_high_cv": cells_above_threshold,
        "cell_details": cell_details,
    }


# ── Main ────────────────────────────────────────────────────────────────────

def print_eval(label, assignments, titles, statuses, prices, conditions, skeleton, total_time, parse_errors=0):
    """Print evaluation metrics for a set of assignments."""
    print(f"\n{'='*60}")
    print(f"Evaluation: {label}")
    print(f"{'='*60}")

    coverage = sum(1 for a in assignments if a) / len(assignments) * 100
    print(f"\nCoverage: {coverage:.1f}% ({sum(1 for a in assignments if a)}/{len(assignments)})")
    if parse_errors:
        print(f"Parse errors: {parse_errors}")
    print(f"Extraction time: {total_time:.1f}s ({total_time/len(titles)*1000:.1f}ms/title)")

    # Axis coverage
    print("\nAxis coverage:")
    for ax in skeleton["axes"]:
        name = ax["name"]
        assigned = sum(1 for a in assignments if a and name in a)
        print(f"  {name}: {assigned}/{len(assignments)} ({assigned/len(assignments)*100:.0f}%)")

    # Build cells and evaluate
    cells = build_cells(titles, assignments, statuses, prices, conditions)
    metrics = evaluate_cells(cells)

    print(f"\nCell metrics:")
    print(f"  Total cells: {metrics['total_cells']}")
    print(f"  Cells with >= 3 sold comps: {metrics['cells_with_min_comps']}")
    print(f"  Avg intra-cell CV: {metrics['avg_cv']:.3f}")
    print(f"  Cells with CV >= 0.5 (high variance): {metrics['cells_with_high_cv']}")
    print(f"  Total assigned listings: {metrics['total_assigned']}")

    # Top cells by sold count
    print(f"\nTop 15 cells by sold count:")
    for c in metrics["cell_details"][:15]:
        cv_str = f"CV={c['cv']:.2f}" if c["cv"] is not None else "CV=N/A"
        range_str = f"£{c['price_range'][0]:,.0f}-£{c['price_range'][1]:,.0f}" if c["price_range"] else ""
        print(f"  [{c['sold']:>3} sold, {c['active']:>3} active] {cv_str:>10} | {range_str:>20} | {c['cell_key'][:70]}")

    return coverage, metrics


def main():
    parser = argparse.ArgumentParser(description="Top-down taxonomy experiment")
    parser.add_argument("--job-id", type=int, default=1030)
    parser.add_argument("--search-term", default="Rolex Submariner")
    parser.add_argument("--n", type=int, default=0, help="Limit titles (0 = all)")
    parser.add_argument("--skip-skeleton", action="store_true", help="Reuse saved skeleton")
    parser.add_argument("--mode", choices=["keyword", "llm", "compare"], default="keyword",
                        help="keyword=fast regex matching, llm=Qwen3 extraction, compare=both")
    parser.add_argument("--model", default="unsloth/qwen3-4b-unsloth-bnb-4bit")
    parser.add_argument("--sample-size", type=int, default=250, help="Diverse titles for skeleton")
    args = parser.parse_args()

    settings = load_settings()

    # ── Load listings from DB ───────────────────────────────────────────
    print(f"\nLoading listings for job {args.job_id} ({args.search_term})...")
    conn = get_db_connection(settings)
    cursor = conn.cursor()
    cursor.execute(
        "SELECT Id, Title, Price, ListingStatus, Condition "
        "FROM Listings WHERE ScrapeJobId = ? AND Title IS NOT NULL "
        "ORDER BY Id",
        args.job_id,
    )
    rows = cursor.fetchall()
    conn.close()

    listing_ids = [r[0] for r in rows]
    titles = [r[1] for r in rows]
    prices = [float(r[2]) if r[2] else None for r in rows]
    statuses = [r[3] for r in rows]
    conditions = [r[4] for r in rows]

    if args.n > 0:
        listing_ids = listing_ids[:args.n]
        titles = titles[:args.n]
        prices = prices[:args.n]
        statuses = statuses[:args.n]
        conditions = conditions[:args.n]

    print(f"Loaded {len(titles)} listings")
    sold_count = sum(1 for s in statuses if s and s.lower() == "sold")
    active_count = sum(1 for s in statuses if s and s.lower() == "active")
    print(f"  Sold: {sold_count}, Active: {active_count}")

    # ── Phase 1: Generate or load skeleton ──────────────────────────────
    skeleton_path = OUTPUT_DIR / f"skeleton_{args.job_id}.json"

    if args.skip_skeleton and skeleton_path.exists():
        print(f"\nLoading saved skeleton from {skeleton_path}")
        with open(skeleton_path) as f:
            skeleton = json.load(f)
        print(f"Loaded {len(skeleton['axes'])} axes")
    else:
        print(f"\nSelecting {args.sample_size} diverse titles via farthest-point sampling...")
        t0 = time.time()
        diverse_indices = sample_diverse(titles, n=args.sample_size)
        sample = [titles[i] for i in diverse_indices]
        print(f"Diverse sampling took {time.time() - t0:.1f}s")

        skeleton = generate_skeleton(args.search_term, sample, len(titles), settings)

        with open(skeleton_path, "w") as f:
            json.dump(skeleton, f, indent=2)
        print(f"Skeleton saved to {skeleton_path}")

    # ── Phase 2: Extraction ───────────────────────────────────────────
    if args.mode in ("keyword", "compare"):
        print(f"\n{'='*60}")
        print(f"Phase 2a: Keyword extraction ({len(titles)} titles)")
        print(f"{'='*60}")

        t0 = time.time()
        kw_assignments = extract_axes_keyword_batch(titles, skeleton, batch_label=args.search_term)
        kw_time = time.time() - t0

        kw_coverage, kw_metrics = print_eval(
            "Keyword Matching", kw_assignments, titles, statuses, prices, conditions, skeleton, kw_time)

        # Save keyword assignments
        kw_path = OUTPUT_DIR / f"assignments_keyword_{args.job_id}.json"
        with open(kw_path, "w") as f:
            json.dump({
                "job_id": args.job_id,
                "search_term": args.search_term,
                "method": "keyword",
                "total_titles": len(titles),
                "extraction_time_s": round(kw_time, 3),
                "assignments": [
                    {"listing_id": lid, "title": t, "extracted": a}
                    for lid, t, a in zip(listing_ids, titles, kw_assignments)
                ],
            }, f, indent=2)
        print(f"\nKeyword assignments saved to {kw_path}")

    if args.mode in ("llm", "compare"):
        print(f"\n{'='*60}")
        print(f"Phase 2b: LLM extraction ({len(titles)} titles) using {args.model}")
        print(f"{'='*60}")

        model, tokenizer = load_local_model(args.model)

        t0 = time.time()
        llm_assignments, parse_errors = extract_axes_batch(
            model, tokenizer, titles, skeleton, batch_label=args.search_term)
        llm_time = time.time() - t0

        llm_coverage, llm_metrics = print_eval(
            "LLM Extraction", llm_assignments, titles, statuses, prices, conditions,
            skeleton, llm_time, parse_errors)

        # Save LLM assignments
        llm_path = OUTPUT_DIR / f"assignments_llm_{args.job_id}.json"
        with open(llm_path, "w") as f:
            json.dump({
                "job_id": args.job_id,
                "search_term": args.search_term,
                "method": "llm",
                "model": args.model,
                "total_titles": len(titles),
                "parse_errors": parse_errors,
                "extraction_time_s": round(llm_time, 1),
                "assignments": [
                    {"listing_id": lid, "title": t, "extracted": a}
                    for lid, t, a in zip(listing_ids, titles, llm_assignments)
                ],
            }, f, indent=2)
        print(f"\nLLM assignments saved to {llm_path}")

    # ── Comparison (if both ran) ──────────────────────────────────────
    if args.mode == "compare":
        print(f"\n{'='*60}")
        print("Head-to-Head Comparison")
        print(f"{'='*60}")
        print(f"\n{'Metric':<35} {'Keyword':>12} {'LLM':>12}")
        print(f"{'-'*35} {'-'*12} {'-'*12}")
        print(f"{'Extraction time':<35} {kw_time:>11.1f}s {llm_time:>11.1f}s")
        print(f"{'Coverage %':<35} {kw_coverage:>11.1f}% {llm_coverage:>11.1f}%")
        print(f"{'Total cells':<35} {kw_metrics['total_cells']:>12} {llm_metrics['total_cells']:>12}")
        print(f"{'Cells with >= 3 sold':<35} {kw_metrics['cells_with_min_comps']:>12} {llm_metrics['cells_with_min_comps']:>12}")
        print(f"{'Avg intra-cell CV':<35} {kw_metrics['avg_cv']:>12.3f} {llm_metrics['avg_cv']:>12.3f}")
        print(f"{'High CV cells (>= 0.5)':<35} {kw_metrics['cells_with_high_cv']:>12} {llm_metrics['cells_with_high_cv']:>12}")

    # Save full results
    results_path = OUTPUT_DIR / f"results_{args.job_id}.json"
    results = {
        "job_id": args.job_id,
        "search_term": args.search_term,
        "skeleton": skeleton,
    }
    if args.mode in ("keyword", "compare"):
        results["keyword"] = {
            "coverage_pct": round(kw_coverage, 1),
            "extraction_time_s": round(kw_time, 3),
            "metrics": kw_metrics,
        }
    if args.mode in ("llm", "compare"):
        results["llm"] = {
            "coverage_pct": round(llm_coverage, 1),
            "parse_errors": parse_errors,
            "extraction_time_s": round(llm_time, 1),
            "metrics": llm_metrics,
        }
    with open(results_path, "w") as f:
        json.dump(results, f, indent=2)
    print(f"\nFull results saved to {results_path}")


if __name__ == "__main__":
    main()
