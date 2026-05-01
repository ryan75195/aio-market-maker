"""
V11 Dataset Audit: Scan v10 labels for systematic errors and report
what would be corrected before making any changes.

Patterns scanned:
  1. Condition tier gaps (sealed/new vs for-parts/faulty, Grade A vs Grade C, etc.)
  2. Accessory vs product confusion (disc drive vs console, barrel vs full kit)
  3. Product tier confusion (Premium vs Ultra-Premium, standard vs Pro/Max)
  4. Quantity mismatch (single vs bundle/lot/joblot)

Usage:
    py -3.12 audit_v11.py              # dry-run report only
    py -3.12 audit_v11.py --apply      # apply corrections and write v11
    py -3.12 audit_v11.py --sample 20  # show 20 sample corrections per pattern
"""

import argparse
import csv
import re
import sys
import io
from collections import defaultdict, Counter
from pathlib import Path

csv.field_size_limit(10_000_000)
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

DATA_DIR = Path(__file__).parent.parent / "data"
V11_CSV = DATA_DIR / "labeled_pairs_v11.csv"
OUTPUT_CSV = DATA_DIR / "labeled_pairs_v11.csv"  # overwrite in place

# ═══════════════════════════════════════════════════════════════════════
# CONDITION TIER EXTRACTION
# ═══════════════════════════════════════════════════════════════════════

# Tier 0: NEW/SEALED (highest value)
NEW_PATTERNS = re.compile(
    r'\b(?:sealed|bnib|brand\s*new|factory\s*sealed|unopened|shrink\s*wrap'
    r'|new\s*in\s*box|new\s*sealed|mint\s*sealed)\b',
    re.IGNORECASE,
)

# Tier 1: EXCELLENT / GRADE A / MINT (opened but pristine)
TIER_A_PATTERNS = re.compile(
    r'\b(?:grade\s*a\+?|pristine|mint\s*condition|excellent|like\s*new'
    r'|near\s*mint|nm\+?|immaculate|flawless)\b',
    re.IGNORECASE,
)

# Tier 2: GOOD / GRADE B (normal used)
TIER_B_PATTERNS = re.compile(
    r'\b(?:grade\s*b|good\s*condition|very\s*good|great\s*condition'
    r'|gently\s*used|lightly\s*used|pre[\s-]?owned)\b',
    re.IGNORECASE,
)

# Tier 3: FAIR / GRADE C (visibly worn)
TIER_C_PATTERNS = re.compile(
    r'\b(?:grade\s*c|fair\s*condition|average|scratches|scuffs|dents'
    r'|cosmetic\s*damage|well\s*used|heavily\s*used|worn)\b',
    re.IGNORECASE,
)

# Tier 4: PARTS/BROKEN (non-functional, lowest value)
PARTS_PATTERNS = re.compile(
    r'\b(?:for\s*parts|spares|faulty|broken|not\s*working|damaged'
    r'|as[\s-]is|defective|dead|cracked\s*screen|water\s*damage'
    r'|doesn.t\s*work|won.t\s*turn\s*on|needs\s*repair)\b',
    re.IGNORECASE,
)


def extract_condition_tier(title, desc=""):
    """Extract condition tier from title + description.
    Returns (tier_number, tier_name, matched_text) or None."""
    text = f"{title} {desc[:200]}"

    # Check from worst to best (most specific first)
    m = PARTS_PATTERNS.search(text)
    if m:
        return (4, "PARTS", m.group(0))

    m = TIER_C_PATTERNS.search(text)
    if m:
        return (3, "FAIR/C", m.group(0))

    m = TIER_B_PATTERNS.search(text)
    if m:
        return (2, "GOOD/B", m.group(0))

    m = TIER_A_PATTERNS.search(text)
    if m:
        return (1, "EXCELLENT/A", m.group(0))

    m = NEW_PATTERNS.search(text)
    if m:
        return (0, "NEW/SEALED", m.group(0))

    return None


# ═══════════════════════════════════════════════════════════════════════
# ACCESSORY VS PRODUCT DETECTION
# ═══════════════════════════════════════════════════════════════════════

# Patterns where one listing is an accessory and the other is the full product
ACCESSORY_INDICATORS = re.compile(
    r'\b(?:replacement\s+(?:cord|cable|charger|battery|strap|band|barrel|brush|filter|blade|nozzle|head|tip|pad|roller)'
    r'|spare\s+(?:battery|charger|cable|strap|band|part|wheel)'
    r'|(?:cord|cable|charger|strap|band|barrel|brush|filter|blade|nozzle)\s+only'
    r'|disc\s*drive\s*(?:only|unit|attachment)'
    r'|power\s*(?:cord|cable|adapter)\s+(?:only|replacement|for\b)'
    r'|(?:just|only)\s+the\s+(?:cord|cable|charger|remote|controller|strap|band|base|dock|stand))\b',
    re.IGNORECASE,
)

FULL_PRODUCT_INDICATORS = re.compile(
    r'\b(?:full\s*(?:set|kit|system|bundle|package)'
    r'|complete\s*(?:set|kit|system|with)'
    r'|console\s*(?:bundle|with|only)'
    r'|main\s*unit'
    r'|all\s*(?:included|accessories|attachments))\b',
    re.IGNORECASE,
)


def is_accessory_vs_product(title_a, desc_a, title_b, desc_b):
    """Check if one listing is an accessory and the other is the full product.
    Only checks TITLES — descriptions are too noisy (full products mention included cables).
    Both being accessories is fine (they're comparable to each other)."""
    a_is_accessory = bool(ACCESSORY_INDICATORS.search(title_a))
    b_is_accessory = bool(ACCESSORY_INDICATORS.search(title_b))

    # Both accessories = comparable to each other, skip
    if a_is_accessory and b_is_accessory:
        return False, ""

    if a_is_accessory and not b_is_accessory:
        return True, f"A is accessory ({ACCESSORY_INDICATORS.search(title_a).group(0)}), B is product"
    if b_is_accessory and not a_is_accessory:
        return True, f"B is accessory ({ACCESSORY_INDICATORS.search(title_b).group(0)}), A is product"

    return False, ""


# ═══════════════════════════════════════════════════════════════════════
# PRODUCT TIER / MODEL CONFUSION
# ═══════════════════════════════════════════════════════════════════════

def check_product_tier(title_a, title_b):
    """Check if titles refer to different product tiers.
    Only flags when the two sides are genuinely different tiers."""

    # Premium vs Ultra-Premium — must check one side does NOT have "ultra"
    a_has_ultra = bool(re.search(r'\bultra[\s-]?premium', title_a, re.I))
    b_has_ultra = bool(re.search(r'\bultra[\s-]?premium', title_b, re.I))
    a_has_premium = bool(re.search(r'\bpremium\s+collection\b', title_a, re.I))
    b_has_premium = bool(re.search(r'\bpremium\s+collection\b', title_b, re.I))

    # One has Ultra-Premium, other has Premium (without Ultra)
    if (a_has_ultra and not b_has_ultra and b_has_premium and not a_has_premium):
        return True, "Premium Collection vs Ultra-Premium Collection"
    if (b_has_ultra and not a_has_ultra and a_has_premium and not b_has_premium):
        return True, "Premium Collection vs Ultra-Premium Collection"

    # Digital vs Disc edition
    a_digital = bool(re.search(r'\bdigital\s+edition\b', title_a, re.I))
    b_digital = bool(re.search(r'\bdigital\s+edition\b', title_b, re.I))
    a_disc = bool(re.search(r'\bdisc\s+(?:edition|version)\b', title_a, re.I))
    b_disc = bool(re.search(r'\bdisc\s+(?:edition|version)\b', title_b, re.I))
    if (a_digital and b_disc) or (a_disc and b_digital):
        return True, "Digital vs Disc edition"

    # PS5 Slim vs PS5 original — different products, different prices
    a_ps5 = bool(re.search(r'\b(?:PS5|PlayStation\s*5)\b', title_a, re.I))
    b_ps5 = bool(re.search(r'\b(?:PS5|PlayStation\s*5)\b', title_b, re.I))
    if a_ps5 and b_ps5:
        a_slim = bool(re.search(r'\bslim\b', title_a, re.I))
        b_slim = bool(re.search(r'\bslim\b', title_b, re.I))
        if a_slim != b_slim:
            return True, "PS5 Slim vs PS5 original"

    return False, ""


# ═══════════════════════════════════════════════════════════════════════
# STORAGE / SPEC MISMATCH
# ═══════════════════════════════════════════════════════════════════════

STORAGE_PATTERN = re.compile(r'\b(\d+)\s*(GB|TB)\b', re.I)
# Multi-SKU: "128/256/512GB" or "128GB 256GB 512GB" (3+ sizes) or "128GB, 256GB"
MULTI_SKU_SLASH = re.compile(r'\d+\s*/\s*\d+\s*(?:GB|TB)', re.I)
MULTI_SKU_COMMA = re.compile(r'\d+\s*(?:GB|TB)\s*,\s*\d+\s*(?:GB|TB)', re.I)
# Common RAM sizes that should be excluded
RAM_SIZES_GB = {4, 8, 12, 16, 18, 24, 32, 36, 48, 64}


def normalize_storage_gb(val, unit):
    """Normalize to GB. 2TB = 2000GB (not 2048) since SSDs use decimal."""
    v = int(val)
    if unit.upper() == 'TB':
        v *= 1000
    return v


def extract_storage_specs(title):
    """Extract storage values from title, filtering out RAM.
    Returns a set of storage capacities in GB."""
    matches = STORAGE_PATTERN.findall(title)
    if not matches:
        return set()

    specs = set()
    for val_str, unit in matches:
        gb = normalize_storage_gb(val_str, unit)
        # Skip common RAM sizes unless title explicitly says SSD/storage
        if gb in RAM_SIZES_GB:
            # Only keep if "SSD" or "storage" appears near it
            if not re.search(r'\bSSD\b', title, re.I):
                continue
        specs.add(gb)

    return specs


def check_storage_mismatch(title_a, title_b):
    """Check if listings have different storage capacities.
    Skips multi-SKU listings and RAM-only values."""
    # Skip multi-SKU listings (multiple options, not a single config)
    if MULTI_SKU_SLASH.search(title_a) or MULTI_SKU_SLASH.search(title_b):
        return False, ""
    if MULTI_SKU_COMMA.search(title_a) or MULTI_SKU_COMMA.search(title_b):
        return False, ""
    # Also skip if 3+ different GB/TB values (probably listing all options)
    if len(STORAGE_PATTERN.findall(title_a)) >= 3 or len(STORAGE_PATTERN.findall(title_b)) >= 3:
        return False, ""

    a_storage = extract_storage_specs(title_a)
    b_storage = extract_storage_specs(title_b)

    if not a_storage or not b_storage:
        return False, ""

    # Compare: use the largest value from each side as the primary storage
    a_max = max(a_storage)
    b_max = max(b_storage)

    if a_max == b_max:
        return False, ""

    # Only flag significant differences (>15% apart)
    if abs(a_max - b_max) / max(a_max, b_max) < 0.15:
        return False, ""

    return True, f"Storage: {a_max}GB vs {b_max}GB"


# ═══════════════════════════════════════════════════════════════════════
# CPU / CHIP MISMATCH (laptops only)
# ═══════════════════════════════════════════════════════════════════════

APPLE_CHIP_PATTERN = re.compile(r'\b(M[1-4])\s*(?:(Pro|Max|Ultra))?\b', re.I)
LAPTOP_KEYWORDS = re.compile(r'\b(?:laptop|macbook|thinkpad|zenbook|xps|surface|elitebook|spectre|pavilion|inspiron|desktop|pc|workstation)\b', re.I)


def extract_apple_chip(title):
    """Extract Apple chip from title, normalizing variants.
    Returns 'base', 'pro', 'max', or 'ultra' for the chip tier, or None."""
    m = APPLE_CHIP_PATTERN.search(title)
    if not m:
        return None
    base = m.group(1).upper()  # "M3"
    variant = m.group(2)  # "Pro", "Max", etc. or None
    if variant:
        return f"{base} {variant.capitalize()}"

    # Check if "Pro"/"Max" appears shortly after M3 (within ~20 chars)
    # Handles "M3 2023 Pro" or "M3 14 Core ... Pro"
    after = title[m.end():]
    pro_check = re.match(r'.{0,25}\b(Pro|Max|Ultra)\b', after, re.I)
    if pro_check:
        return f"{base} {pro_check.group(1).capitalize()}"

    return base


def check_cpu_mismatch(title_a, title_b):
    """Check for different CPU/chip variants in laptops/computers.
    Skips non-computer products where i3/i5/i7 are model names (e.g., Roomba)."""
    # Apple M-series chips
    a_chip = extract_apple_chip(title_a)
    b_chip = extract_apple_chip(title_b)
    if a_chip and b_chip and a_chip != b_chip:
        return True, f"Chip: {a_chip} vs {b_chip}"

    # Intel i-series — only if context is a laptop/computer
    if LAPTOP_KEYWORDS.search(title_a) or LAPTOP_KEYWORDS.search(title_b):
        intel = re.compile(r'\bi([3579])\b', re.I)
        a_intel = set(m.group(0).lower() for m in intel.finditer(title_a))
        b_intel = set(m.group(0).lower() for m in intel.finditer(title_b))
        if a_intel and b_intel and a_intel != b_intel:
            return True, f"CPU: {a_intel} vs {b_intel}"

    return False, ""


# ═══════════════════════════════════════════════════════════════════════
# QUANTITY MISMATCH
# ═══════════════════════════════════════════════════════════════════════

BUNDLE_PATTERNS = re.compile(
    r'\b(?:joblot|job\s*lot|bundle\s*of\s*\d+|lot\s*of\s*\d+)\b',
    re.IGNORECASE,
)
# "N pack/pcs/pieces" — but not "backpack" or model names like "Atmos 50 Pack"
BUNDLE_COUNT_PATTERN = re.compile(
    r'\b(\d+)\s*(?:pack(?!s?\s*-)|pcs|pieces|items|units)\b',
    re.IGNORECASE,
)
# Separate pattern for "Nx <item>" style — short number (2-999) before "x"
# Also catches reverse "xN" at end of title: "balls x24"
# Excludes model numbers (GDDR6X, M50X, 7950X) and specs (2x M.2, 2x 10 Speed)
BUNDLE_NX_PATTERN = re.compile(
    r'(?<![a-zA-Z\d])([2-9]\d{0,2})\s*x\s+(?![\d.]+\b)(?!(?:M\.?\d|speed|usb|hdmi|pcie|sata|nvme|ddr|ssd|hdd|gpu|cpu|ram|dimm)\b)',
    re.IGNORECASE,
)
# Note: deliberately NOT matching reverse "x24" / "x 36" patterns — too noisy
# (catches canvas sizes like "16 x 16 Inch", resolutions, etc.)


def extract_bundle_quantity(title):
    """Extract bundle quantity from title.
    Returns (match, quantity_int, match_text) or (None, None, None)."""
    m = BUNDLE_PATTERNS.search(title)
    if m:
        # Extract the number from "bundle of 5", "lot of 10"
        nums = re.findall(r'\d+', m.group(0))
        qty = int(nums[0]) if nums else 2
        return m, qty, m.group(0)

    m = BUNDLE_COUNT_PATTERN.search(title)
    if m:
        qty = int(m.group(1))
        if qty <= 1:
            return None, None, None
        # Skip "backpack" style false positives (number is a model, not quantity)
        # Heuristic: if title contains the product category name right before "Pack",
        # it's likely a model name. We check if any letter precedes the number closely.
        return m, qty, m.group(0)

    m = BUNDLE_NX_PATTERN.search(title)
    if m:
        return m, int(m.group(1)), m.group(0)

    return None, None, None


def check_quantity_mismatch(title_a, title_b):
    """Check if one is a bundle/lot and the other isn't.
    If both mention the same quantity, they're comparable."""
    a_match, a_qty, a_text = extract_bundle_quantity(title_a)
    b_match, b_qty, b_text = extract_bundle_quantity(title_b)

    # Both have quantities = comparable (same or similar bundles)
    if a_match and b_match:
        return False, ""

    if a_match and not b_match:
        return True, f"A is bundle ({a_text}), B is single"
    if b_match and not a_match:
        return True, f"B is bundle ({b_text}), A is single"

    return False, ""


# ═══════════════════════════════════════════════════════════════════════
# MAIN AUDIT
# ═══════════════════════════════════════════════════════════════════════

def main():
    parser = argparse.ArgumentParser(description="V11 dataset audit")
    parser.add_argument("--apply", action="store_true",
                        help="Apply corrections and overwrite v11 CSV")
    parser.add_argument("--sample", type=int, default=10,
                        help="Sample corrections to display per pattern")
    args = parser.parse_args()

    print(f"Loading {V11_CSV}...")
    with open(V11_CSV, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        fieldnames = reader.fieldnames
        rows = list(reader)
    print(f"  Loaded {len(rows):,} pairs")

    # Count current label distribution
    label_dist = Counter(r["label"] for r in rows)
    print(f"  Labels: {dict(label_dist)}")

    # Track corrections by pattern
    corrections = {
        "condition_gap": [],
        "accessory_vs_product": [],
        "product_tier": [],
        "quantity_mismatch": [],
        "storage_mismatch": [],
        "cpu_mismatch": [],
    }

    # Also track new hard negatives found in label=1 pairs
    total_label_1 = sum(1 for r in rows if r["label"] == "1")
    total_label_0 = sum(1 for r in rows if r["label"] == "0")

    print(f"\n{'='*70}")
    print(f"SCANNING {total_label_1:,} label=1 (comparable) pairs for errors...")
    print(f"{'='*70}\n")

    for i, row in enumerate(rows):
        if row["label"] != "1":
            continue

        title_a = row.get("anchor_title", "")
        title_b = row.get("neighbor_title", "")
        desc_a = row.get("anchor_desc", "")
        desc_b = row.get("neighbor_desc", "")

        # 1. Condition tier gap
        tier_a = extract_condition_tier(title_a, desc_a)
        tier_b = extract_condition_tier(title_b, desc_b)
        if tier_a and tier_b:
            gap = abs(tier_a[0] - tier_b[0])
            if gap >= 2:
                corrections["condition_gap"].append({
                    "index": i,
                    "reason": f"Condition gap {gap} tiers: "
                              f"A={tier_a[1]} ({tier_a[2]}) vs "
                              f"B={tier_b[1]} ({tier_b[2]})",
                    "title_a": title_a,
                    "title_b": title_b,
                    "category": row.get("product_name", ""),
                })

        # 2. Accessory vs product
        is_acc, acc_reason = is_accessory_vs_product(title_a, desc_a, title_b, desc_b)
        if is_acc:
            corrections["accessory_vs_product"].append({
                "index": i,
                "reason": acc_reason,
                "title_a": title_a,
                "title_b": title_b,
                "category": row.get("product_name", ""),
            })

        # 3. Product tier confusion
        is_tier, tier_reason = check_product_tier(title_a, title_b)
        if is_tier:
            corrections["product_tier"].append({
                "index": i,
                "reason": tier_reason,
                "title_a": title_a,
                "title_b": title_b,
                "category": row.get("product_name", ""),
            })

        # 4. Quantity mismatch
        is_qty, qty_reason = check_quantity_mismatch(title_a, title_b)
        if is_qty:
            corrections["quantity_mismatch"].append({
                "index": i,
                "reason": qty_reason,
                "title_a": title_a,
                "title_b": title_b,
                "category": row.get("product_name", ""),
            })

        # 5. Storage mismatch
        is_stor, stor_reason = check_storage_mismatch(title_a, title_b)
        if is_stor:
            corrections["storage_mismatch"].append({
                "index": i,
                "reason": stor_reason,
                "title_a": title_a,
                "title_b": title_b,
                "category": row.get("product_name", ""),
            })

        # 6. CPU/chip mismatch
        is_cpu, cpu_reason = check_cpu_mismatch(title_a, title_b)
        if is_cpu:
            corrections["cpu_mismatch"].append({
                "index": i,
                "reason": cpu_reason,
                "title_a": title_a,
                "title_b": title_b,
                "category": row.get("product_name", ""),
            })

    # ── Also scan label=0 pairs for potential false negatives ──────────
    # (pairs marked different that might actually be same)
    # This is harder to do with regex, so we skip it for now

    # ═══════════════════════════════════════════════════════════════════
    # REPORT
    # ═══════════════════════════════════════════════════════════════════

    total_corrections = 0
    all_correction_indices = set()

    for pattern, items in corrections.items():
        print(f"\n{'='*70}")
        print(f"PATTERN: {pattern} — {len(items)} errors found in label=1 pairs")
        print(f"{'='*70}")

        if not items:
            print("  (none found)")
            continue

        # Category breakdown
        by_cat = Counter(c["category"] for c in items)
        print(f"\n  By category (top 15):")
        for cat, count in by_cat.most_common(15):
            print(f"    {cat:<40} {count:>5}")

        # Sample corrections
        import random
        random.seed(42)
        sample = random.sample(items, min(args.sample, len(items)))
        print(f"\n  Sample corrections ({len(sample)}):")
        for c in sample:
            print(f"    [{c['category']}] {c['reason']}")
            print(f"      A: {c['title_a'][:80]}")
            print(f"      B: {c['title_b'][:80]}")
            print()

        for c in items:
            all_correction_indices.add(c["index"])
        total_corrections += len(items)

    # Deduplicate (some pairs may match multiple patterns)
    unique_corrections = len(all_correction_indices)

    print(f"\n{'='*70}")
    print(f"SUMMARY")
    print(f"{'='*70}")
    print(f"Total label=1 pairs scanned:   {total_label_1:,}")
    print(f"Total label=0 pairs:           {total_label_0:,}")
    print(f"")
    print(f"Errors found by pattern:")
    for pattern, items in corrections.items():
        print(f"  {pattern:<30} {len(items):>6}")
    print(f"  {'─'*30} {'─'*6}")
    print(f"  {'Total (with overlaps)':<30} {total_corrections:>6}")
    print(f"  {'Unique pairs to flip':<30} {unique_corrections:>6}")
    print(f"")
    print(f"Impact: {unique_corrections:,} pairs flipped from label=1 to label=0")
    print(f"  Before: {total_label_1:,} comparable / {total_label_0:,} different")
    print(f"  After:  {total_label_1 - unique_corrections:,} comparable / "
          f"{total_label_0 + unique_corrections:,} different")

    # ═══════════════════════════════════════════════════════════════════
    # APPLY CORRECTIONS
    # ═══════════════════════════════════════════════════════════════════

    if args.apply:
        print(f"\nApplying {unique_corrections} corrections...")

        # Build a lookup of corrections by index for reasoning
        correction_lookup = {}
        for pattern, items in corrections.items():
            for c in items:
                idx = c["index"]
                if idx not in correction_lookup:
                    correction_lookup[idx] = []
                correction_lookup[idx].append(f"{pattern}: {c['reason']}")

        flipped = 0
        for idx in all_correction_indices:
            rows[idx]["label"] = "0"
            reasons = correction_lookup.get(idx, [])
            rows[idx]["reasoning"] = " | ".join(reasons)
            old_source = rows[idx].get("source", "")
            if "_v11corrected" not in old_source:
                rows[idx]["source"] = old_source + "_v11corrected"
            flipped += 1

        print(f"  Flipped {flipped} labels from 1 to 0")

        print(f"\nWriting to {OUTPUT_CSV}...")
        with open(OUTPUT_CSV, "w", newline="", encoding="utf-8") as f:
            writer = csv.DictWriter(f, fieldnames=fieldnames)
            writer.writeheader()
            writer.writerows(rows)
        print(f"  Done. {len(rows):,} pairs written.")

        # Verify
        new_label_1 = sum(1 for r in rows if r["label"] == "1")
        new_label_0 = sum(1 for r in rows if r["label"] == "0")
        print(f"\n  Verification: {new_label_1:,} comparable / {new_label_0:,} different")
    else:
        print(f"\n[DRY RUN] Use --apply to write corrections to {OUTPUT_CSV}")


if __name__ == "__main__":
    main()
