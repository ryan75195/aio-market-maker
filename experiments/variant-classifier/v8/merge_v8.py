"""
Merge v7 dataset with v8 mined hard pairs to create the v8 training dataset.

Steps:
1. Load v7 merged dataset (129K pairs — includes v6 base + v7 hard pairs)
2. Apply v8 regex audit: flip label=1 pairs where new P0/P1 extractors detect spec diffs
3. Add v8 mined hard pairs (hard negatives + hard positives)
4. Deduplicate by (anchor_id, neighbor_id)
5. Output: labeled_pairs_v8.csv

Usage:
    py -3.12 merge_v8.py
"""

import csv
import sys
import io
from collections import defaultdict
from pathlib import Path

csv.field_size_limit(10 * 1024 * 1024)  # 10MB — mined pairs have large description fields

from mine_hard_pairs import extract_fingerprint

DATA_DIR = Path(__file__).parent
V7_CSV = DATA_DIR.parent / "v7" / "labeled_pairs_v7.csv"
V8_MINED_CSV = DATA_DIR / "hard_pairs_v8.csv"
OUTPUT_CSV = DATA_DIR / "labeled_pairs_v8.csv"


def main():
    # ── Step 1: Load v7 and apply v8 audit corrections ──────────────────
    print(f"Loading v7 dataset from {V7_CSV}...")

    rows = []
    corrections = 0
    seen_pairs = set()

    with open(V7_CSV, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        fieldnames = reader.fieldnames
        for row in reader:
            pair_key = (row["anchor_id"], row["neighbor_id"])
            seen_pairs.add(pair_key)
            seen_pairs.add((row["neighbor_id"], row["anchor_id"]))

            # Apply v8 audit: if label=1 but new extractors detect spec diffs, flip to 0
            if int(row["label"]) == 1:
                cat = row.get("product_name", "")
                fp_a = extract_fingerprint(row["anchor_title"], cat)
                fp_b = extract_fingerprint(row["neighbor_title"], cat)
                if fp_a.has_specs and fp_b.has_specs:
                    diffs = fp_a.diff(fp_b)
                    if diffs:
                        row["label"] = "0"
                        src = row.get("source", "")
                        if "_corrected" not in src:
                            row["source"] = src + "_v8corrected"
                        row["reasoning"] = f"V8 spec mismatch: {', '.join(f'{k}:{a}!={b}' for k, (a,b) in diffs.items())}"
                        corrections += 1

            rows.append(row)

    print(f"  Loaded {len(rows):,} v7 pairs, corrected {corrections} labels (1->0)")

    # ── Step 2: Load v8 mined pairs, skip duplicates ────────────────────
    print(f"\nLoading v8 mined pairs from {V8_MINED_CSV}...")

    v8_new = 0
    v8_dupes = 0

    with open(V8_MINED_CSV, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        for row in reader:
            pair_key = (row["anchor_id"], row["neighbor_id"])
            reverse_key = (row["neighbor_id"], row["anchor_id"])
            if pair_key in seen_pairs or reverse_key in seen_pairs:
                v8_dupes += 1
                continue
            seen_pairs.add(pair_key)
            seen_pairs.add(reverse_key)
            rows.append(row)
            v8_new += 1

    print(f"  Added {v8_new:,} new v8 pairs, skipped {v8_dupes:,} duplicates")

    # ── Step 3: Write merged output ─────────────────────────────────────
    total = len(rows)
    label_1 = sum(1 for r in rows if int(r["label"]) == 1)
    label_0 = total - label_1

    print(f"\n{'='*60}")
    print(f"V8 MERGED DATASET")
    print(f"{'='*60}")
    print(f"Total pairs:          {total:,}")
    print(f"Label=1 (comparable): {label_1:,} ({100*label_1/total:.1f}%)")
    print(f"Label=0 (different):  {label_0:,} ({100*label_0/total:.1f}%)")
    print(f"V8 corrections:       {corrections}")
    print(f"V8 new pairs:         {v8_new:,}")

    # Per-source breakdown
    source_counts = defaultdict(lambda: {"total": 0, "pos": 0, "neg": 0})
    for row in rows:
        src = row.get("source", "unknown")
        source_counts[src]["total"] += 1
        if int(row["label"]) == 1:
            source_counts[src]["pos"] += 1
        else:
            source_counts[src]["neg"] += 1

    print(f"\nPer-source breakdown:")
    print(f"{'Source':<40} {'Total':>7} {'Pos':>7} {'Neg':>7} {'Pos%':>6}")
    print(f"{'-'*40} {'-'*7} {'-'*7} {'-'*7} {'-'*6}")
    for src in sorted(source_counts.keys()):
        sc = source_counts[src]
        pct = 100 * sc["pos"] / sc["total"] if sc["total"] > 0 else 0
        print(f"{src[:40]:<40} {sc['total']:>7} {sc['pos']:>7} {sc['neg']:>7} {pct:>5.1f}%")

    print(f"\nWriting to {OUTPUT_CSV}...")
    with open(OUTPUT_CSV, "w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for row in rows:
            writer.writerow(row)
    print(f"  Done. {total:,} pairs written.")


if __name__ == "__main__":
    main()
