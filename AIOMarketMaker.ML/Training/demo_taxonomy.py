"""Demo: Run fine-tuned extraction model on real jobs to show taxonomy output.

Usage:
    python demo_taxonomy.py
    python demo_taxonomy.py --jobs 1 1020 1045
    python demo_taxonomy.py --n 30  # titles per job
"""

import argparse
import json
import time
from collections import Counter
from pathlib import Path

import pyodbc

from experiment_topdown_taxonomy import (
    OUTPUT_DIR, load_settings, sample_diverse, generate_skeleton,
    build_cells, coefficient_of_variation,
)
from score_eval_set import load_finetuned_model, extract_axes_finetuned


def get_listings(job_id, conn):
    cursor = conn.cursor()
    cursor.execute(
        "SELECT Id, Title, Price, ListingStatus, Condition "
        "FROM Listings WHERE ScrapeJobId = ? AND Title IS NOT NULL "
        "ORDER BY Id",
        job_id,
    )
    rows = cursor.fetchall()
    return {
        "ids": [r[0] for r in rows],
        "titles": [r[1] for r in rows],
        "prices": [float(r[2]) if r[2] else None for r in rows],
        "statuses": [r[3] for r in rows],
        "conditions": [r[4] for r in rows],
    }


def get_or_generate_skeleton(job_id, search_term, titles, settings):
    skeleton_path = OUTPUT_DIR / f"skeleton_{job_id}.json"
    if skeleton_path.exists():
        with open(skeleton_path) as f:
            return json.load(f)

    sample_indices = sample_diverse(titles, n=250)
    sample = [titles[i] for i in sample_indices]
    skeleton = generate_skeleton(search_term, sample, len(titles), settings)
    with open(skeleton_path, "w") as f:
        json.dump(skeleton, f, indent=2)
    return skeleton


def run_job(job_id, search_term, model, tokenizer, conn, settings, n_titles=50):
    print(f"\n{'='*70}")
    print(f"  {search_term} (job {job_id})")
    print(f"{'='*70}")

    data = get_listings(job_id, conn)
    total = len(data["titles"])
    sold = sum(1 for s in data["statuses"] if s and s.lower() == "sold")
    active = sum(1 for s in data["statuses"] if s and s.lower() == "active")
    print(f"  Total listings: {total} (sold: {sold}, active: {active})")

    # Skeleton
    skeleton = get_or_generate_skeleton(job_id, search_term, data["titles"], settings)
    print(f"\n  Axes ({len(skeleton['axes'])}):")
    for ax in skeleton["axes"]:
        vals = ax["values"]
        preview = ", ".join(vals[:8])
        if len(vals) > 8:
            preview += f", ... (+{len(vals)-8})"
        print(f"    {ax['name']}: [{preview}]")

    # Sample titles for extraction
    if total > n_titles:
        indices = sample_diverse(data["titles"], n=n_titles)
    else:
        indices = list(range(total))

    sample_titles = [data["titles"][i] for i in indices]
    sample_prices = [data["prices"][i] for i in indices]
    sample_statuses = [data["statuses"][i] for i in indices]
    sample_conditions = [data["conditions"][i] for i in indices]

    # Extract
    print(f"\n  Extracting from {len(sample_titles)} titles...")
    t0 = time.time()
    assignments = []
    all_null_count = 0
    for i, title in enumerate(sample_titles):
        result = extract_axes_finetuned(model, tokenizer, title, skeleton)
        if result is None:
            assignments.append(None)
            all_null_count += 1
        else:
            assignments.append(result)
        if (i + 1) % 10 == 0:
            elapsed = time.time() - t0
            print(f"    {i+1}/{len(sample_titles)} — {elapsed/(i+1):.1f}s/title")

    extract_time = time.time() - t0
    print(f"  Extraction done: {extract_time:.1f}s total, {extract_time/len(sample_titles):.1f}s/title")
    if all_null_count:
        print(f"  All-null (accessories/parts/unmatched): {all_null_count}")

    # Build cells
    cells = build_cells(sample_titles, assignments, sample_statuses, sample_prices, sample_conditions)

    # Coverage
    covered = sum(1 for a in assignments if a)
    print(f"\n  Coverage: {covered}/{len(sample_titles)} ({100*covered/len(sample_titles):.0f}%)")
    print(f"  Unique cells: {len(cells)}")

    # Show top cells
    sorted_cells = sorted(cells.items(), key=lambda x: len(x[1]["titles"]), reverse=True)
    print(f"\n  Top cells:")
    for cell_key, cell_data in sorted_cells[:12]:
        n = len(cell_data["titles"])
        sold_prices = cell_data.get("sold", [])
        active_prices = cell_data.get("active", [])

        price_str = ""
        if sold_prices:
            median_sold = sorted(sold_prices)[len(sold_prices)//2]
            price_str += f"sold median: {median_sold:.0f}"
        if active_prices:
            median_active = sorted(active_prices)[len(active_prices)//2]
            if price_str:
                price_str += ", "
            price_str += f"active median: {median_active:.0f}"

        print(f"    [{n:>2}] {cell_key}")
        if price_str:
            print(f"         {price_str}")

    # Show some example extractions
    print(f"\n  Example extractions:")
    shown = 0
    for title, assignment in zip(sample_titles, assignments):
        if assignment and shown < 5:
            # Truncate title for display
            t = title[:80] + "..." if len(title) > 80 else title
            vals = {k: v for k, v in assignment.items() if v is not None}
            print(f"    \"{t}\"")
            print(f"      -> {vals}")
            shown += 1

    # Show uncovered examples
    uncovered = [(t, a) for t, a in zip(sample_titles, assignments) if not a]
    if uncovered:
        print(f"\n  Uncovered examples ({len(uncovered)}):")
        for title, _ in uncovered[:3]:
            t = title[:90] + "..." if len(title) > 90 else title
            print(f"    \"{t}\"")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--jobs", nargs="+", type=int,
                        default=[1, 1020, 1045, 1046, 1029])
    parser.add_argument("--n", type=int, default=50, help="Titles per job")
    args = parser.parse_args()

    job_names = {
        1: "PlayStation 5 Console",
        1020: "iPhone 15 Pro Max",
        1029: "Pokemon Booster Box Sealed",
        1032: "Funko Pop Chase",
        1042: "Nike Air Jordan 1",
        1044: "iPad Pro",
        1045: "LEGO Star Wars Set",
        1046: "Omega Seamaster Watch",
        1054: "Ray-Ban Wayfarer Sunglasses",
        1061: "Fender Stratocaster Guitar",
        1065: "Birkenstock Arizona Sandals",
        1080: "Titleist Pro V1 Golf Balls",
        1106: "Ninja Foodi Air Fryer",
        1129: "Moissanite Engagement Ring",
    }

    settings = load_settings()
    conn = pyodbc.connect(
        "DRIVER={ODBC Driver 17 for SQL Server};"
        "SERVER=(localdb)\\MSSQLLocalDB;"
        "DATABASE=AIOMarketMaker;"
        "Trusted_Connection=yes;"
    )

    # Look up search terms for any jobs not in our lookup
    cursor = conn.cursor()
    for jid in args.jobs:
        if jid not in job_names:
            cursor.execute("SELECT SearchTerm FROM ScrapeJobs WHERE Id = ?", jid)
            row = cursor.fetchone()
            if row:
                job_names[jid] = row[0]

    print("Loading fine-tuned model...")
    model, tokenizer = load_finetuned_model()

    for job_id in args.jobs:
        search_term = job_names.get(job_id, f"Job {job_id}")
        run_job(job_id, search_term, model, tokenizer, conn, settings, n_titles=args.n)

    conn.close()
    print(f"\n{'='*70}")
    print("Demo complete.")


if __name__ == "__main__":
    main()
