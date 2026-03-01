"""
Scan 300 random label=1 pairs from v11 that are NOT caught by the existing
audit patterns. Look for additional error patterns we could add.

Outputs pairs grouped by potential issue type for manual review.
"""

import csv
import re
import random
import sys
import io
from collections import defaultdict, Counter
from pathlib import Path

csv.field_size_limit(10_000_000)

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

DATA_DIR = Path(__file__).parent.parent / "data"
V11_CSV = DATA_DIR / "labeled_pairs_v11.csv"

# ── Inline copies of existing audit patterns (avoids import side-effects) ──

_NEW = re.compile(r'\b(?:sealed|bnib|brand\s*new|factory\s*sealed|unopened|new\s*sealed|mint\s*sealed)\b', re.I)
_TIER_A = re.compile(r'\b(?:grade\s*a\+?|pristine|mint\s*condition|excellent|like\s*new|near\s*mint|immaculate)\b', re.I)
_TIER_B = re.compile(r'\b(?:grade\s*b|good\s*condition|very\s*good|great\s*condition|gently\s*used|lightly\s*used|pre[\s-]?owned)\b', re.I)
_TIER_C = re.compile(r'\b(?:grade\s*c|fair\s*condition|scratches|scuffs|dents|cosmetic\s*damage|well\s*used|heavily\s*used|worn)\b', re.I)
_PARTS = re.compile(r'\b(?:for\s*parts|spares|faulty|broken|not\s*working|damaged|as[\s-]is|defective|dead|cracked\s*screen|needs\s*repair)\b', re.I)

def extract_condition_tier(title, desc=""):
    text = f"{title} {desc[:200]}"
    for tier, pat in [(4, _PARTS), (3, _TIER_C), (2, _TIER_B), (1, _TIER_A), (0, _NEW)]:
        m = pat.search(text)
        if m:
            return (tier, "", m.group(0))
    return None

_ACCESSORY = re.compile(
    r'\b(?:replacement\s+(?:cord|cable|charger|battery|strap|band|barrel|brush|filter|blade|nozzle|head|tip|pad|roller)'
    r'|spare\s+(?:battery|charger|cable|strap|band|part|wheel)'
    r'|(?:cord|cable|charger|strap|band|barrel|brush|filter|blade|nozzle)\s+only'
    r'|disc\s*drive\s*(?:only|unit|attachment)'
    r'|power\s*(?:cord|cable|adapter)\s+(?:only|replacement|for\b)'
    r'|(?:just|only)\s+the\s+(?:cord|cable|charger|remote|controller|strap|band|base|dock|stand))\b', re.I)

def is_accessory_vs_product(title_a, desc_a, title_b, desc_b):
    a = bool(_ACCESSORY.search(title_a))
    b = bool(_ACCESSORY.search(title_b))
    if a and b: return False, ""
    if a and not b: return True, ""
    if b and not a: return True, ""
    return False, ""

def check_product_tier(title_a, title_b):
    a_u = bool(re.search(r'\bultra[\s-]?premium', title_a, re.I))
    b_u = bool(re.search(r'\bultra[\s-]?premium', title_b, re.I))
    a_p = bool(re.search(r'\bpremium\s+collection\b', title_a, re.I))
    b_p = bool(re.search(r'\bpremium\s+collection\b', title_b, re.I))
    if (a_u and not b_u and b_p and not a_p) or (b_u and not a_u and a_p and not b_p):
        return True, ""
    a_d = bool(re.search(r'\bdigital\s+edition\b', title_a, re.I))
    b_d = bool(re.search(r'\bdigital\s+edition\b', title_b, re.I))
    a_disc = bool(re.search(r'\bdisc\s+(?:edition|version)\b', title_a, re.I))
    b_disc = bool(re.search(r'\bdisc\s+(?:edition|version)\b', title_b, re.I))
    if (a_d and b_disc) or (a_disc and b_d): return True, ""
    return False, ""

_BUNDLE = re.compile(r'\b(?:joblot|job\s*lot|bundle\s*of\s*\d+|lot\s*of\s*\d+)\b', re.I)
_BUNDLE_COUNT = re.compile(r'\b(\d+)\s*(?:pack(?!s?\s*-)|pcs|pieces|items|units)\b', re.I)
_BUNDLE_NX = re.compile(r'(?<![a-zA-Z\d])([2-9]\d{0,2})\s*x\s+(?![\d.]+\b)(?!(?:M\.?\d|speed|usb|hdmi|pcie|sata|nvme|ddr|ssd|hdd|gpu|cpu|ram|dimm)\b)', re.I)

def _extract_bundle(title):
    m = _BUNDLE.search(title)
    if m: return True
    m = _BUNDLE_COUNT.search(title)
    if m and int(m.group(1)) > 1: return True
    m = _BUNDLE_NX.search(title)
    if m: return True
    return False

def check_quantity_mismatch(title_a, title_b):
    a = _extract_bundle(title_a)
    b = _extract_bundle(title_b)
    if a and b: return False, ""
    if a and not b: return True, ""
    if b and not a: return True, ""
    return False, ""


# ── NEW PATTERN CANDIDATES ──────────────────────────────────────────────

def check_storage_mismatch(title_a, title_b):
    """Different storage capacity: 128GB vs 256GB vs 512GB vs 1TB."""
    storage = re.compile(r'\b(\d+)\s*(?:GB|TB)\b', re.I)
    a_sizes = set(m.group(0).upper() for m in storage.finditer(title_a))
    b_sizes = set(m.group(0).upper() for m in storage.finditer(title_b))
    # Only flag if both have storage specs and they differ
    if a_sizes and b_sizes and a_sizes != b_sizes:
        # Exclude RAM-only specs (e.g., "16GB RAM" — that's not storage)
        # Heuristic: if the number is small (4, 8, 16, 32) and title contains RAM/DDR, skip
        a_storage = {s for s in a_sizes if not re.search(r'\b(?:RAM|DDR)\b', title_a, re.I) or int(re.match(r'\d+', s).group()) >= 64}
        b_storage = {s for s in b_sizes if not re.search(r'\b(?:RAM|DDR)\b', title_b, re.I) or int(re.match(r'\d+', s).group()) >= 64}
        if a_storage and b_storage and a_storage != b_storage:
            return True, f"Storage: {a_storage} vs {b_storage}"
    return False, ""


def check_color_variant(title_a, title_b):
    """Different color variants that may affect price (watches, phones)."""
    colors = re.compile(
        r'\b(?:black|white|silver|gold|rose\s*gold|blue|red|green|pink|purple'
        r'|graphite|midnight|starlight|sierra\s*blue|alpine\s*green'
        r'|space\s*(?:gray|grey|black)|natural\s*titanium|desert\s*titanium'
        r'|cream|yellow|orange|burgundy|copper|nickel)\b',
        re.I,
    )
    a_colors = set(m.group(0).lower() for m in colors.finditer(title_a))
    b_colors = set(m.group(0).lower() for m in colors.finditer(title_b))
    if a_colors and b_colors and a_colors != b_colors and not a_colors.intersection(b_colors):
        return True, f"Color: {a_colors} vs {b_colors}"
    return False, ""


def check_size_variant(title_a, title_b):
    """Different sizes: clothing (S/M/L/XL), watch (40mm/44mm), display (13"/14"/16")."""
    # Watch sizes
    watch_mm = re.compile(r'\b(\d{2})mm\b', re.I)
    a_mm = set(m.group(1) for m in watch_mm.finditer(title_a))
    b_mm = set(m.group(1) for m in watch_mm.finditer(title_b))
    if a_mm and b_mm and a_mm != b_mm:
        return True, f"Size (mm): {a_mm} vs {b_mm}"

    # Display sizes (13", 14", 16")
    display = re.compile(r'\b(\d{2}(?:\.\d)?)["\u201d\u2033]?\s*(?:inch|in\b)', re.I)
    a_disp = set(m.group(1) for m in display.finditer(title_a))
    b_disp = set(m.group(1) for m in display.finditer(title_b))
    if a_disp and b_disp and a_disp != b_disp:
        return True, f"Display: {a_disp}\" vs {b_disp}\""

    return False, ""


def check_model_number_mismatch(title_a, title_b):
    """Different model numbers/generations that indicate different products."""
    # iPhone generation
    iphone = re.compile(r'iPhone\s*(\d{1,2})\b', re.I)
    a_iphone = set(m.group(1) for m in iphone.finditer(title_a))
    b_iphone = set(m.group(1) for m in iphone.finditer(title_b))
    if a_iphone and b_iphone and a_iphone != b_iphone:
        return True, f"iPhone gen: {a_iphone} vs {b_iphone}"

    # iPad generation / model
    ipad_gen = re.compile(r'iPad\s*(?:Pro|Air|Mini)?\s*(\d{1,2})', re.I)
    a_ipad = set(m.group(0) for m in ipad_gen.finditer(title_a))
    b_ipad = set(m.group(0) for m in ipad_gen.finditer(title_b))
    if a_ipad and b_ipad and a_ipad != b_ipad:
        return True, f"iPad: {a_ipad} vs {b_ipad}"

    # Galaxy S/Tab generation
    galaxy = re.compile(r'(?:Galaxy\s*(?:S|Tab\s*S?))\s*(\d{1,2})', re.I)
    a_galaxy = set(m.group(0) for m in galaxy.finditer(title_a))
    b_galaxy = set(m.group(0) for m in galaxy.finditer(title_b))
    if a_galaxy and b_galaxy and a_galaxy != b_galaxy:
        return True, f"Galaxy: {a_galaxy} vs {b_galaxy}"

    # PS5 Slim vs PS5 (original)
    a_slim = bool(re.search(r'\bslim\b', title_a, re.I))
    b_slim = bool(re.search(r'\bslim\b', title_b, re.I))
    a_ps5 = bool(re.search(r'\b(?:PS5|PlayStation\s*5)\b', title_a, re.I))
    b_ps5 = bool(re.search(r'\b(?:PS5|PlayStation\s*5)\b', title_b, re.I))
    if a_ps5 and b_ps5 and a_slim != b_slim:
        return True, f"PS5 Slim vs PS5 original"

    return False, ""


def check_language_edition(title_a, title_b):
    """Different language editions (common in Pokemon/TCG)."""
    langs = re.compile(
        r'\b(?:Japanese|Korean|English|Spanish|French|German|Italian'
        r'|Portuguese|Chinese|Thai|Indonesian)\b',
        re.I,
    )
    a_langs = set(m.group(0).lower() for m in langs.finditer(title_a))
    b_langs = set(m.group(0).lower() for m in langs.finditer(title_b))
    if a_langs and b_langs and a_langs != b_langs:
        return True, f"Language: {a_langs} vs {b_langs}"
    return False, ""


def check_different_product_in_same_category(title_a, title_b):
    """Detect totally different products matched just because they share a search term.
    E.g., DeWalt drill vs DeWalt impact driver, or two different LEGO sets."""
    # LEGO set numbers
    lego = re.compile(r'\b(\d{4,5})\b')
    if re.search(r'lego', title_a, re.I) and re.search(r'lego', title_b, re.I):
        a_nums = set(m.group(1) for m in lego.finditer(title_a))
        b_nums = set(m.group(1) for m in lego.finditer(title_b))
        if a_nums and b_nums and not a_nums.intersection(b_nums):
            return True, f"Different LEGO sets: {a_nums} vs {b_nums}"

    # DeWalt model numbers (DCDxxx vs DCFxxx = different tools)
    dewalt = re.compile(r'\b(DC[A-Z]\d{3})', re.I)
    a_dw = set(m.group(1).upper() for m in dewalt.finditer(title_a))
    b_dw = set(m.group(1).upper() for m in dewalt.finditer(title_b))
    if a_dw and b_dw and not a_dw.intersection(b_dw):
        return True, f"Different DeWalt models: {a_dw} vs {b_dw}"

    return False, ""


def check_cpu_mismatch(title_a, title_b):
    """Different CPU: i5 vs i7, M3 vs M3 Pro vs M3 Max."""
    # Intel i-series
    intel = re.compile(r'\bi([3579])\b', re.I)
    a_intel = set(m.group(0).lower() for m in intel.finditer(title_a))
    b_intel = set(m.group(0).lower() for m in intel.finditer(title_b))
    if a_intel and b_intel and a_intel != b_intel:
        return True, f"CPU: {a_intel} vs {b_intel}"

    # Apple M-series
    apple_m = re.compile(r'\b(M[1-4](?:\s*(?:Pro|Max|Ultra))?)\b', re.I)
    a_m = set(m.group(1).lower() for m in apple_m.finditer(title_a))
    b_m = set(m.group(1).lower() for m in apple_m.finditer(title_b))
    if a_m and b_m and a_m != b_m:
        return True, f"Apple chip: {a_m} vs {b_m}"

    return False, ""


def main():
    print(f"Loading {V11_CSV}...")
    with open(V11_CSV, newline="", encoding="utf-8", errors="replace") as f:
        reader = csv.DictReader(f)
        rows = list(reader)

    # Get label=1 pairs NOT caught by existing audit
    uncaught = []
    for row in rows:
        if row["label"] != "1":
            continue
        title_a = row.get("anchor_title", "")
        title_b = row.get("neighbor_title", "")
        desc_a = row.get("anchor_desc", "")
        desc_b = row.get("neighbor_desc", "")

        # Skip if already caught by existing patterns
        tier_a = extract_condition_tier(title_a, desc_a)
        tier_b = extract_condition_tier(title_b, desc_b)
        if tier_a and tier_b and abs(tier_a[0] - tier_b[0]) >= 2:
            continue
        is_acc, _ = is_accessory_vs_product(title_a, desc_a, title_b, desc_b)
        if is_acc:
            continue
        is_tier, _ = check_product_tier(title_a, title_b)
        if is_tier:
            continue
        is_qty, _ = check_quantity_mismatch(title_a, title_b)
        if is_qty:
            continue

        uncaught.append(row)

    print(f"  {len(uncaught):,} label=1 pairs NOT caught by existing audit")

    # Scan ALL uncaught pairs (not just 300) for full counts
    sample = uncaught

    # Run new pattern candidates
    new_patterns = {
        "storage_mismatch": check_storage_mismatch,
        "color_variant": check_color_variant,
        "size_variant": check_size_variant,
        "model_number": check_model_number_mismatch,
        "language_edition": check_language_edition,
        "different_product": check_different_product_in_same_category,
        "cpu_mismatch": check_cpu_mismatch,
    }

    results = defaultdict(list)
    clean = []

    for row in sample:
        title_a = row.get("anchor_title", "")
        title_b = row.get("neighbor_title", "")
        desc_a = row.get("anchor_desc", "")
        desc_b = row.get("neighbor_desc", "")
        cat = row.get("product_name", "")

        found = False
        for pattern_name, check_fn in new_patterns.items():
            if pattern_name in ("storage_mismatch", "cpu_mismatch", "color_variant", "size_variant"):
                flagged, reason = check_fn(title_a, title_b)
            elif pattern_name in ("language_edition",):
                flagged, reason = check_fn(title_a, title_b)
            elif pattern_name == "different_product":
                flagged, reason = check_fn(title_a, title_b)
            else:
                flagged, reason = check_fn(title_a, title_b)

            if flagged:
                results[pattern_name].append({
                    "cat": cat,
                    "reason": reason,
                    "title_a": title_a[:100],
                    "title_b": title_b[:100],
                })
                found = True

        if not found:
            clean.append({
                "cat": cat,
                "title_a": title_a[:100],
                "title_b": title_b[:100],
            })

    # Report
    print(f"\n{'='*70}")
    print(f"NEW PATTERN SCAN — {len(sample)} uncaught label=1 pairs")
    print(f"{'='*70}")

    for pattern_name, items in sorted(results.items(), key=lambda x: -len(x[1])):
        print(f"\n{'─'*70}")
        print(f"CANDIDATE: {pattern_name} — {len(items)} hits")
        print(f"{'─'*70}")
        by_cat = Counter(c["cat"] for c in items)
        print(f"  By category: {dict(by_cat.most_common(10))}")
        for item in items[:8]:
            print(f"    [{item['cat']}] {item['reason']}")
            print(f"      A: {item['title_a']}")
            print(f"      B: {item['title_b']}")
            print()

    print(f"\n{'─'*70}")
    print(f"CLEAN (no new patterns detected): {len(clean)} pairs")
    print(f"{'─'*70}")
    # Show 15 random clean pairs for manual review
    random.seed(42)
    clean_sample = random.sample(clean, min(15, len(clean)))
    for item in clean_sample:
        print(f"  [{item['cat']}]")
        print(f"    A: {item['title_a']}")
        print(f"    B: {item['title_b']}")
        print()


if __name__ == "__main__":
    main()
