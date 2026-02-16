"""Append PS5 training pairs from evaluate-comps analysis to v9 CSV."""
import csv, subprocess

DB = r"(localdb)\MSSQLLocalDB"
DESC_LIMIT = 300

ids = [83156,272612,252411,272618,262696,143753,106511,
       143251,80084,262799,81237,262663,143423,156649,143018,142718,143087,
       138222,272601,272611,240986,291847,142836,
       143115,142736,143244,143275,291796,252452,143311,
       81207,142862,143008,143165,143253,291823,143051,
       151370,156668,80029,142859,143068,142789,262784,79963,
       143712,143098,143194,
       79955,81290,142783,151366,142954,80003,142867,143084,142920,151372,106496]

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

# PS5 pairs: (active_id, comp_id, label, confidence, reasoning)
pairs = [
    # === Listing 1: PS5 Disc Drive accessory (83156) - 9 flagged, 1 clean ===
    (83156, 143251, 0, "high", "Wrong variant (accessory vs product) - active is PS5 Disc Drive add-on accessory, comp is full PS5 Digital Slim console. Shared keywords PS5/Digital/Slim fool the classifier."),
    (83156, 80084, 0, "high", "Wrong variant (accessory vs product) - active is PS5 Disc Drive accessory, comp is full PS5 Digital Slim console."),
    (83156, 262799, 0, "high", "Wrong variant (accessory vs product) - active is PS5 Disc Drive accessory, comp is full PS5 Slim Console. 5x price difference."),
    (83156, 81237, 0, "high", "Wrong variant (accessory vs product) - active is PS5 Disc Drive accessory, comp is full PS5 Slim Digital Edition console."),
    (83156, 262663, 0, "high", "Wrong variant (accessory vs product) - active is PS5 Disc Drive accessory, comp is full PS5 Digital Edition console."),
    (83156, 143423, 0, "high", "Wrong variant (accessory vs product) - active is PS5 Disc Drive accessory, comp is full PlayStation 5 Digital Edition console."),
    (83156, 156649, 0, "high", "Wrong variant (accessory vs product) - active is PS5 Disc Drive accessory, comp is full PS5 Slim Digital Edition console."),
    (83156, 143018, 0, "high", "Wrong variant (accessory vs product) - active is PS5 Disc Drive accessory, comp is full PS5 Slim Digital Edition console."),
    (83156, 142718, 0, "high", "Wrong variant (accessory vs product) - active is PS5 Disc Drive accessory, comp is full PS5 Digital Slim Edition console."),
    (83156, 143087, 1, "high", "Same product - both PS5 Disc Drive accessories for Digital Edition Slim/Pro. Both brand new."),

    # === Listing 2: PS5 Disc FOR_PARTS HDMI/Power Fault (272612) - 1 flagged, 5 clean ===
    (272612, 138222, 0, "medium", "Price outlier - at 244.99 well above 145-200 cluster for faulty PS5 disc consoles. Generic FAULTY with no detail vs active specific HDMI/Power fault."),
    (272612, 272601, 1, "high", "Same product class - both faulty PS5 Disc 825GB consoles FOR_PARTS. Different fault types but same broken-console pricing tier."),
    (272612, 272611, 1, "high", "Same product class - both faulty PS5 Disc 825GB FOR_PARTS with HDMI faults. 0.99 similarity, near-identical."),
    (272612, 240986, 1, "high", "Same product class - both PS5 consoles FOR_PARTS with HDMI display faults."),
    (272612, 291847, 1, "high", "Same product class - both faulty PS5 Disc Edition 825GB consoles FOR_PARTS."),
    (272612, 142836, 1, "high", "Same product class - both faulty PS5 Disc Edition consoles FOR_PARTS with no display."),

    # === Listing 3: PS5 825GB Disc Console USED (252411) - 2 flagged, 5 clean ===
    (252411, 143115, 0, "high", "Price outlier - at 465.70 for used original PS5 disc, 75% above 250-313 clean comp cluster. Anomalous sale skews pricing."),
    (252411, 142736, 0, "medium", "Bundle inflation - comp includes a game (Comes With Controller And Game). Active is console only. Game adds 10-40 to price."),
    (252411, 143244, 1, "high", "Same product - both Sony PlayStation 5 825 GB White Disc Console, USED. Exact title match."),
    (252411, 143275, 1, "high", "Same product - both PS5 825GB White Disc Console USED. Controller and wires are standard inclusions."),
    (252411, 291796, 1, "high", "Same product - both Sony PlayStation 5 825 GB White Disc Console, USED."),
    (252411, 252452, 1, "high", "Same product - both Sony PlayStation 5 825 GB White Disc Console, USED."),
    (252411, 143311, 1, "high", "Same product - both Sony PlayStation 5 825 GB White Disc Console, USED."),

    # === Listing 4: PS5 Slim Digital Edition USED (272618) - 1 flagged, 6 clean ===
    (272618, 81207, 0, "high", "Condition mismatch (lower) - comp says No Pad (no controller). Active presumably includes controller. Missing DualSense worth 40-50 makes comp price artificially low."),
    (272618, 142862, 1, "high", "Same product - both PS5 Slim Digital Edition, USED. 1TB storage, white."),
    (272618, 143008, 1, "high", "Same product - both PS5 Slim Digital Edition, USED. Perfect condition with all cables."),
    (272618, 143165, 1, "high", "Same product - both PS5 Slim 1TB Digital Edition, USED."),
    (272618, 143253, 1, "high", "Same product - both PS5 Slim 1TB Digital Edition, USED."),
    (272618, 291823, 1, "high", "Same product - both PS5 Slim Digital Edition 1TB White Console, USED. Boxed."),
    (272618, 143051, 1, "high", "Same product - both PS5 Slim 1TB Digital Edition USED with controller and box."),

    # === Listing 5: PS5 Digital Console Only USED (262696) - 2 flagged, 6 clean ===
    (262696, 151370, 0, "high", "Condition mismatch (higher) - comp includes controller (With Controller Good Condition). Active says Console Only = no controller. Controller adds 40-50 value."),
    (262696, 156668, 0, "high", "Condition mismatch (lower) - comp is Grade B (cosmetic grading, more wear). At 199.99 its 60+ below clean cluster. Grade B is a different pricing tier."),
    (262696, 80029, 1, "high", "Same product - both PS5 Digital Edition 825GB, USED. Fully working."),
    (262696, 142859, 1, "high", "Same product - both PS5 Digital Edition Console 825GB, USED. Fully working."),
    (262696, 143068, 1, "high", "Same product - both PS5 Digital Edition Game Console, USED."),
    (262696, 142789, 1, "high", "Same product - both PS5 825GB White Console Digital Edition, USED."),
    (262696, 262784, 1, "high", "Same product - both PS5 Digital Edition White, USED. Excellent Condition is cosmetic not variant."),
    (262696, 79963, 1, "high", "Same product - both PS5 Digital Edition Console White, USED."),

    # === Listing 6: Brand New Unsealed PS5 Slim (143753) - 2 flagged, 1 clean ===
    (143753, 143712, 0, "high", "Condition mismatch (higher) - comp is Brand New Sealed, active is New Unsealed. Sealed commands 30-50 premium. Also comp explicitly Digital Edition while active ambiguous."),
    (143753, 143194, 0, "high", "Wrong variant + condition mismatch - comp is Disc edition (PS5 Slim Disc) while active pricing suggests Digital. Also Sealed vs Unsealed."),
    (143753, 143098, 1, "medium", "Reasonable match - both PS5 Slim Console, NEW. Comp title generic but price-consistent."),

    # === Listing 7: PS5 Digital Edition & Cables USED (106511) - 4 flagged, 9 clean ===
    (106511, 79955, 0, "high", "Bundle inflation - comp includes aftermarket Gulikit TMR controller, custom stand, and carbon fibre skin. Active is standard console with cables only."),
    (106511, 81290, 0, "high", "Wrong variant (likely disc) - comp title PS5 825GB with no Digital designation. Original PS5 without Digital typically means disc edition, more valuable."),
    (106511, 142783, 0, "high", "Wrong variant (likely disc) - comp title PS5 825GB with no Digital. Same issue - likely disc edition matched to digital listing."),
    (106511, 151366, 0, "medium", "Condition mismatch (lower) - comp says Console Only With Wires = no controller. Active includes Games Console & Cables implying controller. Missing controller reduces value 40-50."),
    (106511, 142859, 1, "high", "Same product - both PS5 Digital Edition Console 825GB, USED. Fully working."),
    (106511, 80029, 1, "high", "Same product - both PS5 Digital Edition Console 825GB, USED. Fully working."),
    (106511, 142954, 1, "high", "Same product - both PS5 Digital Console 825GB White, USED. With controller and cables."),
    (106511, 80003, 1, "high", "Same product - both PS5 Digital Edition 825GB Console, USED. Boxed with cables."),
    (106511, 142867, 1, "high", "Same product - both PS5 Digital Console USED with wireless controller and all cables."),
    (106511, 143084, 1, "high", "Same product - both PS5 Console Digital Edition USED with cables. Good condition."),
    (106511, 142920, 1, "high", "Same product - both PS5 Digital Edition Console USED. Boxed."),
    (106511, 151372, 1, "high", "Same product - both PS5 Console Digital Edition USED. All cables and controller."),
    (106511, 106496, 1, "high", "Same product - both PS5 Games Console & Cables USED. 0.98 similarity. Near-identical listing, Digital Edition confirmed in description."),
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
        writer.writerow([aid, cid, a["job"], "PlayStation 5 Console",
                         a["title"], c["title"], a["desc"], c["desc"],
                         label, conf, reason, "evaluate-comps-correction"])
        written += 1

l0 = sum(1 for _, _, l, _, _ in pairs if l == 0)
l1 = sum(1 for _, _, l, _, _ in pairs if l == 1)
print(f"\nAppended {written} PS5 pairs ({skipped} skipped)")
print(f"  Label 0 (flagged): {l0}")
print(f"  Label 1 (clean):   {l1}")
