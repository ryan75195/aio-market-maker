"""
Generate v9 training pairs from evaluate-comps analysis of 11 iPad Pro listings.

Pairs come from manual review of comp quality:
- Flagged comps → label=0 (false positives the model needs to learn)
- Clean comps → label=1 (confirmed true positives)

Source: evaluate-comps skill run on 2026-02-15.
"""

import csv
import subprocess
import sys

DB_SERVER = r"(localdb)\MSSQLLocalDB"
DB_NAME = "AIOMarketMaker"
DESC_LIMIT = 300

# All listing IDs we need (actives + all comps)
ALL_IDS = [
    # Active listings
    244042, 266089, 135656, 135747, 159191, 159178, 140080, 266090, 150074, 135868, 154189,
    # Comps
    135343, 135329, 136470, 136064,  # 244042 comps
    140081, 135674, 135588, 135610, 135123, 150043, 134774, 135513,  # 266089 comps
    135924, 135141, 298296,  # 135656 comps
    159165, 135338, 135079, 134973, 254527, 135253,  # 135747 comps (excl shared)
    134743, 159143, 159200,  # 159191/159178 comps
    135431, 244016, 244074, 135255, 266079,  # 140080 comps
    135077, 135382, 134951, 135399, 244032, 134815, 135207, 134808,  # 266090 comps
    134824, 135369, 135309, 135197, 134737, 154205, 135320, 150042,  # 150074 comps
    135012, 135912, 135501, 134666, 135260,  # 150074 comps cont
    135339, 134694, 254529,  # 135868 comps
    135529, 135571, 135553, 254603, 134682,  # 154189 comps
]

# Deduplicate
ALL_IDS = list(set(ALL_IDS))

# Training pairs from evaluate-comps analysis
# Format: (active_id, comp_id, label, confidence, reasoning)
PAIRS = [
    # === FLAGGED COMPS (label=0) — false positives to correct ===

    # Listing 1: iPad Pro 13" M4 512GB Cellular (244042)
    (244042, 135329, 0, "high",
     "Bundle inflation — comp includes Magic Keyboard (~£300 accessory). Active is tablet only. Same iPad Pro M4 512GB but bundled with keyboard inflates sold price."),

    # Listing 2: iPad Pro 3rd Gen 128GB Wi-Fi 11" (266089)
    (266089, 135123, 0, "high",
     "Bundle inflation — comp includes Logitech Folio Touch keyboard case (~£50-80 accessory). Active is tablet only."),

    # Listing 3: iPad Pro 12.9" 3rd Gen 256GB Cellular CRACKED SCREEN (135656)
    (135656, 135924, 0, "high",
     "Condition mismatch — active has cracked screen (damaged unit), comp is a working unit with no damage. Cracked-screen iPads sell for 40-60% less."),
    (135656, 135141, 0, "high",
     "Condition mismatch — active has cracked screen, comp is a working unit. Price difference due to damage, not variant."),
    (135656, 298296, 0, "high",
     "Bundle inflation + condition mismatch — comp includes Genuine Apple Keyboard AND has no cracked screen. Double inflation vs damaged tablet-only active."),

    # Listing 4: iPad Pro 11 3rd Gen M1 128GB Wi-Fi (135747)
    (135747, 159165, 0, "high",
     "Bundle inflation — comp includes Magic Keyboard (title: 'with Magic Keyboard'). Active is tablet only. Same M1 128GB model but keyboard adds £200-300 to sold price."),
    (135747, 135338, 0, "high",
     "Bundle inflation — comp includes Magic Keyboard. Active is tablet only."),

    # Listing 6: iPad Pro 2nd Gen 10.5" 64GB Space Grey FOR_PARTS (159178)
    (159178, 134743, 0, "high",
     "Wrong variant — comp is Cellular model ('64GB Cellular Silver'), active is Wi-Fi only ('64GB Space Grey'). Cellular units command higher price even as parts."),

    # Listing 7: iPad Pro 4th Gen 128GB Wi-Fi 11" (140080)
    (140080, 135431, 0, "high",
     "Condition mismatch (higher) — comp is graded 'Excellent' with documented 92% battery health. Active is ungraded USED. Excellent-grade units with good battery sell for 20-30% more."),

    # Listing 8: iPad Pro 12.9 5th Gen 256GB Cellular (266090)
    (266090, 135077, 0, "high",
     "Condition mismatch (higher) — comp described as 'Near Mint' condition. Active is generic USED. Near Mint commands a significant premium."),
    (266090, 135399, 0, "high",
     "Wrong variant — comp title says '256GB Space Grey' with no Cellular mention (Wi-Fi only). Active is explicitly WIFI+Cellular. Wi-Fi models sell for £50-100 less."),
    (266090, 135207, 0, "high",
     "Wrong variant — comp title explicitly says 'WIFI' only. Active is WIFI+Cellular. Wi-Fi-only models are cheaper."),
    (266090, 134808, 0, "high",
     "Condition mismatch (lower) — comp is FOR_PARTS_NOT_WORKING, active is USED (working). Non-working unit drastically drags average down."),

    # Listing 9: iPad Pro 12.9" 1st Gen 128GB Wi-Fi (150074)
    (150074, 134824, 0, "high",
     "Wrong variant — comp says '(Unlocked)' indicating Cellular model. Active is Wi-Fi only. Cellular models command higher price."),
    (150074, 135369, 0, "high",
     "Condition mismatch (higher) — comp described as 'Exceptional Condition'. Active is generic USED. Premium condition grade inflates sold price."),

    # Listing 10: iPad Pro 9.7" 1st Gen 128GB (135868)
    (135868, 135339, 0, "low",
     "Possible bundle inflation — comp title ends with 'Lot' which may indicate multi-item lot sale. At £75 vs £40-53 for singles, this is suspicious."),

    # Listing 11: iPad Pro 5th Gen 12.9" Cellular 128GB (154189)
    (154189, 135529, 0, "high",
     "Condition mismatch (higher) — comp is OPENED_NEVER_USED (essentially new/sealed). Active is USED. New units command 20-30% premium over used."),
    (154189, 254603, 0, "high",
     "Condition mismatch (higher) — comp is VERY_GOOD_REFURBISHED (professionally graded). Active is generic USED. Refurbished units with grade sell for more."),

    # === CLEAN COMPS (label=1) — confirmed true positives ===

    # Listing 1: iPad Pro 13" M4 512GB Cellular (244042)
    (244042, 135343, 1, "high",
     "Same product — both iPad Pro 13-inch M4, 512GB, Cellular + Wi-Fi. Different color (Space Grey vs Space Grey) doesn't affect comparability."),
    (244042, 136470, 1, "high",
     "Same product — both iPad Pro M4 512GB WiFi+Cellular. Space Black vs Space Grey is cosmetic only."),
    (244042, 136064, 1, "high",
     "Same product — both iPad Pro M4 512GB WiFi+Cellular. Silver vs Space Grey is cosmetic only."),

    # Listing 2: iPad Pro 3rd Gen 128GB Wi-Fi 11" (266089)
    (266089, 140081, 1, "high",
     "Same product — both iPad Pro 3rd Gen (M1) 128GB WiFi 11-inch. Space Grey color match."),
    (266089, 135674, 1, "high",
     "Same product — both iPad Pro 3rd Gen 128GB Wi-Fi 11-inch Space Grey."),
    (266089, 135588, 1, "high",
     "Same product — both iPad Pro 3rd Gen 128GB Wi-Fi 11-inch Space Grey. Free P&P is shipping detail, not product difference."),
    (266089, 135610, 1, "high",
     "Same product — both iPad Pro 3rd Gen M1 128GB Wi-Fi 11-inch Space Grey."),
    (266089, 150043, 1, "high",
     "Same product — both iPad Pro 3rd Gen 128GB Wi-Fi 11-inch Space Grey."),
    (266089, 134774, 1, "high",
     "Same product — both iPad Pro 3rd Gen 128GB Wi-Fi 11-inch Space Grey."),
    (266089, 135513, 1, "high",
     "Same product — both iPad Pro 3rd Gen 128GB Wi-Fi 11-inch Space Grey."),

    # Listing 4: iPad Pro 11 3rd Gen M1 128GB Wi-Fi (135747)
    (135747, 140081, 1, "high",
     "Same product — both iPad Pro 3rd Gen M1 128GB WiFi 11-inch. Silver vs Space Grey is cosmetic."),
    (135747, 135079, 1, "high",
     "Same product — both iPad Pro 3rd Gen 128GB 11-inch. 'Great Condition' is cosmetic assessment, not a variant difference."),
    (135747, 134973, 1, "high",
     "Same product — both iPad Pro 3rd Gen M1 128GB Wi-Fi 11-inch. '90% Battery' is condition detail, not variant."),
    (135747, 135610, 1, "high",
     "Same product — both iPad Pro 3rd Gen M1 128GB Wi-Fi 11-inch Space Grey."),
    (135747, 254527, 1, "high",
     "Same product — both iPad Pro 3rd Gen M1 128GB WiFi 11-inch. Silver vs Space Grey is cosmetic."),
    (135747, 135253, 1, "high",
     "Same product — both iPad Pro 3rd Gen M1 128GB Wi-Fi 11-inch Space Grey."),

    # Listing 5: iPad Pro 2nd Gen 10.5" 64GB FOR_PARTS (159191)
    (159191, 134743, 1, "high",
     "Same product — both iPad Pro 2nd Gen 10.5-inch 64GB, both FOR_PARTS. Cellular Silver vs Cellular Space Grey is cosmetic."),
    (159191, 159143, 1, "high",
     "Same product — both iPad Pro 2nd Gen 10.5-inch 64GB Space Grey FOR_PARTS from same seller."),
    (159191, 159200, 1, "high",
     "Same product — both iPad Pro 2nd Gen 10.5-inch 64GB Space Grey FOR_PARTS from same seller."),

    # Listing 6: iPad Pro 2nd Gen 10.5" 64GB FOR_PARTS (159178) — clean ones only
    (159178, 159143, 1, "high",
     "Same product — both iPad Pro 2nd Gen 10.5-inch 64GB Space Grey FOR_PARTS."),
    (159178, 159200, 1, "high",
     "Same product — both iPad Pro 2nd Gen 10.5-inch 64GB Space Grey FOR_PARTS."),

    # Listing 7: iPad Pro 4th Gen 128GB Wi-Fi 11" (140080)
    (140080, 244016, 1, "high",
     "Same product — both iPad Pro 4th Gen (M2) 128GB WiFi 11-inch. Space Grey vs Silver is cosmetic."),
    (140080, 244074, 1, "high",
     "Same product — both iPad Pro 4th Gen (M2) 128GB WiFi 11-inch Space Grey."),
    (140080, 135255, 1, "high",
     "Same product — both iPad Pro 4th Gen 128GB Wi-Fi 11-inch Space Grey."),
    (140080, 266079, 1, "high",
     "Same product — both iPad Pro 4th Gen 128GB Wi-Fi 11-inch Space Grey."),

    # Listing 8: iPad Pro 12.9 5th Gen 256GB Cellular (266090) — clean ones
    (266090, 135382, 1, "high",
     "Same product — both iPad Pro 5th Gen 256GB 12.9-inch. 'Unlocked' implies Cellular. Space Grey match."),
    (266090, 134951, 1, "high",
     "Same product — both iPad Pro 5th Gen M1 256GB WiFi Cellular 5G 12.9-inch."),
    (266090, 244032, 1, "high",
     "Same product — both iPad Pro 5th Gen 256GB M1 Wi-Fi + Cellular 5G 12.9-inch."),
    (266090, 134815, 1, "high",
     "Same product — both iPad Pro 5th Gen M1 256GB WiFi+Cellular 5G 12.9-inch Space Grey."),

    # Listing 9: iPad Pro 12.9" 1st Gen 128GB Wi-Fi (150074) — clean ones
    (150074, 135309, 1, "high",
     "Same product — both iPad Pro 1st Gen 128GB Wi-Fi 12.9-inch. Silver vs unspecified color is cosmetic. 'Good Condition' is cosmetic assessment."),
    (150074, 135197, 1, "high",
     "Same product — both iPad Pro 1st Gen 128GB Wi-Fi 12.9-inch Silver. 'Good battery health' is condition detail."),
    (150074, 134737, 1, "high",
     "Same product — both iPad Pro 1st Gen 128GB Wi-Fi 12.9-inch. '88% Battery' is condition detail, not variant."),
    (150074, 154205, 1, "high",
     "Same product — both iPad Pro 1st Gen 128GB WiFi 12.9-inch Silver. 'Fully Working' is condition claim."),
    (150074, 135320, 1, "high",
     "Same product — both iPad Pro 1st Gen 128GB 12.9-inch. 'Excellent Condition' is cosmetic assessment."),
    (150074, 150042, 1, "high",
     "Same product — both iPad Pro 1st Gen (A1584) 128GB Wi-Fi 12.9-inch."),
    (150074, 135012, 1, "high",
     "Same product — both iPad Pro 1st Gen 128GB WiFi 12.9-inch. Black vs unspecified color is cosmetic."),
    (150074, 135912, 1, "high",
     "Same product — both iPad Pro 1st Gen 128GB Wi-Fi 12.9-inch. Gold vs unspecified color is cosmetic."),
    (150074, 135501, 1, "high",
     "Same product — both iPad Pro 1st Gen 128GB Wi-Fi 12.9-inch Silver."),
    (150074, 134666, 1, "high",
     "Same product — both iPad Pro 1st Gen 12.9-inch Wi-Fi Silver. 'Works well' is condition claim."),
    (150074, 135260, 1, "high",
     "Same product — both iPad Pro 1st Gen 128GB Wi-Fi 12.9-inch Silver. 'Very Good Condition' is cosmetic."),

    # Listing 10: iPad Pro 9.7" 1st Gen 128GB (135868) — clean ones
    (135868, 134694, 1, "high",
     "Same product — both iPad Pro 9.7-inch 1st Gen 128GB Wi-Fi. Gold vs Rose Gold is cosmetic."),
    (135868, 254529, 1, "high",
     "Same product — both iPad Pro 1st Gen 128GB Wi-Fi 9.7-inch Rose Gold. 'Good Grade' matches 'Grade B/C'."),

    # Listing 11: iPad Pro 5th Gen 12.9" Cellular 128GB (154189) — clean ones
    (154189, 135571, 1, "high",
     "Same product — both iPad Pro 5th Gen 128GB Wi-Fi + 5G 12.9-inch Space Grey."),
    (154189, 135553, 1, "high",
     "Same product — both iPad Pro 5th Gen 128GB Wi-Fi + 5G 12.9-inch Space Grey. 'Great' is condition assessment."),
    (154189, 134682, 1, "high",
     "Same product — both iPad Pro 5th Gen 128GB Wi-Fi + Cellular 12.9-inch."),
]


def query_listings(ids: list[int]) -> dict[int, dict]:
    """Query listing data from LocalDB via sqlcmd."""
    id_list = ",".join(str(i) for i in ids)
    sql = f"""
    SELECT l.Id, l.ScrapeJobId, l.Title,
        REPLACE(REPLACE(LEFT(ISNULL(l.Description,''), {DESC_LIMIT}), CHAR(10), ' '), CHAR(13), ' ')
    FROM Listings l
    WHERE l.Id IN ({id_list})
    """
    result = subprocess.run(
        ["sqlcmd", "-S", DB_SERVER, "-d", DB_NAME, "-Q", sql,
         "-s", "|", "-h", "-1", "-W"],
        capture_output=True, text=True, encoding="cp1252", errors="replace"
    )

    listings = {}
    for line in result.stdout.strip().split("\n"):
        line = line.strip()
        if not line or "rows affected" in line:
            continue
        parts = line.split("|")
        if len(parts) >= 4:
            try:
                lid = int(parts[0].strip())
                job_id = parts[1].strip()
                title = parts[2].strip()
                desc = parts[3].strip() if len(parts) > 3 else ""
                listings[lid] = {
                    "job_id": job_id,
                    "title": title,
                    "desc": desc,
                }
            except (ValueError, IndexError):
                continue
    return listings


def main():
    print(f"Querying {len(ALL_IDS)} listings from database...")
    listings = query_listings(ALL_IDS)
    print(f"Retrieved {len(listings)} listings")

    missing = [i for i in set(p[0] for p in PAIRS) | set(p[1] for p in PAIRS) if i not in listings]
    if missing:
        print(f"WARNING: Missing listings: {missing}")

    output_path = "labeled_pairs_v9.csv"
    written = 0
    skipped = 0

    with open(output_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow([
            "anchor_id", "neighbor_id", "job_id", "product_name",
            "anchor_title", "neighbor_title", "anchor_desc", "neighbor_desc",
            "label", "confidence", "reasoning", "source"
        ])

        for active_id, comp_id, label, confidence, reasoning in PAIRS:
            active = listings.get(active_id)
            comp = listings.get(comp_id)
            if not active or not comp:
                skipped += 1
                continue

            writer.writerow([
                active_id, comp_id,
                active["job_id"], "iPad Pro",
                active["title"], comp["title"],
                active["desc"], comp["desc"],
                label, confidence, reasoning,
                "evaluate-comps-correction"
            ])
            written += 1

    label_0 = sum(1 for _, _, l, _, _ in PAIRS if l == 0)
    label_1 = sum(1 for _, _, l, _, _ in PAIRS if l == 1)
    print(f"\nWrote {written} pairs to {output_path} ({skipped} skipped)")
    print(f"  Label 0 (flagged/corrected): {label_0}")
    print(f"  Label 1 (confirmed clean):   {label_1}")


if __name__ == "__main__":
    main()
