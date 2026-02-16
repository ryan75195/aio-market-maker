"""Append Rolex Submariner training pairs from evaluate-comps analysis to v9 CSV."""
import csv, subprocess

DB = r"(localdb)\MSSQLLocalDB"
DESC_LIMIT = 300

ids = [94928,157057,94397,94613,94599,94639,
       94956,94697,94726,94515,
       94982,94817,94715,94412,94714,
       95010,94598,94545,94733,94678,94469,94447,94821,94832,94728,94467,94873,
       97966,94478,94433,94508,94605,
       97972,94608,94744,94827,94588,95560,
       97984,
       98093,94438,94802,94539,94533,94824,94547,94451,94633,94816,94795,94437,94653,94763,94655,
       106859,94504,94881,94878,94379,94380,292804,
       146314,94424,94392,94669]

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

# Rolex Submariner pairs: (active_id, comp_id, label, confidence, reasoning)
pairs = [
    # === Listing: 94928 | Rolex 16610 custom blue bezel + diamond dial ===
    (94928, 157057, 0, "high", "Wrong variant — comp has diamond LUGS + sapphire bezel, significantly more aftermarket work than parent (blue bezel + diamond dial only). Different modification level."),
    (94928, 94397, 1, "high", "Same base ref 16610 with custom blue ceramic bezel and blue dial. Similar aftermarket modification level and price tier."),
    (94928, 94613, 1, "high", "Same base ref 16610 with custom blue ceramic bezel and blue dial. Same modification tier as 94397."),
    (94928, 94599, 0, "high", "Wrong variant — Malachite dial is a distinctly different and rarer aftermarket modification than a diamond dial. Different aesthetic and collector value."),
    (94928, 94639, 1, "high", "Same base ref 16610 with custom blue bezel and MOP diamond dial. Very close match to parent modification profile."),

    # === Listing: 94956 | Rolex 16610 custom blue bezel + diamond dial (2nd listing) ===
    (94956, 157057, 0, "high", "Wrong variant — Diamond LUGS + sapphire bezel is substantially higher aftermarket modification level. Price gap reflects this."),
    (94956, 94397, 1, "high", "Same base ref 16610 with custom blue ceramic bezel and blue dial. Similar modification level."),
    (94956, 94613, 1, "high", "Same base ref 16610 with custom blue ceramic bezel and blue dial. Same modification tier."),
    (94956, 94599, 0, "high", "Wrong variant — Malachite dial is a different aftermarket modification from diamond dial. Different aesthetic/collector appeal."),
    (94956, 94697, 0, "medium", "Wrong variant — No reference stated, blue dial and insert but no mention of 16610 or diamond dial. Unclear variant match."),
    (94956, 94639, 1, "high", "Same ref 16610, custom blue bezel + MOP diamond dial. Very close match to parent modification profile."),
    (94956, 94726, 0, "high", "Wrong variant — Stock factory 16610 with black dial, no aftermarket modifications. Completely different from custom blue bezel + diamond dial."),
    (94956, 94515, 0, "high", "Wrong variant — Stock factory 16610 with black dial. No aftermarket modifications. Different variant from custom blue bezel + diamond dial."),

    # === Listing: 94982 | Rolex 5513 no-date 1987 ===
    (94982, 94817, 0, "high", "Price outlier + wrong variant era — 1971 5513 with original paperwork. 1960s-70s with original docs are in a completely different price tier. Nearly 2x the parent price."),
    (94982, 94715, 1, "high", "Same ref 5513, 1968 production. Price differential is moderate and within range for the broader 5513 market."),
    (94982, 94412, 0, "medium", "Wrong variant — BLACK MATTE dial on 1982 5513 is a specific collectible sub-variant. Matte dials have different collector value."),
    (94982, 94714, 1, "high", "Same ref 5513 no-date, standard black dial. 1966. Price close to parent at 7495 vs 7995."),

    # === Listing: 95010 | Rolex 16613 Bluesy (Read Description - problem watch) ===
    (95010, 94598, 0, "high", "Condition mismatch (higher) — Full set, unpolished, RSC serviced at 9605. Parent at 5111 with Read Description warning likely has condition issues."),
    (95010, 94545, 0, "high", "Condition mismatch (higher) — Unpolished, 100% original, box and papers. Premium condition vs parent Read Description listing."),
    (95010, 94733, 0, "high", "Condition mismatch (higher) — Box and papers at 8995. Clean condition vs parent Read Description warning."),
    (95010, 94678, 0, "high", "Wrong variant — BLACK dial 16613LN, not blue dial 16613LB/Bluesy. Different dial color variant."),
    (95010, 94469, 0, "high", "Condition mismatch (higher) — Box and papers, clean condition at 8295. Parent Read Description and 5111 price suggests significant condition issues."),
    (95010, 94447, 0, "high", "Condition mismatch (higher) — With box, normal market price 16613 Bluesy. Parent at 5111 with Read Description indicates condition issues."),
    (95010, 94821, 0, "high", "Condition mismatch (higher) — Standard market price 16613 Bluesy at 8005. Parent at 5111 with Read Description indicates condition issues."),
    (95010, 94832, 0, "high", "Condition mismatch (higher) — Duplicate of 94821. Standard market price vs parent discounted Read Description price."),
    (95010, 94728, 1, "medium", "Same ref 16613, dated 1991. No box/papers mentioned, no special condition claims. Closest market comp without premium condition."),
    (95010, 94467, 0, "high", "Wrong variant — BLACK dial 16613, not blue dial Bluesy. Different variant."),
    (95010, 94873, 0, "medium", "Condition mismatch (higher) — At 6095 with box. Still 1K above parent. Parent Read Description warning suggests issue not present in this comp."),

    # === Listing: 97966 | Rolex 116610LN standard black Sub ===
    (97966, 94478, 1, "high", "Exact same reference 116610LN, same variant. Price difference could reflect condition/completeness differences."),
    (97966, 94433, 0, "high", "Condition mismatch (higher) — Listed as NEW condition vs parent USED. New vs used is significant for a Rolex."),
    (97966, 94508, 1, "high", "Same ref 116610LN, 2018 production year. Box and papers mentioned. Same variant."),
    (97966, 94605, 1, "high", "Same ref 116610LN, 2010 production year. Straightforward same-variant comp."),

    # === Listing: 97972 | Rolex 116613LN black dial bi-metal ===
    (97972, 94608, 0, "high", "Wrong variant — 116613LB is BLUE dial two-tone. Parent is 116613LN black dial. Different dial color variant. Also significant price outlier."),
    (97972, 94744, 0, "high", "Bundle inflation — Includes Extra Everest Strap (aftermarket strap worth 200-400). Same base variant 116613LN but price inflated by bundle."),
    (97972, 94827, 0, "high", "Wrong variant — 116613LB blue dial, not 116613LN black dial. Different variant."),
    (97972, 94588, 1, "high", "Exact match — 116613LN black dial two-tone. Full set explains higher price but same variant."),
    (97972, 95560, 1, "high", "Exact match — 116613LN two-tone, 2012 production. Price very close to parent. Clean comp."),

    # === Listing: 97984 | Rolex 5513 no-date 1984 ===
    (97984, 94817, 0, "high", "Price outlier + wrong variant era — 1971 5513 with original paperwork. 15500 is 2x the parent 7750. Early 70s with documentation are in a different price tier."),
    (97984, 94715, 1, "high", "Same ref 5513, 1968 production. In the broader 5513 market the price differential is reasonable."),
    (97984, 94714, 1, "high", "Same ref 5513, 1966 production. Price very close to parent at 7495 vs 7750. Good comp."),

    # === Listing: 98093 | Rolex 126610LV Starbucks 2022 ===
    (98093, 94438, 1, "high", "Same ref 126610LV Starbucks. Mk2 2023 full set. Same variant, slightly newer."),
    (98093, 94802, 0, "high", "Condition mismatch (higher) — Listed as NEW condition, unworn. Parent is USED 2022."),
    (98093, 94539, 0, "high", "Condition mismatch (higher) — NEW UNWORN condition. Parent is USED 2022."),
    (98093, 94533, 0, "high", "Condition mismatch (higher) — NEW condition, unworn with tags. Parent is USED."),
    (98093, 94824, 0, "high", "Condition mismatch (higher) — NEW condition, unworn. Parent is USED."),
    (98093, 94547, 0, "high", "Condition mismatch (higher) — Unworn MKI designation. Though listed as NEW, real flag is unworn vs used."),
    (98093, 94451, 1, "high", "Same ref 126610LV, 2023, USED condition, full set. Good match to parent variant."),
    (98093, 94633, 1, "high", "Same ref 126610LV, 2020 production, USED, box and papers. Same variant."),
    (98093, 94816, 1, "high", "Same ref 126610LV, August 2022 — same production year as parent. USED. Excellent comp."),
    (98093, 94795, 1, "high", "Same ref 126610LV, 2021, USED, box and card, mint condition. Same variant."),
    (98093, 94437, 1, "high", "Same ref 126610LV, full set, USED. Same variant."),
    (98093, 94653, 1, "high", "Same ref 126610LV, 2021, USED. Same variant."),
    (98093, 94763, 1, "high", "Same ref 126610LV, 2021 MK1, USED. Same variant."),
    (98093, 94655, 1, "high", "Same ref 126610LV, 2022, USED, boxed. Very close match to parent."),

    # === Listing: 106859 | Rolex 116610LV Hulk ===
    (106859, 94504, 0, "high", "Price outlier — Same ref 116610LV Hulk with box and papers but at 15516 nearly 2x the parent 8598. Extreme outlier."),
    (106859, 94881, 0, "high", "Condition mismatch (higher) — Listed as NEW condition. Parent is USED. New vs used is significant for discontinued Hulk."),
    (106859, 94878, 1, "high", "Same ref 116610LV Hulk, USED condition. Price is high but Hulks have wide price range. Same variant."),
    (106859, 94379, 1, "high", "Same ref 116610LV Hulk, USED. Good comp, closer in price to parent."),
    (106859, 94380, 1, "high", "Same ref 116610LV Hulk, USED. Appears to be duplicate/re-listing of 94379 from same seller."),
    (106859, 292804, 0, "high", "Penny auction — Sold for 1.16. Clearly a penny auction with minimal bidding. Not a genuine market price for a Rolex Hulk."),

    # === Listing: 146314 | Rolex 116613LB Bluesy bi-metal ===
    (146314, 94608, 1, "high", "Exact match — 116613LB blue dial two-tone. 2016 production. Same variant."),
    (146314, 94424, 1, "high", "Same ref 116613LB, 2018, box and papers. Same variant."),
    (146314, 94392, 1, "high", "Same ref 116613LB, 2018, box and papers. Same variant."),
    (146314, 94827, 1, "high", "Same ref 116613LB, 2017. Same variant."),
    (146314, 94669, 1, "high", "Same ref 116613LB, 2015, full set with recent Rolex service. Same variant."),
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
        writer.writerow([aid, cid, a["job"], "Rolex Submariner",
                         a["title"], c["title"], a["desc"], c["desc"],
                         label, conf, reason, "evaluate-comps-correction"])
        written += 1

l0 = sum(1 for _, _, l, _, _ in pairs if l == 0)
l1 = sum(1 for _, _, l, _, _ in pairs if l == 1)
print(f"\nAppended {written} Rolex Submariner pairs ({skipped} skipped)")
print(f"  Label 0 (flagged): {l0}")
print(f"  Label 1 (clean):   {l1}")
