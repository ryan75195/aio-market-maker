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
        REPLACE(REPLACE(CAST(LEFT(ISNULL(a.Description,''), {DESC_LIMIT}) AS VARCHAR({DESC_LIMIT})), CHAR(10), ' '), CHAR(13), ' ') AS DescA,
        b.Title AS TitleB,
        REPLACE(REPLACE(CAST(LEFT(ISNULL(b.Description,''), {DESC_LIMIT}) AS VARCHAR({DESC_LIMIT})), CHAR(10), ' '), CHAR(13), ' ') AS DescB,
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
