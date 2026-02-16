"""Append electronics training pairs from evaluate-comps analysis to v9 CSV.

Categories: MacBook Pro M3, Sony A7 IV Camera, iPhone 15 Pro Max, Peloton Bike.
"""
import csv, subprocess

DB = r"(localdb)\MSSQLLocalDB"
DESC_LIMIT = 300

ids = [
    # MacBook Pro M3 (parent 88885) + comps
    88885, 88871, 74661, 75026, 75055, 74819,
    # Sony A7 IV + 28-70mm (parent 70072) + comps
    70072, 69931, 69885, 69958, 69904,
    # Sony A7R IV body (parent 88964) + comps
    88964, 70024, 69988, 70055, 70022, 70057, 70046, 69888,
    # iPhone 15 Pro Max 256GB OPENED_NEVER_USED (parent 252611) + comps
    252611, 144510, 144382, 144915, 144685, 105095, 144736, 144614, 144519, 145234, 145052, 252596,
    # iPhone 15 Pro Max 512GB USED (parent 293819) + comps
    293819, 144491, 144436, 144616, 144778, 156867, 106690, 144738, 144484, 144796, 144801,
    # Peloton Bike+ (parent 293740) + comps
    293740, 72901, 72607, 72896, 72759, 72857, 72556, 72802, 72631, 72801, 72698, 72831, 72790, 72688,
    242953, 72602, 72743, 72762, 72822, 72612, 72809, 72583, 72777, 72783, 153117, 72598,
    72616, 72567, 72914, 72587, 72596, 72800,
]

sql = f"""SELECT l.Id, l.ScrapeJobId, l.Title,
    REPLACE(REPLACE(LEFT(ISNULL(l.Description,''),{DESC_LIMIT}),CHAR(10),' '),CHAR(13),' ')
FROM Listings l WHERE l.Id IN ({','.join(str(i) for i in ids)})"""

r = subprocess.run(
    ["sqlcmd", "-S", DB, "-d", "AIOMarketMaker", "-Q", sql, "-s", "|", "-h", "-1", "-W"],
    capture_output=True, text=True, encoding="cp1252", errors="replace"
)

db = {}
for line in r.stdout.strip().split("\n"):
    line = line.strip()
    if not line or "rows affected" in line:
        continue
    parts = line.split("|")
    if len(parts) >= 4:
        try:
            lid = int(parts[0].strip())
            db[lid] = {"job": parts[1].strip(), "title": parts[2].strip(), "desc": parts[3].strip()}
        except (ValueError, IndexError):
            continue

print(f"Loaded {len(db)} listings from DB")

# Map active_id to product_name
product_names = {
    88885: "MacBook Pro M3",
    70072: "Sony A7 IV Camera",
    88964: "Sony A7 IV Camera",
    252611: "iPhone 15 Pro Max",
    293819: "iPhone 15 Pro Max",
    293740: "Peloton Bike",
}

# Electronics pairs: (active_id, comp_id, label, confidence, reasoning)
pairs = [
    # =====================================================================
    # MacBook Pro M3 Grade C (88885) — A2992 14" M3 PRO 18GB/512GB Grade C, 276 Cycles 85% Capacity
    # Clean: Grade C same spec. Flagged: Grade B and Grade A are higher condition tiers.
    # =====================================================================
    (88885, 88871, 1, "high", "Same product — both A2992 14\" M3 PRO 18GB/512GB Grade C. Comp has 201 cycles vs parent 276 cycles, both Grade C. Excellent match."),
    (88885, 74661, 0, "high", "Condition mismatch (higher) — comp is Grade B with 29 cycles and 100% battery capacity. Parent is Grade C with 276 cycles and 85% capacity. Grade B is one tier above, significantly better condition."),
    (88885, 75026, 0, "high", "Condition mismatch (higher) — comp is Grade A with only 10 cycles. Parent is Grade C with 276 cycles. Two tiers above in grading, much better condition and battery health."),
    (88885, 75055, 0, "high", "Condition mismatch (higher) — comp is Grade A with CC 133. Parent is Grade C with 276 cycles. Two tiers above in grading."),
    (88885, 74819, 0, "high", "Condition mismatch (higher) — comp is Grade B. Parent is Grade C. One tier above in grading. Different color (Midnight vs parent unspecified)."),

    # =====================================================================
    # Sony A7 IV + 28-70mm Kit (70072) — all 4 comps clean
    # All same A7 IV with 28-70mm kit lens, USED.
    # =====================================================================
    (70072, 69931, 1, "high", "Same product — both Sony A7 IV with 28-70mm zoom lens, USED. Same kit configuration."),
    (70072, 69885, 1, "high", "Same product — both Sony A7 IV with FE 28-70mm f/3.5-5.6 OSS kit lens, USED. Identical title format."),
    (70072, 69958, 1, "high", "Same product — both Sony A7 IV with FE 28-70mm f/3.5-5.6 OSS kit lens, USED. Mint condition is cosmetic, not a variant difference."),
    (70072, 69904, 1, "high", "Same product — both Sony A7 IV with FE 28-70mm f/3.5-5.6 OSS kit lens, USED. Same kit configuration."),

    # =====================================================================
    # Sony A7R IV Body Only (88964) — 5 clean body-only, 2 flagged
    # =====================================================================
    (88964, 70024, 1, "high", "Same product — both Sony A7R IV body only, USED. Excellent condition is cosmetic, not variant."),
    (88964, 69988, 1, "high", "Same product — both Sony A7R IV body only, USED. Excellent condition is cosmetic."),
    (88964, 70055, 1, "high", "Same product — both Sony A7R IV 61MP body only, USED. Identical product."),
    (88964, 70022, 1, "high", "Same product — both Sony A7R IV 61MP body only, USED. Identical product."),
    (88964, 70057, 1, "high", "Same product — both Sony A7R IV 61MP body only, USED. Identical product."),
    (88964, 70046, 0, "high", "Condition mismatch (higher) — comp says Unused Mint condition. Despite being listed as USED on eBay, the title claims unused/mint which is significantly higher condition than parent standard USED."),
    (88964, 69888, 0, "medium", "Bundle inflation — comp includes SmallRig Cage accessory. Parent is body only. Cage adds minor value (30-50) but still inflates comp price relative to body-only listings."),

    # =====================================================================
    # iPhone 15 Pro Max 256GB OPENED_NEVER_USED (252611)
    # Clean: 3 OPENED_NEVER_USED 256GB. Flagged: 8 USED condition (lower condition for comps).
    # =====================================================================
    (252611, 144510, 1, "high", "Same product — both iPhone 15 Pro Max 256GB, OPENED_NEVER_USED. Identical title and condition. Excellent match."),
    (252611, 144382, 1, "high", "Same product — both iPhone 15 Pro Max 256GB, OPENED_NEVER_USED. Identical condition and storage."),
    (252611, 144915, 1, "high", "Same product — both iPhone 15 Pro Max 256GB, OPENED_NEVER_USED (Apple prefix only difference). Same condition and storage."),
    (252611, 144685, 0, "high", "Condition mismatch (lower) — comp is USED, parent is OPENED_NEVER_USED. Lower condition tier means comp price reflects wear that parent doesn't have."),
    (252611, 105095, 0, "high", "Condition mismatch (lower) — comp is USED, parent is OPENED_NEVER_USED. Lower condition tier skews pricing downward."),
    (252611, 144736, 0, "medium", "Condition mismatch (ambiguous) — comp listed as NEW on eBay but at 516.70, well below parent OPENED_NEVER_USED at 618.70. Suspicious pricing suggests condition may not match listing."),
    (252611, 144614, 0, "high", "Condition mismatch (lower) — comp is USED 256GB White Titanium, parent is OPENED_NEVER_USED. Lower condition tier."),
    (252611, 144519, 0, "high", "Condition mismatch (lower) — comp is USED, parent is OPENED_NEVER_USED. Price at 465.11 reflects used condition discount."),
    (252611, 145234, 0, "high", "Condition mismatch (lower) — comp is USED, parent is OPENED_NEVER_USED. Price at 461.00 reflects used condition discount."),
    (252611, 145052, 0, "high", "Condition mismatch (lower) — comp is USED, parent is OPENED_NEVER_USED. Price at 435.10 well below OPENED_NEVER_USED tier."),
    (252611, 252596, 0, "high", "Condition mismatch (lower) — comp is USED at 414.70, parent is OPENED_NEVER_USED at 618.70. Significant price gap from condition difference."),

    # =====================================================================
    # iPhone 15 Pro Max 512GB USED (293819) — Blue Titanium Unlocked With Box
    # Clean: 8 USED 512GB. Flagged: 1 OPENED_NEVER_USED (higher), 1 price outlier.
    # =====================================================================
    (293819, 144491, 1, "high", "Same product — both iPhone 15 Pro Max 512GB, USED. Comp also with box, excellent condition. Same variant."),
    (293819, 144436, 1, "high", "Same product — both iPhone 15 Pro Max 512GB Blue Titanium Unlocked, USED. Boxed. Same variant."),
    (293819, 144616, 1, "high", "Same product — both iPhone 15 Pro Max 512GB Blue Titanium Unlocked, USED. Same variant."),
    (293819, 144778, 1, "high", "Same product — both iPhone 15 Pro Max 512GB Blue Titanium Unlocked, USED. Same variant."),
    (293819, 156867, 1, "high", "Same product — both iPhone 15 Pro Max 512GB, USED. Same storage and condition."),
    (293819, 106690, 1, "high", "Same product — both iPhone 15 Pro Max 512GB Blue Titanium Unlocked, USED. Same variant."),
    (293819, 144738, 1, "high", "Same product — both iPhone 15 Pro Max 512GB Blue Titanium Unlocked, USED. Boxed. Same variant."),
    (293819, 144484, 1, "high", "Same product — both iPhone 15 Pro Max 512GB Blue Titanium Unlocked, USED. Same variant."),
    (293819, 144796, 0, "high", "Condition mismatch (higher) — comp is OPENED_NEVER_USED, parent is USED. Higher condition tier commands different pricing."),
    (293819, 144801, 0, "medium", "Price outlier — comp at 465.70 is below the 512GB USED cluster (516-618). May indicate damage, missing accessories, or anomalous sale."),

    # =====================================================================
    # Peloton Bike+ (293740) — Bought In 2021
    # Clean: 13 standard Bike+ listings.
    # Flagged: bundles with shoes/weights/mats, wrong variant (regular Bike not Bike+),
    #          condition mismatches, shipping anomalies.
    # =====================================================================

    # --- 13 CLEAN Bike+ comps ---
    (293740, 72901, 1, "high", "Same product — both Peloton Bike+ Plus. 3rd Generation noted but same Bike+ product line. Standard USED."),
    (293740, 72607, 1, "high", "Same product — Peloton Plus Bike, USED. Same Bike+ variant, no extras."),
    (293740, 72896, 1, "high", "Same product — Peloton Plus Bike, USED. Same Bike+ variant."),
    (293740, 72759, 1, "high", "Same product — Peloton Bike Plus, USED. Same variant."),
    (293740, 72857, 1, "high", "Same product — Peloton Bike+, USED. Same variant."),
    (293740, 72556, 1, "high", "Same product — Peloton Bike+ Plus Exercise Bike, USED. Excellent Condition is cosmetic, not a variant difference."),
    (293740, 72802, 1, "high", "Same product — Peloton Bike+ Excellent Condition, USED. Same variant."),
    (293740, 72631, 1, "high", "Same product — Peloton Bike+ 2024, USED. Less than 25 workouts is usage detail, not variant."),
    (293740, 72801, 1, "high", "Same product — Peloton Bike+ Exercise Bike, USED. Hardly Used is cosmetic detail."),
    (293740, 72698, 1, "high", "Same product — Peloton Bike Plus Indoor Auto Resistance, USED. Auto Resistance is standard Bike+ feature."),
    (293740, 72831, 1, "high", "Same product — Peloton Bike+ Exercise Bike, USED. Same variant."),
    (293740, 72790, 1, "high", "Same product — Peloton Bike+ with Rotating Screen, USED. Rotating screen is standard Bike+ feature."),
    (293740, 72688, 1, "high", "Same product — Peloton+ Fitness Bike, USED. Same Bike+ variant despite low price."),

    # --- FLAGGED: bundles with shoes/weights/mats ---
    (293740, 72743, 0, "high", "Bundle inflation — comp includes shoes, mat, and weights. Parent is bike only. Accessories add 50-150 to price."),
    (293740, 72762, 0, "high", "Bundle inflation — comp includes Lots of Extras with Auto Resistance. Low use noted. Extras inflate price."),
    (293740, 72822, 0, "high", "Bundle inflation — comp includes tons of accessories. Parent is bike only. Accessories inflate price significantly."),
    (293740, 72612, 0, "high", "Bundle inflation — comp includes shoes and 3kg weights. Parent is bike only. Bundled accessories inflate comp price."),
    (293740, 72809, 0, "high", "Bundle inflation — comp includes shoes (sizes 11 and 7). Parent is bike only."),
    (293740, 72583, 0, "high", "Bundle inflation — comp explicitly says With Extras. Parent is bike only."),
    (293740, 72777, 0, "high", "Bundle inflation — comp includes weights, shoes, and mat with auto resistance. Parent is bike only."),
    (293740, 72783, 0, "high", "Bundle inflation — comp includes weights, shoes, and mat with auto resistance. Duplicate/relisting of 72777."),
    (293740, 153117, 0, "high", "Bundle inflation — comp includes weights and bottles. Parent is bike only. Also priced suspiciously low at 339.22."),

    # --- FLAGGED: shipping anomaly ---
    (293740, 242953, 0, "high", "Shipping anomaly — comp has 2300 shipping cost which inflates total cost far above market value. Nationwide Delivery surcharge makes this an outlier."),

    # --- FLAGGED: condition / variant mismatches ---
    (293740, 72598, 0, "high", "Condition mismatch (higher) — comp is Nearly New Unused with NULL condition. Parent is standard USED Bike+. Unused commands premium."),
    (293740, 72602, 0, "medium", "Condition mismatch — comp has NULL condition and title says with Accessories. May be regular Bike (no Plus in title) with extras, ambiguous variant."),

    # --- FLAGGED: wrong variant (regular Peloton Bike, not Bike+) ---
    (293740, 72616, 0, "high", "Wrong variant — comp is Peloton Fitness Bike (original/regular), not Bike+. Regular Bike lacks auto-resistance and rotating screen. Different product at lower price tier."),
    (293740, 72567, 0, "high", "Wrong variant — comp is Peloton Fitness Bike Original, not Bike+. Regular Bike is a different, cheaper product."),
    (293740, 72914, 0, "medium", "Wrong variant (likely) — comp title says Peloton Exercise Bike with no Plus/+ designation. At 924.70 price could be either, but title suggests regular Bike. Ambiguous."),
    (293740, 72587, 0, "high", "Wrong variant — comp is Peloton Bike (regular), not Bike+. No Plus designation. Lower price tier product at 389.20."),
    (293740, 72596, 0, "high", "Wrong variant — comp is Peloton Bike (regular), not Bike+. Duplicate/relisting of 72587. Lower price tier."),
    (293740, 72800, 0, "high", "Wrong variant — comp is Peloton Bike (regular) High-Quality Cycling Fitness. No Plus designation. Different product."),
]

written = 0
skipped = 0
with open("labeled_pairs_v9.csv", "a", newline="", encoding="utf-8") as f:
    writer = csv.writer(f)
    for aid, cid, label, conf, reason in pairs:
        a = db.get(aid)
        c = db.get(cid)
        if not a or not c:
            skipped += 1
            print(f"  SKIP: {aid} or {cid} not found")
            continue
        product_name = product_names[aid]
        writer.writerow([aid, cid, a["job"], product_name,
                         a["title"], c["title"], a["desc"], c["desc"],
                         label, conf, reason, "evaluate-comps-correction"])
        written += 1

l0 = sum(1 for _, _, l, _, _ in pairs if l == 0)
l1 = sum(1 for _, _, l, _, _ in pairs if l == 1)
print(f"\nAppended {written} electronics pairs ({skipped} skipped)")
print(f"  Label 0 (flagged): {l0}")
print(f"  Label 1 (clean):   {l1}")
print(f"  MacBook Pro M3:      {sum(1 for a, _, _, _, _ in pairs if a == 88885)}")
print(f"  Sony A7 IV (kit):    {sum(1 for a, _, _, _, _ in pairs if a == 70072)}")
print(f"  Sony A7R IV (body):  {sum(1 for a, _, _, _, _ in pairs if a == 88964)}")
print(f"  iPhone 256GB:        {sum(1 for a, _, _, _, _ in pairs if a == 252611)}")
print(f"  iPhone 512GB:        {sum(1 for a, _, _, _, _ in pairs if a == 293819)}")
print(f"  Peloton Bike+:       {sum(1 for a, _, _, _, _ in pairs if a == 293740)}")
