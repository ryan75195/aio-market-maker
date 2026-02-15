"""
Audit the v6 merged dataset: apply regex spec extraction to all label=1 pairs
to find any where GPT labeled them as comparable but specs actually differ.

These are training data errors that need to be corrected before merging.

Usage:
    py -3.14 audit_v6.py
"""

import csv
import sys
import io
from collections import defaultdict
from pathlib import Path

# Import spec extraction from the mining script
# Note: mine_hard_pairs already sets up sys.stdout with UTF-8 encoding
from mine_hard_pairs import extract_fingerprint, describe_diff

DATA_DIR = Path(__file__).parent
V6_CSV = DATA_DIR.parent / "v6" / "labeled_pairs_v6_merged.csv"


def main():
    print(f"Loading v6 dataset from {V6_CSV}...")

    total = 0
    label_1_total = 0
    label_0_total = 0
    label_1_both_specs = 0
    label_1_false_pos = 0
    label_0_both_specs = 0
    label_0_false_neg = 0  # label=0 but specs match (potential false negative)

    false_positives = []  # label=1 but specs differ
    false_negatives = []  # label=0 but specs match

    cat_stats = defaultdict(lambda: {"total_1": 0, "fp": 0, "total_0": 0, "fn": 0})

    with open(V6_CSV, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        for row in reader:
            total += 1
            label = int(row["label"])
            category = row["product_name"]
            title_a = row["anchor_title"]
            title_b = row["neighbor_title"]

            fp_a = extract_fingerprint(title_a)
            fp_b = extract_fingerprint(title_b)

            if label == 1:
                label_1_total += 1
                cat_stats[category]["total_1"] += 1

                if fp_a.has_specs and fp_b.has_specs:
                    label_1_both_specs += 1
                    diffs = fp_a.diff(fp_b)
                    if diffs:
                        label_1_false_pos += 1
                        cat_stats[category]["fp"] += 1
                        false_positives.append({
                            "category": category,
                            "title_a": title_a[:80],
                            "title_b": title_b[:80],
                            "diffs": describe_diff(diffs),
                            "source": row.get("source", ""),
                            "confidence": row.get("confidence", ""),
                        })
            else:
                label_0_total += 1
                cat_stats[category]["total_0"] += 1

                if fp_a.has_specs and fp_b.has_specs:
                    label_0_both_specs += 1
                    diffs = fp_a.diff(fp_b)
                    if not diffs:
                        label_0_false_neg += 1
                        cat_stats[category]["fn"] += 1
                        false_negatives.append({
                            "category": category,
                            "title_a": title_a[:80],
                            "title_b": title_b[:80],
                            "source": row.get("source", ""),
                        })

            if total % 25000 == 0:
                print(f"  ...processed {total:,}")

    print(f"\n{'='*70}")
    print(f"V6 DATASET AUDIT")
    print(f"{'='*70}")
    print(f"Total pairs:              {total:,}")
    print(f"Label=1 (comparable):     {label_1_total:,}")
    print(f"Label=0 (different):      {label_0_total:,}")
    print()
    print(f"--- Label=1 pairs with specs on both sides: {label_1_both_specs:,} ---")
    print(f"Specs MATCH (correct):    {label_1_both_specs - label_1_false_pos:,}")
    print(f"Specs DIFFER (ERROR):     {label_1_false_pos:,}  <-- GPT got these wrong")
    print()
    print(f"--- Label=0 pairs with specs on both sides: {label_0_both_specs:,} ---")
    print(f"Specs DIFFER (correct):   {label_0_both_specs - label_0_false_neg:,}")
    print(f"Specs MATCH (potential):  {label_0_false_neg:,}  <-- possibly correct (other reasons)")

    if false_positives:
        print(f"\n{'='*70}")
        print(f"LABEL=1 ERRORS: GPT said comparable but specs differ ({label_1_false_pos})")
        print(f"{'='*70}")

        # Group by category
        by_cat = defaultdict(list)
        for fp in false_positives:
            by_cat[fp["category"]].append(fp)

        for cat in sorted(by_cat.keys(), key=lambda c: -len(by_cat[c])):
            items = by_cat[cat]
            print(f"\n  {cat} ({len(items)} errors):")
            for item in items[:5]:  # show up to 5 examples
                print(f"    A: {item['title_a']}")
                print(f"    B: {item['title_b']}")
                print(f"    Diff: {item['diffs']}  (src={item['source']}, conf={item['confidence']})")
                print()

    if false_negatives:
        print(f"\n{'='*70}")
        print(f"LABEL=0 WITH MATCHING SPECS: GPT said different but specs match ({label_0_false_neg})")
        print(f"{'='*70}")
        print("(These may be correct — GPT could have found other differences like bundle/parts/condition)")

        by_cat = defaultdict(list)
        for fn in false_negatives:
            by_cat[fn["category"]].append(fn)

        for cat in sorted(by_cat.keys(), key=lambda c: -len(by_cat[c])):
            items = by_cat[cat]
            print(f"\n  {cat} ({len(items)} cases):")
            for item in items[:3]:
                print(f"    A: {item['title_a']}")
                print(f"    B: {item['title_b']}")
                print(f"    (src={item['source']})")
                print()

    # Per-category summary
    print(f"\n{'='*70}")
    print(f"PER-CATEGORY SUMMARY (categories with errors)")
    print(f"{'='*70}")
    print(f"{'Category':<40} {'L=1':>5} {'FP':>5} {'FP%':>6} {'L=0':>5} {'FN':>5}")
    print(f"{'-'*40} {'-'*5} {'-'*5} {'-'*6} {'-'*5} {'-'*5}")

    for cat in sorted(cat_stats.keys(), key=lambda c: -cat_stats[c]["fp"]):
        cs = cat_stats[cat]
        if cs["fp"] == 0 and cs["fn"] == 0:
            continue
        fp_pct = 100 * cs["fp"] / cs["total_1"] if cs["total_1"] > 0 else 0
        print(f"{cat[:40]:<40} {cs['total_1']:>5} {cs['fp']:>5} {fp_pct:>5.1f}% {cs['total_0']:>5} {cs['fn']:>5}")


if __name__ == "__main__":
    main()
