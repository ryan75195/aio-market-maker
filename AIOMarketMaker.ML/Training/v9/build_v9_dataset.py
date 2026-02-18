"""
Build unified v9 training dataset by combining:
  1. v8 base pairs (143K)
  2. v9 evaluate-comps corrections (486)
  3. GPT audit misclassification corrections (1,731)

Pulls FRESH descriptions from the database for all listings,
replacing any truncated descriptions from earlier CSV exports.

Usage:
    py -3.12 -u build_v9_dataset.py
    py -3.12 -u build_v9_dataset.py --dry-run
    py -3.12 -u build_v9_dataset.py --desc-limit 2000
"""

import argparse
import csv
import sys
from collections import Counter
from pathlib import Path

import pyodbc

csv.field_size_limit(10_000_000)

DATA_DIR = Path(__file__).parent.parent / "data"
V8_CSV = DATA_DIR / "labeled_pairs_v8.csv"
V9_CSV = DATA_DIR / "labeled_pairs_v9.csv"
AUDIT_CSV = DATA_DIR / "evaluator_audit_gpt.csv"
OUTPUT_CSV = DATA_DIR / "labeled_pairs_v9_merged.csv"

CONN_STR = (
    r"Driver={ODBC Driver 17 for SQL Server};"
    r"Server=(localdb)\MSSQLLocalDB;"
    r"Database=AIOMarketMaker;"
    r"Trusted_Connection=yes;"
)

COLUMNS = [
    "anchor_id", "neighbor_id", "job_id", "product_name",
    "anchor_title", "neighbor_title", "anchor_desc", "neighbor_desc",
    "label", "confidence", "reasoning", "source",
]


def parse_args():
    parser = argparse.ArgumentParser(description="Build v9 merged dataset")
    parser.add_argument("--dry-run", action="store_true",
                        help="Show counts without writing output")
    parser.add_argument("--desc-limit", type=int, default=2000,
                        help="Max description chars to store (default: 2000)")
    parser.add_argument("--oversample", type=int, default=1,
                        help="Oversample correction pairs N times (default: 1)")
    return parser.parse_args()


def load_listing_lookup(desc_limit):
    """Load all listings from DB into lookup dicts keyed by Id and ListingId."""
    print("Connecting to database...", flush=True)
    conn = pyodbc.connect(CONN_STR)
    cursor = conn.cursor()

    cursor.execute(
        "SELECT Id, ListingId, "
        "CAST(Title AS VARCHAR(500)), "
        "CAST(LEFT(Description, ?) AS VARCHAR(MAX)) "
        "FROM Listings",
        desc_limit,
    )

    by_id = {}
    by_listing_id = {}
    count = 0
    for row in cursor:
        db_id, listing_id, title, desc = row
        entry = {
            "db_id": db_id,
            "listing_id": listing_id,
            "title": title or "",
            "desc": desc or "",
        }
        by_id[db_id] = entry
        by_listing_id[str(listing_id)] = entry
        count += 1
        if count % 50000 == 0:
            print(f"  Loaded {count} listings...", flush=True)

    conn.close()
    print(f"  Loaded {count} listings total "
          f"({sum(1 for e in by_id.values() if e['desc'])} with descriptions)",
          flush=True)
    return by_id, by_listing_id


def load_v8(by_listing_id):
    """Load v8 pairs, refresh descriptions from DB lookup by eBay ListingId."""
    with open(V8_CSV, newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))

    refreshed = 0
    missing = 0
    output = []
    for r in rows:
        anchor = by_listing_id.get(r["anchor_id"])
        neighbor = by_listing_id.get(r["neighbor_id"])

        if anchor:
            r["anchor_title"] = anchor["title"]
            r["anchor_desc"] = anchor["desc"]
            refreshed += 1
        else:
            missing += 1

        if neighbor:
            r["neighbor_title"] = neighbor["title"]
            r["neighbor_desc"] = neighbor["desc"]
            refreshed += 1
        else:
            missing += 1

        output.append({col: r.get(col, "") for col in COLUMNS})

    print(f"  v8: {len(output)} pairs, {refreshed} descriptions refreshed, "
          f"{missing} listings not found in DB", flush=True)
    return output


def load_v9_corrections(by_id):
    """Load v9 evaluate-comps corrections, refresh descriptions by internal Id."""
    with open(V9_CSV, newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))

    refreshed = 0
    missing = 0
    output = []
    for r in rows:
        anchor = by_id.get(int(r["anchor_id"]))
        neighbor = by_id.get(int(r["neighbor_id"]))

        if anchor:
            r["anchor_id"] = anchor["listing_id"]
            r["anchor_title"] = anchor["title"]
            r["anchor_desc"] = anchor["desc"]
            refreshed += 1
        else:
            missing += 1

        if neighbor:
            r["neighbor_id"] = neighbor["listing_id"]
            r["neighbor_title"] = neighbor["title"]
            r["neighbor_desc"] = neighbor["desc"]
            refreshed += 1
        else:
            missing += 1

        output.append({col: r.get(col, "") for col in COLUMNS})

    print(f"  v9 corrections: {len(output)} pairs, {refreshed} refreshed, "
          f"{missing} not found", flush=True)
    return output


def load_gpt_audit_corrections(by_id):
    """Load GPT audit misclassifications, convert to v8 format with corrected labels."""
    with open(AUDIT_CSV, newline="", encoding="utf-8") as f:
        rows = [r for r in csv.DictReader(f)
                if r["verdict"] == "misclassification"]

    refreshed = 0
    missing = 0
    skipped = 0
    output = []
    for r in rows:
        anchor = by_id.get(int(r["listing_id_a"]))
        neighbor = by_id.get(int(r["listing_id_b"]))

        if not anchor or not neighbor:
            skipped += 1
            missing += (0 if anchor else 1) + (0 if neighbor else 1)
            continue

        output.append({
            "anchor_id": anchor["listing_id"],
            "neighbor_id": neighbor["listing_id"],
            "job_id": "",
            "product_name": r.get("search_term", ""),
            "anchor_title": anchor["title"],
            "neighbor_title": neighbor["title"],
            "anchor_desc": anchor["desc"],
            "neighbor_desc": neighbor["desc"],
            "label": r["correct_label"],
            "confidence": "high",
            "reasoning": f'{r["error_type"]}: {r["reasoning"][:300]}',
            "source": "gpt-audit-correction",
        })
        refreshed += 2

    print(f"  GPT audit: {len(output)} correction pairs, {refreshed} refreshed, "
          f"{skipped} skipped (listings not in DB)", flush=True)
    return output


def deduplicate(rows):
    """Remove duplicate pairs (same anchor_id + neighbor_id)."""
    seen = set()
    unique = []
    dupes = 0
    for r in rows:
        key = (r["anchor_id"], r["neighbor_id"])
        rev_key = (r["neighbor_id"], r["anchor_id"])
        if key in seen or rev_key in seen:
            dupes += 1
            continue
        seen.add(key)
        unique.append(r)
    print(f"  Deduplication: removed {dupes} duplicates", flush=True)
    return unique


def main():
    args = parse_args()

    print("=== Building v9 merged dataset ===\n", flush=True)
    print(f"Description limit: {args.desc_limit} chars", flush=True)
    print(f"Oversample corrections: {args.oversample}x\n", flush=True)

    by_id, by_listing_id = load_listing_lookup(args.desc_limit)

    print("\nLoading datasets:", flush=True)
    v8_rows = load_v8(by_listing_id)
    v9_rows = load_v9_corrections(by_id)
    gpt_rows = load_gpt_audit_corrections(by_id)

    # Oversample correction pairs
    correction_rows = v9_rows + gpt_rows
    if args.oversample > 1:
        correction_rows = correction_rows * args.oversample
        print(f"  Oversampled corrections: {len(correction_rows)} "
              f"({len(v9_rows) + len(gpt_rows)} x {args.oversample})",
              flush=True)

    all_rows = v8_rows + correction_rows
    print(f"\n  Combined: {len(all_rows)} pairs", flush=True)

    all_rows = deduplicate(all_rows)

    # Remove pairs missing descriptions
    before = len(all_rows)
    all_rows = [r for r in all_rows
                if r["anchor_desc"] and r["neighbor_desc"]]
    dropped = before - len(all_rows)
    print(f"  Dropped {dropped} pairs missing descriptions", flush=True)

    # Stats
    labels = Counter(r["label"] for r in all_rows)
    sources = Counter(r["source"] for r in all_rows)
    has_desc = sum(1 for r in all_rows if r["anchor_desc"] and r["neighbor_desc"])

    print(f"\n=== FINAL DATASET ===", flush=True)
    print(f"Total pairs:     {len(all_rows)}", flush=True)
    print(f"Labels:          {dict(labels)}", flush=True)
    print(f"Sources:         {dict(sources)}", flush=True)
    print(f"Both have desc:  {has_desc}/{len(all_rows)} "
          f"({has_desc/len(all_rows)*100:.1f}%)", flush=True)

    # Description length stats
    all_descs = ([len(r["anchor_desc"]) for r in all_rows if r["anchor_desc"]] +
                 [len(r["neighbor_desc"]) for r in all_rows if r["neighbor_desc"]])
    if all_descs:
        import statistics
        print(f"Desc lengths:    mean={statistics.mean(all_descs):.0f}, "
              f"median={statistics.median(all_descs):.0f}, "
              f"max={max(all_descs)}", flush=True)

    if args.dry_run:
        print(f"\n[DRY RUN] Would write to {OUTPUT_CSV}", flush=True)
        return

    print(f"\nWriting to {OUTPUT_CSV}...", flush=True)
    with open(OUTPUT_CSV, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=COLUMNS)
        writer.writeheader()
        writer.writerows(all_rows)

    print(f"Done. {len(all_rows)} pairs written.", flush=True)


if __name__ == "__main__":
    main()
