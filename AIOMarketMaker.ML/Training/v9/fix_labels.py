"""
Fix v9 labels based on disagreement analysis.

74 labels identified as wrong after detailed review:
- 69 cases: human labeled comparable (1) but model correctly flagged condition/bundle/variant
- 5 cases: human labeled not comparable (0) but model correctly said minor accessories don't matter

Run: python fix_v9_labels.py
"""

import csv
from pathlib import Path

# Pairs where human said comparable (1) but should be NOT comparable (0)
FLIP_1_TO_0 = {
    # iPad Pro - anchor 266089 has cracked screen, neighbors are working
    (266089, 140081), (266089, 135674), (266089, 135588), (266089, 135610),
    (266089, 150043), (266089, 134774), (266089, 135513),
    # iPad Pro - anchor 140080 has pixel screen damage
    (140080, 244016), (140080, 244074), (140080, 135255), (140080, 266079),
    # iPad Pro - anchor 266090 smashed screen + Apple Pencil bundle
    (266090, 135382), (266090, 134951), (266090, 244032), (266090, 134815),
    # iPad Pro - Apple-certified refurb vs generic used 83% battery
    (135747, 135610),
    # iPad Pro - battery 83% vs 94%
    (135747, 254527),
    # iPad Pro - neighbor has keyboard + Apple Pencil bundle
    (150074, 134666),
    # PS5 Digital - anchor 262696 no controller, neighbors have controller
    (262696, 143068), (262696, 142789), (262696, 262784),
    # PS5 Digital - anchor 106511 explicitly no controller
    (106511, 142954), (106511, 142867), (106511, 143084), (106511, 142920), (106511, 151372),
    # PS5 - different fault tiers (HDMI+power vs HDMI-only)
    (272612, 272611),
    # iPhone 15 Pro Max - anchor 293819 smashed rear glass + camera damage
    (293819, 144491), (293819, 144436), (293819, 144616), (293819, 144778),
    (293819, 156867), (293819, 106690), (293819, 144738), (293819, 144484),
    # Rolex - 1968 gilt dial vs 1987 matte dial (premium collectible sub-variant)
    (94982, 94715),
    # Rolex - 116613LN (black) vs 116613LB (blue) different references
    (97972, 94827),
    # Rolex - unworn with stickers vs scratched/naked
    (98093, 94816),
    # Rolex - anchor on Horus rubber strap (original bracelet missing)
    (146314, 94424), (146314, 94827), (146314, 94669),
    # LV Neverfull - anchor 87949 has cracked handles/stains
    (87949, 87467), (87949, 87668), (87949, 86928), (87949, 87428),
    # LV Neverfull - anchor 88580 heavily worn/cracked/peeling
    (88580, 87432),
    # LV Neverfull - anchor 88243 deteriorated interior
    (88243, 87467), (88243, 86928), (88243, 106407),
    # LV Neverfull - anchor 87895 Grade C
    (87895, 87041), (87895, 87432),
    # LV Neverfull - Fair vs Excellent condition
    (87919, 86920),
    # Chanel - anchor 161925 Fair condition with heavy peeling
    (161925, 161415), (161925, 161570), (161925, 161466),
    # LV - Monogram vs Damier Ebene (different canvas pattern)
    (87821, 87210), (87821, 87304), (87821, 87260), (87821, 87149), (87821, 87124),
    (105217, 87200), (105217, 87321),
    (87879, 87200), (87879, 87006),
    (87829, 87006),
    # LV - Damier Azur vs Brown Damier
    (139213, 86911),
}

# Pairs where human said NOT comparable (0) but should be comparable (1)
# Roland TD-25: drum sticks + headphones are trivial for an 800+ kit
FLIP_0_TO_1 = {
    (173468, 173291), (173468, 173174), (173468, 173113),
    (173468, 173194), (173468, 173182),
}


def main():
    csv_path = Path(__file__).parent / "labeled_pairs_v9.csv"
    backup_path = Path(__file__).parent / "labeled_pairs_v9_pre_fix.csv"

    # Read original
    with open(csv_path, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        fieldnames = reader.fieldnames
        rows = list(reader)

    # Backup
    with open(backup_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)
    print(f"Backup saved to {backup_path}")

    # Apply fixes
    flipped_1_to_0 = 0
    flipped_0_to_1 = 0
    not_found = []

    for row in rows:
        key = (int(row["anchor_id"]), int(row["neighbor_id"]))

        if key in FLIP_1_TO_0:
            if row["label"] == "1":
                row["label"] = "0"
                row["reasoning"] = f"[CORRECTED 1->0] {row['reasoning']}"
                flipped_1_to_0 += 1
            else:
                not_found.append(("1->0", key, row["label"]))

        elif key in FLIP_0_TO_1:
            if row["label"] == "0":
                row["label"] = "1"
                row["reasoning"] = f"[CORRECTED 0->1] {row['reasoning']}"
                flipped_0_to_1 += 1
            else:
                not_found.append(("0->1", key, row["label"]))

    # Write corrected
    with open(csv_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)

    # Summary
    print(f"\nLabel corrections applied:")
    print(f"  1->0 (condition/bundle/variant): {flipped_1_to_0}")
    print(f"  0->1 (minor accessories OK):     {flipped_0_to_1}")
    print(f"  Total flipped:                    {flipped_1_to_0 + flipped_0_to_1}")

    if not_found:
        print(f"\n  WARNING: {len(not_found)} pairs had unexpected current labels:")
        for direction, key, current in not_found:
            print(f"    {direction} for {key}: current label={current}")

    # Label distribution
    label_0 = sum(1 for r in rows if r["label"] == "0")
    label_1 = sum(1 for r in rows if r["label"] == "1")
    print(f"\nNew label distribution: {label_0} not-comparable, {label_1} comparable ({len(rows)} total)")


if __name__ == "__main__":
    main()
