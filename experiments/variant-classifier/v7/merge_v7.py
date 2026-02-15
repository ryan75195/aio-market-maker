"""
Merge v6 dataset with v7 mined hard pairs to create the v7 training dataset.

Steps:
1. Load v6 merged dataset (113K pairs)
2. Apply regex audit: flip label=1 pairs where specs actually differ (GPT errors)
3. Add v7 mined hard pairs (hard negatives + hard positives)
4. Deduplicate by (anchor_id, neighbor_id)
5. Output: labeled_pairs_v7.csv

Usage:
    py -3.14 merge_v7.py
"""

import csv
import sys
import io
from collections import defaultdict
from pathlib import Path

csv.field_size_limit(10 * 1024 * 1024)  # 10MB — mined pairs have large description fields

from mine_hard_pairs import extract_fingerprint

DATA_DIR = Path(__file__).parent
V6_CSV = DATA_DIR.parent / "v6" / "labeled_pairs_v6_merged.csv"
V7_MINED_CSV = DATA_DIR / "hard_pairs_v7.csv"
OUTPUT_CSV = DATA_DIR / "labeled_pairs_v7.csv"


def main():
    # ── Step 1: Load v6 and apply audit corrections ────────────────────
    print(f"Loading v6 dataset from {V6_CSV}...")

    v6_rows = []
    corrections = 0
    seen_pairs = set()

    with open(V6_CSV, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        fieldnames = reader.fieldnames
        for row in reader:
            pair_key = (row["anchor_id"], row["neighbor_id"])
            seen_pairs.add(pair_key)
            # Also add reverse pair for dedup
            seen_pairs.add((row["neighbor_id"], row["anchor_id"]))

            # Apply audit: if label=1 but specs differ, flip to label=0
            if int(row["label"]) == 1:
                fp_a = extract_fingerprint(row["anchor_title"])
                fp_b = extract_fingerprint(row["neighbor_title"])
                if fp_a.has_specs and fp_b.has_specs:
                    diffs = fp_a.diff(fp_b)
                    if diffs:
                        row["label"] = "0"
                        row["source"] = row.get("source", "") + "_corrected"
                        row["reasoning"] = f"Spec mismatch: {', '.join(f'{k}:{a}!={b}' for k, (a,b) in diffs.items())}"
                        corrections += 1

            v6_rows.append(row)

    print(f"  Loaded {len(v6_rows):,} v6 pairs, corrected {corrections} labels (1->0)")

    # ── Step 2: Load v7 mined pairs, skip duplicates ──────────────────
    print(f"\nLoading v7 mined pairs from {V7_MINED_CSV}...")

    v7_new = 0
    v7_dupes = 0

    with open(V7_MINED_CSV, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        for row in reader:
            pair_key = (row["anchor_id"], row["neighbor_id"])
            reverse_key = (row["neighbor_id"], row["anchor_id"])
            if pair_key in seen_pairs or reverse_key in seen_pairs:
                v7_dupes += 1
                continue
            seen_pairs.add(pair_key)
            seen_pairs.add(reverse_key)
            v6_rows.append(row)
            v7_new += 1

    print(f"  Added {v7_new:,} new v7 pairs, skipped {v7_dupes:,} duplicates")

    # ── Step 3: Write merged output ───────────────────────────────────
    total = len(v6_rows)
    label_1 = sum(1 for r in v6_rows if int(r["label"]) == 1)
    label_0 = total - label_1

    print(f"\n{'='*60}")
    print(f"V7 MERGED DATASET")
    print(f"{'='*60}")
    print(f"Total pairs:          {total:,}")
    print(f"Label=1 (comparable): {label_1:,} ({100*label_1/total:.1f}%)")
    print(f"Label=0 (different):  {label_0:,} ({100*label_0/total:.1f}%)")
    print(f"V6 corrections:       {corrections}")
    print(f"V7 new pairs:         {v7_new:,}")

    # Per-source breakdown
    source_counts = defaultdict(lambda: {"total": 0, "pos": 0, "neg": 0})
    for row in v6_rows:
        src = row.get("source", "unknown")
        source_counts[src]["total"] += 1
        if int(row["label"]) == 1:
            source_counts[src]["pos"] += 1
        else:
            source_counts[src]["neg"] += 1

    print(f"\nPer-source breakdown:")
    print(f"{'Source':<35} {'Total':>7} {'Pos':>7} {'Neg':>7} {'Pos%':>6}")
    print(f"{'-'*35} {'-'*7} {'-'*7} {'-'*7} {'-'*6}")
    for src in sorted(source_counts.keys()):
        sc = source_counts[src]
        pct = 100 * sc["pos"] / sc["total"] if sc["total"] > 0 else 0
        print(f"{src[:35]:<35} {sc['total']:>7} {sc['pos']:>7} {sc['neg']:>7} {pct:>5.1f}%")

    print(f"\nWriting to {OUTPUT_CSV}...")
    with open(OUTPUT_CSV, "w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for row in v6_rows:
            writer.writerow(row)
    print(f"  Done. {total:,} pairs written.")


if __name__ == "__main__":
    main()
