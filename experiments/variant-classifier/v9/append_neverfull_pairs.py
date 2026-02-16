"""Append Louis Vuitton Neverfull training pairs from evaluate-comps analysis to v9 CSV."""
import csv, subprocess

DB = r"(localdb)\MSSQLLocalDB"
DESC_LIMIT = 300

ids = [264564,87173,87740,86935,87764,87392,87056,
       294485,87462,158460,87395,87775,86972,87767,87442,
       254370,
       87821,87112,87577,87203,87210,87304,87260,87250,87149,87124,87033,87502,87603,87741,87612,87001,
       88022,87311,87209,105317,87405,87406,
       88516,87678,87635,87370,87806,87143,87330,87683,
       105217,87351,87177,86918,87753,87469,87282,87200,87321,86971,87243,87647,87365,86912,
       87879,87006,87562,149061,243020,
       87985,87029,87267,87650,87668,86928,87066,87777,87428,87345,
       87919,86920,87074,241068,87067,
       87829,
       87949,87467,
       88580,87171,87432,88187,
       88243,86900,106407,87283,87077,
       139213,87818,86911,86898,87507,86986,87438,87693,87722,87782,
       88092,87652,86914,87707,86926,87313,87573,
       87895,87041,86867,87197,87344,87689]

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

# Neverfull pairs: (active_id, comp_id, label, confidence, reasoning)
pairs = [
    # === Listing 264564 (eBay 177853479299): Damier Azur Monogram Neverfull GM ===
    (264564, 87173, 1, "high", "Same variant — Neverfull GM Damier Azur. Same size and pattern."),
    (264564, 87740, 1, "high", "Same variant — Authentic Genuine LV Neverfull Azur GM with box. Same size and pattern."),
    (264564, 86935, 0, "high", "Wrong variant — Monogram canvas, not Damier Azur. Same size GM but different pattern."),
    (264564, 87764, 0, "high", "Wrong variant + bundle inflation — Damier Ebene not Damier Azur. Also includes dust bag and receipt."),
    (264564, 87392, 0, "high", "Wrong variant — Damier Ebene not Damier Azur. Same size GM but different pattern."),
    (264564, 87056, 0, "high", "Wrong variant — Damier Ebene not Damier Azur. Same size GM but different pattern."),

    # === Listing 294485 (eBay 397598781271): Black Empreinte MM ===
    (294485, 87462, 1, "high", "Same variant — Neverfull MM Monogram Empreinte leather tote. Same size, material, and color."),
    (294485, 158460, 1, "high", "Same variant — Neverfull Black & Beige Monogram Empreinte. Same material and size."),
    (294485, 87395, 1, "high", "Same variant — Neverfull MM Black Monogram Empreinte. Exact match."),
    (294485, 87775, 1, "high", "Same variant — Neverfull MM Empreinte Noir. Same material and size."),
    (294485, 86972, 1, "high", "Same variant — Neverfull MM Black Empreinte Leather. Some wear noted but same product."),
    (294485, 87767, 0, "high", "Wrong variant — Brown Empreinte not Black Empreinte. Same material tier and size but different colorway."),
    (294485, 87442, 0, "high", "Bundle inflation — includes pouch. Active listing title does not mention pouch."),

    # === Listing 254370 (eBay 397590912009): Black Empreinte MM (2nd listing) ===
    (254370, 87462, 1, "high", "Same variant — Neverfull MM Monogram Empreinte leather tote. Same material and size."),
    (254370, 158460, 1, "high", "Same variant — Black & Beige Monogram Empreinte. Same material and size."),
    (254370, 87395, 1, "high", "Same variant — Neverfull MM Black Monogram Empreinte. Exact match."),
    (254370, 87775, 1, "high", "Same variant — Neverfull MM Empreinte Noir."),
    (254370, 87767, 0, "high", "Wrong variant — Brown Empreinte not Black. Different colorway within same material tier."),
    (254370, 87442, 0, "high", "Bundle inflation — includes pouch not mentioned in active listing."),

    # === Listing 87821 (eBay 127501193164): MM Brown Canvas ===
    (87821, 87112, 1, "high", "Same variant — LV Neverfull MM Brown Canvas Monogram. Exact match."),
    (87821, 87577, 1, "high", "Same variant — Excellent LV Neverfull MM Brown Canvas Monogram."),
    (87821, 87203, 1, "high", "Same variant — LV Neverfull MM Tote Bag. Same size and canvas."),
    (87821, 87210, 1, "high", "Same variant — Neverfull MM Brown Canvas. Exact match."),
    (87821, 87304, 1, "high", "Same variant — Neverfull MM Brown Canvas."),
    (87821, 87260, 1, "high", "Same variant — Authentic LV Neverfull MM Damier Ebene Canvas. Same size, same canvas tier."),
    (87821, 87250, 1, "high", "Same variant — Neverfull MM Monogram. RRP mention is informational."),
    (87821, 87149, 1, "high", "Same variant — Authentic LV Neverfull MM Damier Ebene."),
    (87821, 87124, 1, "high", "Same variant — Authentic LV Neverfull MM Damier Ebene."),
    (87821, 87033, 1, "high", "Same variant — LV Monogram Canvas Tote MM."),
    (87821, 87502, 0, "high", "Price outlier — at 1774, 2.2x the comp average and 3.6x the ask price. NULL condition suggests possible new item."),
    (87821, 87767, 0, "high", "Price outlier — at 1329, 1.6x comp average. May reflect excellent condition."),
    (87821, 87603, 0, "high", "Price outlier — at 1242, notably above average."),
    (87821, 87741, 0, "high", "Condition mismatch (higher) — listed as NEW condition. Active listing is USED."),
    (87821, 87612, 0, "medium", "Price outlier (low) — at 370, well below average. Title mentions Leather Straps specifically suggesting bag may be in poor condition."),
    (87821, 87001, 0, "high", "Price outlier (low) — at 259, less than 0.32x comp average. Likely significant condition issues or distressed sale."),

    # === Listing 88022 (eBay 198029043436): Damier Azur MM N51107 ===
    (88022, 87311, 1, "high", "Same variant — exact model number N51107, same size MM, same Damier Azur pattern."),
    (88022, 87209, 1, "high", "Same variant — exact model number N51107, MM, Damier Azur."),
    (88022, 105317, 1, "high", "Same variant — exact model number N51107, MM, Damier Azur."),
    (88022, 87405, 1, "high", "Same variant — exact model number N51107, MM, Damier Azur."),
    (88022, 87406, 1, "high", "Same variant — exact model number N51107, MM, Damier Azur."),

    # === Listing 88516 (eBay 187821670007): Damier Ebene MM ===
    (88516, 87056, 1, "high", "Same variant — LV Neverfull MM Damier Ebene."),
    (88516, 87678, 1, "high", "Same variant — LV Damier Ebene Neverfull MM with pouch in pristine condition."),
    (88516, 87635, 1, "high", "Same variant — LV Neverfull Damier Ebene MM Canvas."),
    (88516, 87210, 1, "high", "Same variant — Neverfull MM Brown Canvas."),
    (88516, 87304, 1, "high", "Same variant — Neverfull MM Brown Canvas."),
    (88516, 87370, 1, "high", "Same variant — LV Damier Ebene Neverfull MM Large Tote Cherry."),
    (88516, 87806, 1, "high", "Same variant — Authentic LV Neverfull MM Damier Ebene."),
    (88516, 87143, 1, "high", "Same variant — LV Neverfull MM Damier Ebene Authentic."),
    (88516, 87330, 1, "high", "Same variant — Neverfull Damier Ebene MM Canvas."),
    (88516, 87683, 1, "high", "Same variant — LV Neverfull MM damier ebene classic tote bag."),
    (88516, 87502, 0, "high", "Price outlier (high) — at 1774, 2.2x comp average. NULL condition suggests possible new/unused."),
    (88516, 87767, 0, "medium", "Price outlier (high) — at 1329, 1.6x comp average. May reflect excellent condition."),
    (88516, 87603, 0, "medium", "Price outlier (high) — at 1242, notably above average. Duplicate entry."),
    (88516, 87001, 0, "high", "Price outlier (low) — at 259, less than 0.32x comp average. Likely condition issues."),

    # === Listing 105217 (eBay 177593770662): Damier Ebene GM ===
    (105217, 87351, 1, "high", "Same variant — LV Neverfull GM Damier Ebene with Pouch. Same size and pattern."),
    (105217, 87177, 1, "high", "Same variant — LV Large Damier Ebene Neverfull GM."),
    (105217, 86918, 1, "high", "Same variant — Authentic LV Neverfull GM Damier Ebene."),
    (105217, 87753, 1, "high", "Same variant — LV Neverfull GM Damier Ebene Canvas."),
    (105217, 87469, 1, "high", "Same variant — LV Neverfull GM Tote."),
    (105217, 87282, 1, "high", "Same variant — LV Neverfull GM Damier Ebene."),
    (105217, 87200, 1, "high", "Same variant — LV Neverfull GM Brown Canvas Monogram."),
    (105217, 87321, 1, "high", "Same variant — LV Neverfull GM."),
    (105217, 86971, 0, "high", "Condition mismatch (higher) — Excellent and without tags suggests near-new condition. Active is standard USED."),
    (105217, 87243, 0, "high", "Condition mismatch (higher) — New without tags. Active is USED. Different price tier."),
    (105217, 87392, 0, "medium", "Wrong variant (minor) — Monogram pattern vs Damier Ebene. Same size GM, same canvas tier."),
    (105217, 87647, 0, "medium", "Condition mismatch (potential) — NULL condition, cannot confirm used."),
    (105217, 87365, 0, "medium", "Wrong variant (minor) — Monogram pattern vs Damier Ebene. Same size GM, same tier."),
    (105217, 87173, 0, "high", "Wrong variant — Damier Azur pattern vs Damier Ebene. Same size GM."),
    (105217, 86912, 0, "high", "Wrong variant — Damier Azur vs Damier Ebene. Same size GM."),

    # === Listing 87879 (eBay 277669203053): Damier Ebene GM ===
    (87879, 87177, 1, "high", "Same variant — LV Large Damier Ebene Neverfull GM."),
    (87879, 87764, 1, "high", "Same variant — Authentic Pre-Owned LV Damier Ebene Neverfull GM Brown."),
    (87879, 86918, 1, "high", "Same variant — Authentic LV Neverfull GM Damier Ebene."),
    (87879, 87753, 1, "high", "Same variant — LV Neverfull GM Damier Ebene Canvas."),
    (87879, 87469, 1, "high", "Same variant — LV Neverfull GM Tote."),
    (87879, 87282, 1, "high", "Same variant — LV Neverfull GM Damier Ebene."),
    (87879, 87200, 1, "high", "Same variant — LV Neverfull GM Brown Canvas Monogram."),
    (87879, 87006, 1, "high", "Same variant — LV Neverfull GM Brown Canvas Coated Monogram."),
    (87879, 87562, 1, "high", "Same variant — LV Neverfull Bag Tote GM Brown Damier."),
    (87879, 86971, 0, "high", "Condition mismatch (higher) — Excellent and without tags implies near-new vs active USED."),
    (87879, 87243, 0, "high", "Condition mismatch (higher) — New without tags. Active is USED."),
    (87879, 87365, 0, "medium", "Wrong variant (minor) — Monogram vs Damier. Same size GM, same tier."),
    (87879, 87647, 0, "medium", "Condition mismatch (potential) — NULL condition."),
    (87879, 149061, 0, "medium", "Wrong variant (minor) — Monogram vs Damier. Same size GM."),
    (87879, 243020, 0, "medium", "Wrong variant (minor) — Monogram vs Damier. Same size GM."),
    (87879, 86912, 0, "high", "Wrong variant — Damier Azur vs Damier Ebene. Same size GM."),

    # === Listing 87985 (eBay 188016796822): Monogram MM M40156 ===
    (87985, 87029, 1, "high", "Same variant — exact model M40156, MM, Monogram."),
    (87985, 87267, 1, "high", "Same variant — exact model M40156, MM, Monogram."),
    (87985, 87650, 1, "high", "Same variant — Neverfull MM M40156 Brown Monogram."),
    (87985, 87668, 1, "high", "Same variant — LV Monogram Neverfull MM M40156 Brown."),
    (87985, 86928, 1, "high", "Same variant — LV Monogram Neverfull MM."),
    (87985, 87066, 1, "high", "Same variant — Authentic LV Monogram Neverfull MM M40156."),
    (87985, 87777, 1, "high", "Same variant — LV Monogram Neverfull MM M40156."),
    (87985, 87428, 1, "high", "Same variant — LV Monogram Neverfull MM M40156 Brown."),
    (87985, 87345, 1, "high", "Same variant — LV Monogram Neverfull MM M40156 Brown."),

    # === Listing 87919 (eBay 206025834965): Damier Azur GM (Fair Condition) ===
    (87919, 86920, 1, "high", "Same variant — LV Neverfull GM Damier Azure with shopping bag. Same size and pattern."),
    (87919, 87173, 1, "high", "Same variant — LV Neverfull GM Damier Azur."),
    (87919, 86912, 1, "high", "Same variant — LV Neverfull GM Damier Azur."),
    (87919, 87074, 1, "medium", "Borderline — Authentic LV Neverfull GM Monogram Tote. Different pattern but included as borderline clean."),
    (87919, 241068, 0, "high", "Condition mismatch (higher) + Price outlier — Listed as NEW. Active listing states Used Fair Condition. Massive condition gap. At 2040, 2.2x comp average."),
    (87919, 86971, 0, "high", "Wrong variant + condition mismatch (higher) — Monogram pattern not Damier Azur. Also Excellent and without tags vs Fair Condition."),
    (87919, 87177, 0, "high", "Wrong variant — Damier Ebene not Damier Azur. Same size GM."),
    (87919, 87243, 0, "high", "Wrong variant + condition mismatch (higher) — Monogram not Azur. NULL condition but title says New without tags vs Fair Condition."),
    (87919, 87764, 0, "high", "Wrong variant — Damier Ebene not Damier Azur. Same size GM."),
    (87919, 86918, 0, "high", "Wrong variant — Damier Ebene not Damier Azur. Same size GM."),
    (87919, 87753, 0, "high", "Wrong variant — Damier Ebene not Damier Azur. Same size GM."),
    (87919, 87067, 0, "high", "Wrong variant — Monogram not Damier Azur. Same size GM."),
    (87919, 87282, 0, "high", "Wrong variant — Damier Ebene not Damier Azur. Same size GM."),
    (87919, 87562, 0, "high", "Wrong variant — Damier Ebene not Damier Azur. Same size GM."),

    # === Listing 87829 (eBay 389172879274): Damier Ebene GM ===
    (87829, 87351, 1, "high", "Same variant — LV Neverfull GM Damier Ebene."),
    (87829, 87177, 1, "high", "Same variant — LV Large Damier Ebene Neverfull GM."),
    (87829, 87764, 1, "high", "Same variant — Authentic LV Damier Ebene Neverfull GM."),
    (87829, 86918, 1, "high", "Same variant — Authentic LV Neverfull GM Damier Ebene."),
    (87829, 87753, 1, "high", "Same variant — LV Neverfull GM Damier Ebene Canvas."),
    (87829, 87282, 1, "high", "Same variant — LV Neverfull GM Damier Ebene."),
    (87829, 87006, 1, "high", "Same variant — LV Neverfull GM Brown Canvas."),
    (87829, 87562, 1, "high", "Same variant — LV Neverfull Bag Tote GM Brown Damier."),
    (87829, 87243, 0, "high", "Condition mismatch (higher) — New without tags. Active is USED."),
    (87829, 87647, 0, "medium", "Condition mismatch (potential) — NULL condition."),
    (87829, 243020, 0, "medium", "Wrong variant (minor) — Monogram vs Damier. Same size GM."),
    (87829, 149061, 0, "medium", "Wrong variant (minor) — Monogram vs Damier. Same size GM."),
    (87829, 87200, 0, "medium", "Wrong variant (minor) — Monogram vs Damier. Same size GM."),

    # === Listing 87949 (eBay 326929646066): Monogram MM ===
    (87949, 87467, 1, "high", "Same variant — LV Monogram Neverfull MM. Exact match."),
    (87949, 87668, 1, "high", "Same variant — LV Monogram Neverfull MM M40156 Brown."),
    (87949, 86928, 1, "high", "Same variant — LV Monogram Neverfull MM."),
    (87949, 87428, 1, "high", "Same variant — LV Monogram Neverfull MM M40156 Brown."),
    (87949, 87345, 1, "high", "Same variant — LV Monogram Neverfull MM M40156 Brown."),

    # === Listing 88580 (eBay 365464332364): Monogram PM M40155 ===
    (88580, 87171, 1, "high", "Same variant — exact model M40155, PM, Monogram."),
    (88580, 87432, 1, "high", "Same variant — LV Monogram Neverfull PM M40155 Brown."),
    (88580, 88187, 1, "high", "Same variant — LV Neverfull PM Tote Bag M40155 Monogram."),

    # === Listing 88243 (eBay 397400866451): Monogram MM ===
    (88243, 87467, 1, "high", "Same variant — LV Monogram Neverfull MM."),
    (88243, 86928, 1, "high", "Same variant — LV Monogram Neverfull MM."),
    (88243, 86900, 1, "high", "Same variant — LV Monogram Neverfull MM M40156 Brown."),
    (88243, 106407, 1, "high", "Same variant — LV Neverfull MM Monogram Brown Gold."),
    (88243, 87283, 0, "medium", "Wrong variant (minor) — Damier Ebene N51105. Active is Monogram. Same size MM."),
    (88243, 87077, 0, "medium", "Wrong variant (minor) — Damier Ebene N51105. Active is Monogram. Same size MM."),

    # === Listing 139213 (eBay 406492650077): Damier Azur MM ===
    (139213, 87818, 1, "high", "Same variant — LV Neverfull MM Damier Azur N51110."),
    (139213, 86911, 1, "high", "Same variant — LV Tote Bag Neverfull MM Brown Damier."),
    (139213, 86898, 1, "high", "Same variant — LV Damier Azur Neverfull MM."),
    (139213, 87507, 1, "medium", "Borderline — LV Damier Neverfull MM. Damier without specifying Azur or Ebene. Could be either."),
    (139213, 86986, 0, "high", "Price outlier + possible wrong variant — at 989, 1.55x comp average. NO TARIFF in title suggests import advantage. Brown Damier could be Ebene not Azur."),
    (139213, 87438, 0, "medium", "Wrong variant (potential) — Damier without specifying Azur or Ebene. Could be Ebene."),
    (139213, 87693, 0, "high", "Wrong variant — Monogram M40156. Active is Damier Azur. Different pattern."),
    (139213, 87722, 0, "high", "Wrong variant — Monogram M40156. Active is Damier Azur."),
    (139213, 87782, 0, "high", "Wrong variant — Monogram M40156. Active is Damier Azur."),

    # === Listing 88092 (eBay 389459632526): Monogram MM M40156 ===
    (88092, 87652, 1, "high", "Same variant — LV Monogram Tote Bag Neverfull MM."),
    (88092, 86914, 1, "high", "Same variant — Auth LV Neverfull MM Monogram Canvas M40156."),
    (88092, 87707, 1, "high", "Same variant — LV Tote Bag Monogram Neverfull MM M40156 Brown."),
    (88092, 86926, 1, "high", "Same variant — Auth LV Neverfull MM Monogram Canvas M40156."),
    (88092, 87507, 0, "high", "Wrong variant — Damier. Active is Monogram."),
    (88092, 87438, 0, "high", "Wrong variant — Damier. Active is Monogram."),
    (88092, 87313, 0, "high", "Wrong variant — Damier. Active is Monogram."),
    (88092, 87573, 0, "high", "Wrong variant — Damier Azur. Active is Monogram."),

    # === Listing 87895 (eBay 326945708025): Monogram PM ===
    (87895, 87171, 1, "high", "Same variant — LV Monogram Neverfull PM M40155."),
    (87895, 87041, 1, "high", "Same variant — LV Monogram Neverfull PM."),
    (87895, 87432, 1, "high", "Same variant — LV Monogram Neverfull PM M40155 Brown."),
    (87895, 88187, 1, "high", "Same variant — LV Neverfull PM Tote Bag M40155 Monogram."),
    (87895, 86867, 0, "high", "Wrong variant — Damier Ebene, not Monogram. Same size PM."),
    (87895, 87197, 0, "high", "Wrong variant — Damier N51109. Active is Monogram M40155."),
    (87895, 87344, 0, "high", "Wrong variant — Damier N51109. Active is Monogram."),
    (87895, 87689, 0, "medium", "Price outlier (low) + condition concern — at 343, 0.6x comp average. Title leads with used in lowercase, suggesting poor condition."),
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
        writer.writerow([aid, cid, a["job"], "Louis Vuitton Neverfull",
                         a["title"], c["title"], a["desc"], c["desc"],
                         label, conf, reason, "evaluate-comps-correction"])
        written += 1

l0 = sum(1 for _, _, l, _, _ in pairs if l == 0)
l1 = sum(1 for _, _, l, _, _ in pairs if l == 1)
print(f"\nAppended {written} Neverfull pairs ({skipped} skipped)")
print(f"  Label 0 (flagged): {l0}")
print(f"  Label 1 (clean):   {l1}")
