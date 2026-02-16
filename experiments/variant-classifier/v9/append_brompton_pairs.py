"""Append Brompton and Canada Goose training pairs from evaluate-comps analysis to v9 CSV."""
import csv, subprocess

DB = r"(localdb)\MSSQLLocalDB"
DESC_LIMIT = 300

ids = [222258,259647,222053,222130,221826,222112,221834,222175,
       222362,221888,221843,221866,
       222337,221998,221999,221815,
       126373,125380,295732,125626,125270,125462,125284,124860,
       124910,125164,125728,125061,125320]

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

product_names = {
    222258: "Brompton Folding Bike",
    222362: "Brompton Folding Bike",
    222337: "Brompton Folding Bike",
    126373: "Canada Goose Parka",
}

# Brompton + Canada Goose pairs: (active_id, comp_id, label, confidence, reasoning)
pairs = [
    # === Brompton P Line 4-Speed Titanium (222258) ===
    (222258, 259647, 0, "high", "Price outlier — 10086 for a used P Line is absurd (retail ~3500 new). ~8x the parent price. Likely bidding error or shill bid."),
    (222258, 222053, 0, "high", "Wrong variant — Electric P Line. Electric Bromptons cost 2-3x more than non-electric. Parent is non-electric. Completely different product."),
    (222258, 222130, 1, "high", "Same model — P Line 4-speed titanium. Same condition (Used). Price within reasonable range."),
    (222258, 221826, 1, "high", "Same model — P Line 4-speed. Low handlebar vs unspecified is minor variant. Same base product."),
    (222258, 222112, 1, "high", "Same model — P Line 4-speed, mid bars. Unregistered warranty adds value but same product."),
    (222258, 221834, 0, "medium", "Bundle inflation — Description mentions extras: lightweight rear rack, purple easy roller wheels, 50T chainwheel upgrade, upgraded handlebar grips. Multiple aftermarket additions inflate price."),
    (222258, 222175, 0, "high", "Bundle inflation — Title includes with Borough Bag (retail ~150-200). Bag inflates comp price vs standalone bike."),

    # === Brompton C Line 4-Speed Electric (222362) ===
    # Note: Despite C Line title, description says Electric C Line. All comps are electric.
    (222362, 221888, 1, "high", "Same product — Electric C Line 4-speed. 2025 model year in excellent condition. Reasonable comp."),
    (222362, 221843, 1, "high", "Same product — Electric C Line 4-speed. 2023 model, used condition."),
    (222362, 221866, 1, "high", "Same product — Electric C Line 4-speed, mid handlebar. Same handlebar type as parent. Used condition."),

    # === Brompton C Line Urban Electric Black (222337) ===
    (222337, 221998, 0, "high", "Bundle inflation — Description lists extras: Advance rack (179.99), Phone Mount (40), Front reflector (20). ~240 in bundled accessories."),
    (222337, 221999, 0, "high", "Bundle inflation — Same seller, same extras: Advance rack, Phone Mount, Front reflector. ~240 in accessories."),
    (222337, 221815, 1, "high", "Same product — essentially identical listing, electric C Line. High similarity score 0.996. Same model."),

    # === Canada Goose Wyndham Parka Grey Men's Medium (126373) ===
    # Note: eBay ID 326966370392 maps to internal 126373
    (126373, 125380, 0, "high", "Price outlier + shipping inflation — 820 + 147 shipping = 967 total. Near retail price. 147 shipping extreme and suspicious. US seller, poor UK market comp."),
    (126373, 295732, 1, "high", "Same model — Wyndham, same size (Medium), used condition, authenticated. Price higher but within range."),
    (126373, 125626, 1, "high", "Same model — Wyndham, same size (Medium), used condition (7/10), authentic. Black vs Grey is minor. Price reasonable."),
    (126373, 125270, 0, "high", "Condition mismatch (higher) — Listed as NEW with tags. Parent is USED. Also Black Badge edition may be different sub-variant."),
    (126373, 125462, 0, "high", "Condition mismatch (higher) — Listed as NEW while parent is USED."),
    (126373, 125284, 0, "high", "Condition mismatch (higher) — Brand New no tags but never worn. Parent is USED."),
    (126373, 124860, 0, "high", "Condition mismatch (higher) + suspected counterfeit — NEW at 219 is suspiciously low for genuine Wyndham (retail ~1250). NFC Scannable defensiveness, emoji-heavy. Red Badge."),
    (126373, 124910, 0, "high", "Condition mismatch (higher) + suspected counterfeit — NEW at 209 far below even discounted retail. Generic AI-generated description."),
    (126373, 125164, 0, "high", "Condition mismatch (higher) + suspected counterfeit — NEW with tags at 198 for 1250 retail jacket. Almost certainly fake."),
    (126373, 125728, 0, "high", "Condition mismatch (higher) + suspected counterfeit — NEW at 157 is ~12% of retail. Almost certainly counterfeit."),
    (126373, 125061, 0, "high", "Suspected counterfeit — NULL condition at 130. Emoji-heavy with defensive Authentic claims. ~10% of retail."),
    (126373, 125320, 0, "high", "Condition mismatch (higher) + suspected counterfeit — NEW at 126 (10% of retail). Could fit S suggests sizing inconsistency common with counterfeits."),
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
print(f"\nAppended {written} Brompton/Canada Goose pairs ({skipped} skipped)")
print(f"  Label 0 (flagged): {l0}")
print(f"  Label 1 (clean):   {l1}")
