"""Append luxury goods training pairs from evaluate-comps analysis to v9 CSV."""
import csv, subprocess

DB = r"(localdb)\MSSQLLocalDB"
DESC_LIMIT = 300

ids = [160327,160194,160185,160123,
       161074,160205,160153,160156,160191,
       161925,161415,161570,161466,161628,161471,161652,
       161802,161394,161442,161440,161591,
       262442,238615,238535,238541,
       128694,126915,127687,127388,
       295562,127248,127509,127062,
       154653,127035,127293,127302,126991,128054,127787,
       212733,127630,126944,127147,
       266471,127180,266459,127098,127204,127203,127029,244380,127477,126971,127314,
       173468,173291,173174,173113,173194,173182,173280]

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
    160327: "Cartier Love Bracelet",
    161074: "Cartier Love Bracelet",
    161925: "Chanel Classic Flap Bag",
    161802: "Chanel Classic Flap Bag",
    262442: "Hermes Birkin Bag",
    128694: "Omega Seamaster Watch",
    295562: "Omega Seamaster Watch",
    154653: "Omega Seamaster Watch",
    212733: "Omega Seamaster Watch",
    266471: "Omega Seamaster Watch",
    173468: "Roland TD-25 Electronic Drums",
}

# Luxury pairs: (active_id, comp_id, label, confidence, reasoning)
pairs = [
    # === Cartier Love Bracelet: 160327 | Size 19 18K White Gold ===
    (160327, 160194, 1, "high", "Same variant — 18K white gold, size 19, no diamonds. Higher price likely due to Full set (box/papers) but same product."),
    (160327, 160185, 1, "high", "Same variant — 18K white gold, size 19, no diamonds. Price premium from just polished condition but same product."),
    (160327, 160123, 1, "high", "Exact same title and price — appears to be duplicate/relisting. Similarity score ~1.0. Same product."),

    # === Cartier Love Bracelet: 161074 | White Gold Diamond ===
    (161074, 160205, 0, "high", "Wrong variant — DOUBLE Love bracelet with diamond PAVE in rose gold AND white gold. Completely different product from single white gold diamond Love. Also size 17 vs parent."),
    (161074, 160153, 0, "high", "Wrong variant — Yellow gold, not white gold. Gold color is a key differentiator for Cartier Love bracelets."),
    (161074, 160156, 0, "high", "Wrong variant — Yellow gold, not white gold. Duplicate listing of same item as 160153."),
    (161074, 160191, 1, "high", "Same variant — 18K white gold with diamonds. Size 17 vs parent unknown is minor. Closest match."),

    # === Chanel Classic Flap: 161925 | Vintage Small Black 24k Gold ===
    (161925, 161415, 1, "high", "Same variant — vintage, small, black, lambskin. Double flap is standard Classic Flap construction."),
    (161925, 161570, 1, "high", "Same variant — small, black, lambskin, gold hardware. Price premium from box/dust bag. Not vintage but same product."),
    (161925, 161466, 1, "high", "Same variant — vintage, small, black, lambskin, 24K gold hardware. Excellent match."),
    (161925, 161628, 1, "high", "Same variant — vintage, small, black, lambskin, 24K gold hardware. Excellent match."),
    (161925, 161471, 0, "high", "Wrong variant — Full Flap Clutch on Chain is a different bag model than Classic Flap. Different silhouette. Price reflects this difference."),
    (161925, 161652, 0, "high", "Price outlier — at 822.70 this is suspiciously low for any Chanel Classic Flap. 76% below parent value, well outside normal range."),

    # === Chanel Classic Flap: 161802 | Black 24kt Gold ===
    (161802, 161394, 0, "medium", "Wrong variant (possible) — comp is explicitly Medium at 4950. Parent at 2042 is more consistent with a small. Size mismatch likely."),
    (161802, 161442, 1, "high", "Same variant — very similar listing, black, 24kt gold hardware. Price difference within normal range for Chanel resale variation."),
    (161802, 161440, 1, "high", "Same variant — black, 24kt gold hardware. Single flap is older version of Classic Flap. Price in reasonable range."),
    (161802, 161591, 1, "high", "Same variant — black, 24kt gold hardware. Single flap, same seller pattern."),

    # === Hermes Shoulder Birkin: 262442 | 42 Taurillon Clemence Orange ===
    (262442, 238615, 0, "high", "Wrong variant — standard Birkin not Shoulder Birkin. Different model with different proportions. Title has encoding issues. Price at 11921 consistent with standard Birkin 30/35."),
    (262442, 238535, 1, "high", "Same variant — Shoulder Birkin, Taurillon Clemence, orange/Potiron. GHW vs parent SHW is minor. Close comp."),
    (262442, 238541, 1, "high", "Same variant — identical to 238535 (likely relisting). Shoulder Birkin, Clemence, orange. GHW vs SHW minor."),

    # === Omega Seamaster: 128694 | Ladies 300m 596.152 Quartz ===
    (128694, 126915, 1, "high", "Same variant family — Ladies, 300M, Quartz. Reference 596.1505 closely related to 596.152, minor reference variation for dial color."),
    (128694, 127687, 1, "high", "Same variant — Ladies, 300M Professional, Quartz. Reference 2224.80 is newer reference for same ladies 300M quartz line."),
    (128694, 127388, 1, "high", "Same variant — Women's/Ladies, 300M, Quartz, 28mm. Matches on all key attributes."),

    # === Omega Seamaster: 295562 | Professional 300M Automatic 41mm 212.30.41.20.01.002 ===
    (295562, 127248, 1, "high", "Same exact reference number 212.30.41.20.01.002. 41mm, same model. Immaculate condition explains higher price. Excellent comp."),
    (295562, 127509, 1, "high", "Same exact reference, same Fullset description. Near-identical listing. Excellent comp."),
    (295562, 127062, 1, "high", "Same exact reference number, automatic, 41mm. MICRO CLASP is a bracelet adjustment feature, not a different variant."),

    # === Omega Seamaster: 154653 | Vintage 1978 Quartz Cal.1342 ===
    (154653, 127035, 0, "high", "Wrong variant — Seamaster Professional 200m dive watch with rotating bezel. Parent is standard Seamaster dress watch (non-Professional). Different caliber 1538 vs 1342."),
    (154653, 127293, 0, "high", "Wrong variant — Seamaster 120m, different depth rating model. Reference 2511.80 and Cal.1438 are different from parent Cal.1342/Ref.2065."),
    (154653, 127302, 0, "high", "Wrong variant — Seamaster 120m, reference 2511.20. Different model from parent non-rated Seamaster."),
    (154653, 126991, 0, "high", "Wrong variant — Seamaster Professional 200m Pre Bond is a specific dive watch. Parent is standard Seamaster dress watch with no Professional designation."),
    (154653, 128054, 0, "high", "Wrong variant — Same issue. Seamaster Professional 200m Pre Bond is different model from parent standard Seamaster Cal.1342."),
    (154653, 127787, 1, "high", "Same variant — vintage Seamaster non-Professional, Quartz, Cal.1342. Same caliber, same era (1978 vs 1979). Closest match."),

    # === Omega Seamaster: 212733 | Ladies 300M 2284.50 Quartz ===
    (212733, 127630, 0, "high", "Wrong variant — Men's watch reference 2541.80. Parent is Ladies watch reference 2284.50. Fundamentally different sizes and products."),
    (212733, 126944, 0, "high", "Wrong variant — Reference 2541.80 is Men's version. Parent is Ladies 2284.50. Different product."),
    (212733, 127147, 1, "high", "Same variant — Ladies, Seamaster 300, Quartz. Reference 2582.20 is ladies model in same line. White dial vs parent is minor cosmetic."),

    # === Omega Seamaster: 266471 | Professional 200m Men's 36mm Pre Bond ===
    (266471, 127180, 1, "high", "Same variant — Seamaster Professional 200m Pre-Bond. Price premium from included box. Valid comp."),
    (266471, 266459, 1, "high", "Same variant — Seamaster Professional 200m Pre-Bond. Black Swiss Dial is specific dial variation but same model."),
    (266471, 127098, 1, "high", "Same model — 200m Professional, 36mm, Pre Bond. Gold refers to gold-tone variant, minor cosmetic but same model family."),
    (266471, 127204, 1, "high", "Same variant — exact match 200m, Men's, 36mm, Pre Bond. Near Mint condition noted. Excellent comp."),
    (266471, 127203, 1, "high", "Same variant — exact match 200m, Men's, 36mm, Pre Bond."),
    (266471, 127029, 1, "high", "Same variant — exact match 200m, Men's, 36mm, Pre Bond."),
    (266471, 244380, 1, "high", "Same variant — near-identical title to parent. 200m, Men's, 36mm, Pre Bond. Excellent comp."),
    (266471, 127477, 1, "high", "Same variant — 200m, Men's, 36mm, Pre Bond."),
    (266471, 126971, 1, "high", "Same variant — 200m, Men's, Pre Bond. No 36mm in title but same model."),
    (266471, 127314, 1, "high", "Same variant — exact match 200m, Men's, 36mm, Pre Bond."),

    # === Roland TD-25KV: 173468 | electronic drum kit ===
    (173468, 173291, 0, "high", "Bundle inflation — Includes sticks and headphones as bundled accessories. Parent is just the drum kit."),
    (173468, 173174, 0, "high", "Bundle inflation — Same seller/listing pattern. Includes sticks and headphones bundled. Inflates price vs kit-only."),
    (173468, 173113, 0, "high", "Bundle inflation — Same pattern. Includes sticks and headphones."),
    (173468, 173194, 0, "high", "Bundle inflation — Includes sticks and headphones."),
    (173468, 173182, 0, "high", "Bundle inflation — Includes sticks, headphones, and manual."),
    (173468, 173280, 0, "high", "Bundle inflation — Significant bundle: includes headphones, hi hat stand, kick pedal, AND throne (drum seat). Accessories add 100-200+ to price."),
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
print(f"\nAppended {written} luxury pairs ({skipped} skipped)")
print(f"  Label 0 (flagged): {l0}")
print(f"  Label 1 (clean):   {l1}")
