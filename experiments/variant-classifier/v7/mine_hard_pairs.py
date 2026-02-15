"""
V7 Hard Pair Mining: Find false positives in existing ListingRelationships
by applying regex spec extraction to both sides of IsComparable=1 pairs.

If two listings were marked comparable but have different specs (CPU, RAM,
storage, screen size, etc.), that's a false positive — and becomes a hard
negative training example (label=0).

Also generates hard positives from IsComparable=1 pairs where specs DO match,
confirming the model's decision (label=1).

Usage:
    py -3.12 mine_hard_pairs.py                  # full run
    py -3.12 mine_hard_pairs.py --dry-run         # analyze only, don't write CSV
    py -3.12 mine_hard_pairs.py --max-per-cat 500  # cap pairs per category
"""

import argparse
import csv
import re
import sys
import io
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

import pyodbc

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

# ── Config ────────────────────────────────────────────────────────────────
DATA_DIR = Path(__file__).parent
OUTPUT_CSV = DATA_DIR / "hard_pairs_v7.csv"

DB_CONN = (
    "DRIVER={ODBC Driver 17 for SQL Server};"
    "SERVER=(localdb)\\MSSQLLocalDB;"
    "DATABASE=AIOMarketMaker;"
    "Trusted_Connection=yes;"
)

DEFAULT_MAX_HARD_NEG_PER_CAT = 500
DEFAULT_MAX_HARD_POS_PER_CAT = 200
MIN_LISTINGS_PER_GROUP = 2  # need at least 2 listings with same fingerprint for positives


# ── Spec Extraction ──────────────────────────────────────────────────────

@dataclass
class SpecFingerprint:
    """Normalized spec tokens extracted from a listing title."""
    cpu: Optional[str] = None
    ram_gb: Optional[int] = None
    storage_gb: Optional[int] = None
    screen_inches: Optional[float] = None
    wattage: Optional[int] = None
    voltage: Optional[int] = None
    capacity_mah: Optional[int] = None
    size_label: Optional[str] = None
    generation: Optional[str] = None
    quantity: Optional[str] = None
    raw_tokens: tuple = field(default_factory=tuple)

    @property
    def has_specs(self) -> bool:
        """Returns True if at least one spec was extracted."""
        return any([
            self.cpu, self.ram_gb, self.storage_gb, self.screen_inches,
            self.wattage, self.voltage, self.capacity_mah, self.size_label,
            self.generation, self.quantity
        ])

    def spec_dict(self) -> dict:
        """Returns only non-None specs as a dict for comparison."""
        d = {}
        if self.cpu:
            d["cpu"] = self.cpu
        if self.ram_gb:
            d["ram_gb"] = self.ram_gb
        if self.storage_gb:
            d["storage_gb"] = self.storage_gb
        if self.screen_inches:
            d["screen_inches"] = self.screen_inches
        if self.wattage:
            d["wattage"] = self.wattage
        if self.voltage:
            d["voltage"] = self.voltage
        if self.capacity_mah:
            d["capacity_mah"] = self.capacity_mah
        if self.size_label:
            d["size_label"] = self.size_label
        if self.generation:
            d["generation"] = self.generation
        if self.quantity:
            d["quantity"] = self.quantity
        return d

    def diff(self, other: "SpecFingerprint") -> dict:
        """Returns specs that differ between two fingerprints.
        Only compares specs that are present in BOTH fingerprints.
        Applies tolerances for known near-equivalences."""
        diffs = {}
        mine = self.spec_dict()
        theirs = other.spec_dict()
        # Only compare overlapping keys — if one listing doesn't mention RAM
        # and the other does, we can't conclude they differ
        for key in mine.keys() & theirs.keys():
            a, b = mine[key], theirs[key]
            if a == b:
                continue

            # Screen size tolerance: 16.0 vs 16.2, 14.0 vs 14.2 etc.
            # Apple and others market rounded sizes but actual panels differ slightly
            if key == "screen_inches":
                if abs(float(a) - float(b)) <= 0.3:
                    continue

            # Voltage equivalence: DeWalt 20V Max = 18V nominal
            # Many tool brands use both interchangeably
            if key == "voltage":
                pair = tuple(sorted([int(a), int(b)]))
                if pair == (18, 20):
                    continue

            diffs[key] = (a, b)
        return diffs


# CPU patterns — order matters, longer patterns first
CPU_PATTERNS = [
    # Intel generations: i3-8100B, i5-12400, i7 8700B
    re.compile(r'\b(i[3579])\s*[-]?\s*(\d{4,5}[A-Z]*)\b', re.IGNORECASE),
    # Intel simple: i3, i5, i7, i9
    re.compile(r'\b(i[3579])\b', re.IGNORECASE),
    # Apple Silicon: M1, M2, M3, M4 (with optional Pro/Max/Ultra)
    re.compile(r'\b(M[1-4])\s*(Pro|Max|Ultra)?\b', re.IGNORECASE),
    # AMD Ryzen
    re.compile(r'\b(Ryzen\s*\d)\b', re.IGNORECASE),
    # Apple A-series: A14, A15, A17
    re.compile(r'\b(A\d{2})\b', re.IGNORECASE),
    # Snapdragon
    re.compile(r'\b(Snapdragon\s*\d{3,4})\b', re.IGNORECASE),
    # GPU: RTX 3060, RTX 4090, GTX 1080
    re.compile(r'\b((?:RTX|GTX)\s*\d{4}\s*(?:Ti|SUPER)?)\b', re.IGNORECASE),
]

# RAM pattern: match "8GB RAM", "16 GB DDR4", "8GB" when near RAM context
RAM_PATTERN = re.compile(
    r'\b(\d+)\s*GB\s*(?:RAM|DDR\d?|Memory|LPDDR\d?)\b', re.IGNORECASE
)
# Fallback: "8GB" alone, but only if not followed by storage indicators
RAM_FALLBACK = re.compile(
    r'\b(\d+)\s*GB\b(?!\s*(?:SSD|HDD|Storage|eMMC|Flash|Disk|Hard|NVME|NVMe))',
    re.IGNORECASE
)

# Storage patterns
STORAGE_TB = re.compile(
    r'\b(\d+)\s*TB\s*(?:SSD|HDD|Storage|NVMe|NVME|Hard|Disk)?\b', re.IGNORECASE
)
STORAGE_GB = re.compile(
    r'\b(\d+)\s*GB\s*(?:SSD|HDD|Storage|eMMC|Flash|Disk|Hard|NVMe|NVME)\b',
    re.IGNORECASE
)
# Fallback: common storage sizes without keywords (128GB, 256GB, 512GB standalone)
STORAGE_COMMON_SIZES = {128, 256, 512}

# Screen size
SCREEN_PATTERN = re.compile(
    r'\b(\d+\.?\d*)\s*(?:"|inch|Inch|INCH|-inch|-Inch)\b', re.IGNORECASE
)

# Wattage — require >=5W and exclude "W/" (means "with"), "W/O" (without)
WATTAGE_PATTERN = re.compile(r'\b(\d+)\s*W\b(?!h|att|/|\s*/)', re.IGNORECASE)

# Voltage
VOLTAGE_PATTERN = re.compile(r'\b(\d+)\s*V\b', re.IGNORECASE)

# Battery capacity
MAH_PATTERN = re.compile(r'\b(\d+)\s*mAh\b', re.IGNORECASE)

# Size labels (clothing/shoes)
# Capture the region system AND the number: "UK 8.5", "US 9", "EU 42"
SIZE_PATTERN = re.compile(
    r'\b(?:Size\s*)?(UK|US|EU)\s*(\d+\.?\d*)\b', re.IGNORECASE
)
# Letter sizes — require explicit context to avoid false matches on random S/M/L in words.
# Match: "Size S", "Size M", "size L", "- XL", ", M,", standalone XS/XL/XXL etc.
SIZE_LABEL_PATTERN = re.compile(
    r'\b(?:Size\s+)(S|M|L|XS|XL|XXS|XXL|XXXL)\b'       # "Size M", "Size XL"
    r'|\b(XXS|XS|XXL|XL|XXXL)\b'                         # Multi-letter always safe
    r'|(?<=[\s,/(-])(S|M|L)(?=[\s,/)\]-]|\Z)',           # S/M/L only with clear delimiters
    re.IGNORECASE
)

# Generation/version
GEN_PATTERN = re.compile(
    r'\b(?:Gen(?:eration)?\s*(\d+)|(\d+)(?:st|nd|rd|th)\s*Gen(?:eration)?|'
    r'Mark\s*(\d+)|(?:Series|Version|Rev)\s*(\d+))\b',
    re.IGNORECASE
)

# Quantity/bundle — capture meaningful quantity indicators
# Skip "1 x" (single unit, meaningless) but capture 2+
QUANTITY_PATTERN = re.compile(
    r'\b(bundle|lot|pair|set\s*of\s*\d+|\d+\s*pack|[2-9]\d*\s*x\s+|x\s*[2-9]\d*\b)',
    re.IGNORECASE
)

# Normalize quantity strings to canonical form for comparison
def _normalize_quantity(raw: str) -> str:
    """Normalize quantity expressions: 'pair' -> '2x', '2 x' -> '2x', 'x2' -> '2x', etc."""
    raw = raw.lower().strip()
    if raw == "pair":
        return "2x"
    # "2 x " or "2x " -> "2x"
    m = re.match(r'^(\d+)\s*x\s*$', raw)
    if m:
        return f"{m.group(1)}x"
    # "x2" or "x 2" -> "2x"
    m = re.match(r'^x\s*(\d+)$', raw)
    if m:
        return f"{m.group(1)}x"
    # "20 pack" -> "20pack"
    m = re.match(r'^(\d+)\s*pack$', raw)
    if m:
        return f"{m.group(1)}pack"
    # "set of 3" -> "setof3"
    m = re.match(r'^set\s*of\s*(\d+)$', raw)
    if m:
        return f"setof{m.group(1)}"
    return raw


def extract_fingerprint(title: str) -> SpecFingerprint:
    """Extract spec fingerprint from an eBay listing title."""
    if not title:
        return SpecFingerprint()

    fp = SpecFingerprint()
    tokens = []

    # CPU — try patterns in order, take first match
    # Skip CPU extraction for products where "i3/i5/i7" are model numbers, not CPUs
    _non_cpu_context = re.compile(
        r'Roomba|Vacuum|Cleaner|Brush|Filter|Dust\s*Bag|Side\s*Brush',
        re.IGNORECASE
    )
    skip_cpu = bool(_non_cpu_context.search(title))

    if not skip_cpu:
        for pattern in CPU_PATTERNS:
            m = pattern.search(title)
            if m:
                cpu_str = m.group(0).strip().upper()
                # Normalize: collapse all whitespace so "M3 PRO" == "M3PRO"
                cpu_str = re.sub(r'\s+', '', cpu_str)
                fp.cpu = cpu_str
                tokens.append(f"cpu:{cpu_str}")
                break

    # Storage — extract FIRST so RAM fallback can exclude the storage value
    m = STORAGE_TB.search(title)
    if m:
        fp.storage_gb = int(m.group(1)) * 1024
        tokens.append(f"storage:{m.group(1)}TB")
    else:
        m = STORAGE_GB.search(title)
        if m:
            fp.storage_gb = int(m.group(1))
            tokens.append(f"storage:{fp.storage_gb}GB")
        else:
            # Fallback: standalone common storage sizes (128GB, 256GB, 512GB)
            # without explicit keywords — common in tablet/phone titles like "128GB/6GB"
            all_gb = re.findall(r'\b(\d+)\s*GB\b', title, re.IGNORECASE)
            for val_str in all_gb:
                val = int(val_str)
                if val in STORAGE_COMMON_SIZES:
                    fp.storage_gb = val
                    tokens.append(f"storage:{val}GB")
                    break

    # RAM — explicit first, then fallback
    m = RAM_PATTERN.search(title)
    if m:
        fp.ram_gb = int(m.group(1))
        tokens.append(f"ram:{fp.ram_gb}GB")
    else:
        # Fallback: find all standalone GB values, pick the one most likely to be RAM
        # (typically smaller values like 4, 8, 16, 32, 64 are RAM)
        fallback_matches = RAM_FALLBACK.findall(title)
        # Filter to common RAM sizes, exclude the storage value to avoid confusion
        ram_sizes = {2, 4, 6, 8, 12, 16, 24, 32, 48, 64, 128}
        for val_str in fallback_matches:
            val = int(val_str)
            # Skip if this matches the already-extracted storage value
            if fp.storage_gb and val == fp.storage_gb:
                continue
            # Also skip if this matches storage in TB (e.g. 1TB = 1024GB, but raw "1" won't be in fallback)
            if val in ram_sizes:
                fp.ram_gb = val
                tokens.append(f"ram:{val}GB")
                break

    # Screen size
    m = SCREEN_PATTERN.search(title)
    if m:
        fp.screen_inches = float(m.group(1))
        tokens.append(f"screen:{fp.screen_inches}in")

    # Wattage — skip very small values that are likely model numbers
    m = WATTAGE_PATTERN.search(title)
    if m:
        watts = int(m.group(1))
        if watts >= 5:
            fp.wattage = watts
            tokens.append(f"watts:{fp.wattage}W")

    # Voltage
    m = VOLTAGE_PATTERN.search(title)
    if m:
        fp.voltage = int(m.group(1))
        tokens.append(f"volts:{fp.voltage}V")

    # Battery capacity
    m = MAH_PATTERN.search(title)
    if m:
        fp.capacity_mah = int(m.group(1))
        tokens.append(f"battery:{fp.capacity_mah}mAh")

    # Size (clothing/shoes)
    m = SIZE_PATTERN.search(title)
    if m:
        # Normalize to "UK 8.5" format (strip "Size" prefix, standardize spacing)
        region = m.group(1).upper()
        num = m.group(2)
        fp.size_label = f"{region} {num}"
        tokens.append(f"size:{fp.size_label}")
    else:
        m = SIZE_LABEL_PATTERN.search(title)
        if m:
            # Get whichever group matched
            label = next(g for g in m.groups() if g is not None)
            fp.size_label = label.upper().strip()
            tokens.append(f"size:{fp.size_label}")

    # Generation
    m = GEN_PATTERN.search(title)
    if m:
        gen_num = next(g for g in m.groups() if g is not None)
        fp.generation = f"Gen{gen_num}"
        tokens.append(f"gen:{fp.generation}")

    # Quantity/bundle
    m = QUANTITY_PATTERN.search(title)
    if m:
        fp.quantity = _normalize_quantity(m.group(0))
        tokens.append(f"qty:{fp.quantity}")

    fp.raw_tokens = tuple(sorted(tokens))
    return fp


def describe_diff(diffs: dict) -> str:
    """Generate human-readable reasoning for a spec mismatch."""
    parts = []
    labels = {
        "cpu": "CPU",
        "ram_gb": "RAM",
        "storage_gb": "Storage",
        "screen_inches": "Screen size",
        "wattage": "Wattage",
        "voltage": "Voltage",
        "capacity_mah": "Battery",
        "size_label": "Size",
        "generation": "Generation",
        "quantity": "Quantity",
    }
    for key, (val_a, val_b) in diffs.items():
        label = labels.get(key, key)
        if key in ("ram_gb", "storage_gb"):
            parts.append(f"Different {label}: {val_a}GB vs {val_b}GB")
        elif key == "screen_inches":
            parts.append(f'Different {label}: {val_a}" vs {val_b}"')
        elif key == "wattage":
            parts.append(f"Different {label}: {val_a}W vs {val_b}W")
        elif key == "voltage":
            parts.append(f"Different {label}: {val_a}V vs {val_b}V")
        elif key == "capacity_mah":
            parts.append(f"Different {label}: {val_a}mAh vs {val_b}mAh")
        else:
            parts.append(f"Different {label}: {val_a} vs {val_b}")
    return "; ".join(parts)


# ── Database ─────────────────────────────────────────────────────────────

@dataclass
class PairRow:
    """A classified pair from ListingRelationships with listing details."""
    rel_id: int
    listing_id_a: int
    listing_id_b: int
    is_comparable: bool
    similarity_score: float
    ebay_id_a: str
    title_a: str
    desc_a: Optional[str]
    job_id_a: int
    search_term_a: str
    ebay_id_b: str
    title_b: str
    desc_b: Optional[str]
    job_id_b: int
    search_term_b: str


def load_comparable_pairs(conn) -> list[PairRow]:
    """Load all IsComparable=1 pairs with listing details."""
    print("Loading comparable pairs from database...")

    query = """
        SELECT
            lr.Id, lr.ListingIdA, lr.ListingIdB,
            lr.IsComparable, lr.SimilarityScore,
            la.ListingId, la.Title, la.Description, la.ScrapeJobId,
            ISNULL(ja.SearchTerm, 'Unknown'),
            lb.ListingId, lb.Title, lb.Description, lb.ScrapeJobId,
            ISNULL(jb.SearchTerm, 'Unknown')
        FROM ListingRelationships lr
        INNER JOIN Listings la ON la.Id = lr.ListingIdA
        INNER JOIN Listings lb ON lb.Id = lr.ListingIdB
        LEFT JOIN ScrapeJobs ja ON ja.Id = la.ScrapeJobId
        LEFT JOIN ScrapeJobs jb ON jb.Id = lb.ScrapeJobId
        WHERE lr.IsComparable = 1
          AND la.Title IS NOT NULL
          AND lb.Title IS NOT NULL
    """

    cursor = conn.cursor()
    cursor.execute(query)

    pairs = []
    row_count = 0
    for row in cursor:
        row_count += 1
        pairs.append(PairRow(
            rel_id=row[0],
            listing_id_a=row[1],
            listing_id_b=row[2],
            is_comparable=bool(row[3]),
            similarity_score=float(row[4]) if row[4] else 0.0,
            ebay_id_a=str(row[5]),
            title_a=str(row[6]) if row[6] else "",
            desc_a=str(row[7]) if row[7] else None,
            job_id_a=int(row[8]) if row[8] else 0,
            search_term_a=str(row[9]),
            ebay_id_b=str(row[10]),
            title_b=str(row[11]) if row[11] else "",
            desc_b=str(row[12]) if row[12] else None,
            job_id_b=int(row[13]) if row[13] else 0,
            search_term_b=str(row[14]),
        ))

        if row_count % 50000 == 0:
            print(f"  ...loaded {row_count:,} pairs")

    print(f"  Loaded {len(pairs):,} comparable pairs total")
    return pairs


# ── Mining ───────────────────────────────────────────────────────────────

@dataclass
class MinedPair:
    """A mined hard pair ready for CSV output."""
    anchor_id: str
    neighbor_id: str
    job_id: int
    product_name: str
    anchor_title: str
    neighbor_title: str
    anchor_desc: Optional[str]
    neighbor_desc: Optional[str]
    label: int
    confidence: str
    reasoning: str
    source: str


def mine_pairs(
    pairs: list[PairRow],
    max_neg_per_cat: int,
    max_pos_per_cat: int,
) -> tuple[list[MinedPair], dict]:
    """
    Analyze all comparable pairs with spec extraction.
    Returns (mined_pairs, stats_dict).
    """
    print("\nExtracting spec fingerprints and analyzing pairs...")

    # Track stats
    stats = {
        "total_pairs": len(pairs),
        "both_have_specs": 0,
        "one_has_specs": 0,
        "neither_has_specs": 0,
        "false_positives": 0,
        "confirmed_positives": 0,
        "by_category": defaultdict(lambda: {
            "total": 0, "false_pos": 0, "confirmed_pos": 0,
            "no_specs": 0, "diff_details": defaultdict(int)
        }),
    }

    # Collect candidates by category
    false_pos_by_cat = defaultdict(list)  # category -> [(pair, diffs)]
    confirmed_pos_by_cat = defaultdict(list)  # category -> [pair]

    for i, pair in enumerate(pairs):
        if (i + 1) % 50000 == 0:
            print(f"  ...analyzed {i + 1:,}/{len(pairs):,} pairs")

        fp_a = extract_fingerprint(pair.title_a)
        fp_b = extract_fingerprint(pair.title_b)

        # Use the anchor's category as the category label
        category = pair.search_term_a
        cat_stats = stats["by_category"][category]
        cat_stats["total"] += 1

        if fp_a.has_specs and fp_b.has_specs:
            stats["both_have_specs"] += 1
            diffs = fp_a.diff(fp_b)

            if diffs:
                # Specs differ — this is a FALSE POSITIVE
                stats["false_positives"] += 1
                cat_stats["false_pos"] += 1
                for diff_key in diffs:
                    cat_stats["diff_details"][diff_key] += 1
                false_pos_by_cat[category].append((pair, diffs))
            else:
                # Specs that overlap all match — confirmed positive
                stats["confirmed_positives"] += 1
                cat_stats["confirmed_pos"] += 1
                confirmed_pos_by_cat[category].append(pair)

        elif fp_a.has_specs or fp_b.has_specs:
            stats["one_has_specs"] += 1
            cat_stats["no_specs"] += 1
        else:
            stats["neither_has_specs"] += 1
            cat_stats["no_specs"] += 1

    # Generate output pairs with per-category caps
    mined = []

    # Hard negatives (false positives → label=0)
    for category, candidates in false_pos_by_cat.items():
        # Prioritize pairs with fewer differing specs (harder negatives)
        candidates.sort(key=lambda x: len(x[1]))
        for pair, diffs in candidates[:max_neg_per_cat]:
            reasoning = describe_diff(diffs)
            mined.append(MinedPair(
                anchor_id=pair.ebay_id_a,
                neighbor_id=pair.ebay_id_b,
                job_id=pair.job_id_a,
                product_name=pair.search_term_a,
                anchor_title=pair.title_a,
                neighbor_title=pair.title_b,
                anchor_desc=pair.desc_a or "",
                neighbor_desc=pair.desc_b or "",
                label=0,
                confidence="high",
                reasoning=reasoning,
                source="false_positive_mined",
            ))

    # Hard positives (confirmed true positives → label=1)
    for category, candidates in confirmed_pos_by_cat.items():
        for pair in candidates[:max_pos_per_cat]:
            fp_a = extract_fingerprint(pair.title_a)
            specs_str = ", ".join(fp_a.raw_tokens) if fp_a.raw_tokens else "matching specs"
            mined.append(MinedPair(
                anchor_id=pair.ebay_id_a,
                neighbor_id=pair.ebay_id_b,
                job_id=pair.job_id_a,
                product_name=pair.search_term_a,
                anchor_title=pair.title_a,
                neighbor_title=pair.title_b,
                anchor_desc=pair.desc_a or "",
                neighbor_desc=pair.desc_b or "",
                label=1,
                confidence="high",
                reasoning=f"Same specifications confirmed: {specs_str}",
                source="confirmed_positive_mined",
            ))

    return mined, stats


def print_stats(stats: dict):
    """Print analysis summary."""
    total = stats["total_pairs"]
    print(f"\n{'='*70}")
    print(f"ANALYSIS SUMMARY")
    print(f"{'='*70}")
    print(f"Total comparable pairs analyzed:  {total:,}")
    print(f"Both listings have specs:         {stats['both_have_specs']:,} ({100*stats['both_have_specs']/total:.1f}%)")
    print(f"Only one has specs:               {stats['one_has_specs']:,} ({100*stats['one_has_specs']/total:.1f}%)")
    print(f"Neither has specs:                {stats['neither_has_specs']:,} ({100*stats['neither_has_specs']/total:.1f}%)")
    print()
    print(f"FALSE POSITIVES (specs differ):   {stats['false_positives']:,} ({100*stats['false_positives']/total:.1f}%)")
    print(f"CONFIRMED POSITIVES (specs match):{stats['confirmed_positives']:,} ({100*stats['confirmed_positives']/total:.1f}%)")

    # Per-category breakdown (sorted by false positive count)
    print(f"\n{'='*70}")
    print(f"PER-CATEGORY BREAKDOWN (sorted by false positive count)")
    print(f"{'='*70}")
    print(f"{'Category':<40} {'Total':>7} {'FP':>7} {'FP%':>6} {'TP':>7} {'NoSpec':>7}")
    print(f"{'-'*40} {'-'*7} {'-'*7} {'-'*6} {'-'*7} {'-'*7}")

    sorted_cats = sorted(
        stats["by_category"].items(),
        key=lambda x: x[1]["false_pos"],
        reverse=True
    )

    for cat, cs in sorted_cats:
        if cs["total"] == 0:
            continue
        fp_pct = 100 * cs["false_pos"] / cs["total"] if cs["total"] > 0 else 0
        print(
            f"{cat[:40]:<40} {cs['total']:>7,} {cs['false_pos']:>7,} "
            f"{fp_pct:>5.1f}% {cs['confirmed_pos']:>7,} {cs['no_specs']:>7,}"
        )
        # Show what specs differ
        if cs["diff_details"]:
            details = ", ".join(
                f"{k}({v})" for k, v in
                sorted(cs["diff_details"].items(), key=lambda x: -x[1])
            )
            print(f"  {'':>40} diffs: {details}")


def write_csv(mined: list[MinedPair], output_path: Path):
    """Write mined pairs to CSV matching v6 schema."""
    print(f"\nWriting {len(mined):,} pairs to {output_path}...")

    with open(output_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow([
            "anchor_id", "neighbor_id", "job_id", "product_name",
            "anchor_title", "neighbor_title", "anchor_desc", "neighbor_desc",
            "label", "confidence", "reasoning", "source"
        ])
        for p in mined:
            writer.writerow([
                p.anchor_id, p.neighbor_id, p.job_id, p.product_name,
                p.anchor_title, p.neighbor_title, p.anchor_desc, p.neighbor_desc,
                p.label, p.confidence, p.reasoning, p.source
            ])

    print(f"  Done. {len(mined):,} pairs written.")


# ── Main ─────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Mine hard pairs from ListingRelationships")
    parser.add_argument("--dry-run", action="store_true", help="Analyze only, don't write CSV")
    parser.add_argument("--max-neg-per-cat", type=int, default=DEFAULT_MAX_HARD_NEG_PER_CAT,
                        help=f"Max hard negatives per category (default: {DEFAULT_MAX_HARD_NEG_PER_CAT})")
    parser.add_argument("--max-pos-per-cat", type=int, default=DEFAULT_MAX_HARD_POS_PER_CAT,
                        help=f"Max hard positives per category (default: {DEFAULT_MAX_HARD_POS_PER_CAT})")
    args = parser.parse_args()

    print("Connecting to database...")
    conn = pyodbc.connect(DB_CONN)

    pairs = load_comparable_pairs(conn)
    conn.close()

    mined, stats = mine_pairs(pairs, args.max_neg_per_cat, args.max_pos_per_cat)
    print_stats(stats)

    hard_neg = sum(1 for p in mined if p.label == 0)
    hard_pos = sum(1 for p in mined if p.label == 1)
    print(f"\n{'='*70}")
    print(f"MINED PAIRS")
    print(f"{'='*70}")
    print(f"Hard negatives (label=0):  {hard_neg:,}")
    print(f"Hard positives (label=1):  {hard_pos:,}")
    print(f"Total:                     {len(mined):,}")

    if not args.dry_run and mined:
        write_csv(mined, OUTPUT_CSV)
    elif args.dry_run:
        print("\n[DRY RUN] No CSV written.")
    else:
        print("\nNo pairs to write.")


if __name__ == "__main__":
    main()
