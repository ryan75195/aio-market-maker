"""
V4: Two-stage taxonomy — predefined axis templates + constrained assignment.

Stage 1: LLM picks which axis types apply (from a universal catalog)
Stage 2: LLM assigns n-grams to those fixed axes (no axis invention)

This eliminates the main source of variance: whether to create an axis or not.
"""

import pyodbc
import json
import re
import os
import sys
import numpy as np
from collections import Counter, defaultdict
from openai import OpenAI

CONN_STR = (
    r"Driver={ODBC Driver 17 for SQL Server};"
    r"Server=(localdb)\MSSQLLocalDB;"
    r"Database=AIOMarketMaker;"
    r"Trusted_Connection=yes;"
)

settings_path = os.path.join(os.path.dirname(__file__), "..", "AIOMarketMaker.Console", "local.settings.json")
with open(settings_path) as f:
    settings = json.load(f)

client = OpenAI(api_key=settings["OpenAi"]["ApiKey"])
MODEL = "gpt-4.1-mini"

STOP_WORDS = {
    "the", "a", "an", "and", "or", "of", "for", "in", "on", "at", "to",
    "is", "it", "by", "as", "be", "no", "not", "so", "up", "if", "my",
    "new", "free", "with", "this", "that", "from", "was", "are", "has",
}

JOB_ID = int(sys.argv[1]) if len(sys.argv) > 1 else 1
ME_THRESHOLD = 0.05
MIN_MATCH_PCT = 0.03
MIN_ME_PAIRS = 3

# ═══════════════════════════════════════════════════════════════════
# Universal axis catalog — all possible product dimensions
# ═══════════════════════════════════════════════════════════════════
AXIS_CATALOG = [
    {"id": "model", "name": "Model/Variant", "description": "Product model, variant, or generation (e.g., Slim, Pro, Series X, Gen 3)"},
    {"id": "storage", "name": "Storage/Memory", "description": "Storage capacity or RAM (e.g., 128GB, 256GB, 1TB, 16GB RAM)"},
    {"id": "color", "name": "Color", "description": "Product color or finish (e.g., Black, White, Blue, Space Gray)"},
    {"id": "size", "name": "Size", "description": "Physical size, clothing size, or shoe size (e.g., UK 9, 32W, Medium, 14 inch)"},
    {"id": "gender", "name": "Gender/Target", "description": "Target gender (e.g., Men's, Women's, Unisex, Kids)"},
    {"id": "edition", "name": "Edition/Version", "description": "Special edition, limited run, or version (e.g., Digital Edition, Collector's, First Edition)"},
    {"id": "set_name", "name": "Set/Series Name", "description": "Named product set, series, or collection (e.g., Scarlet & Violet, Clone Wars, Retro)"},
    {"id": "product_type", "name": "Product Type", "description": "Type of product within the search (e.g., Booster Box, ETB, Single Pack, Console, Controller)"},
    {"id": "material", "name": "Material", "description": "Primary material or fabric (e.g., Leather, Canvas, Stainless Steel, Titanium)"},
    {"id": "year", "name": "Year/Era", "description": "Year of manufacture or era (e.g., 2024, 2025, Vintage, 80s)"},
    {"id": "style", "name": "Style/Design", "description": "Design style or sub-line (e.g., High Top, Low, Retro, Classic, Heritage)"},
    {"id": "connectivity", "name": "Connectivity/Interface", "description": "Network or wireless connection type (e.g., WiFi only, WiFi+Cellular, Bluetooth). Do NOT use for disc vs digital media — that belongs under Edition/Version."},
    {"id": "speed_gears", "name": "Speed/Gears", "description": "Number of speeds or gears (e.g., 3-speed, 6-speed, Single Speed)"},
    {"id": "character", "name": "Character/Theme", "description": "Specific character, franchise, or theme (e.g., Darth Vader, Pikachu, Spider-Man)"},
    {"id": "language", "name": "Language/Region", "description": "Language or regional variant (e.g., Japanese, English, Korean, UK)"},
    {"id": "fit", "name": "Fit/Cut", "description": "Clothing fit or cut (e.g., Slim Fit, Regular, Relaxed, Bootcut)"},
]


def load_listings(job_id):
    conn = pyodbc.connect(CONN_STR)
    cursor = conn.cursor()
    cursor.execute(
        "SELECT Title, Price, ListingStatus FROM Listings WHERE ScrapeJobId = ? AND Title IS NOT NULL",
        job_id,
    )
    listings = [
        {"title": row.Title, "price": float(row.Price) if row.Price else None, "status": row.ListingStatus}
        for row in cursor.fetchall()
    ]
    conn.close()
    return listings


def extract_ngrams(listings):
    n = len(listings)
    min_uni_freq = max(20, n // 200)
    min_bi_freq = max(10, n // 200)
    unigrams = Counter()
    bigrams = Counter()
    trigrams = Counter()
    for listing in listings:
        words = re.findall(r'\b\w+\b', listing["title"].lower())
        words = [w for w in words if w not in STOP_WORDS and len(w) > 1]
        for w in words:
            unigrams[w] += 1
        for i in range(len(words) - 1):
            bigrams[f"{words[i]} {words[i+1]}"] += 1
        for i in range(len(words) - 2):
            trigrams[f"{words[i]} {words[i+1]} {words[i+2]}"] += 1
    ngrams = {}
    for ng, freq in unigrams.most_common():
        if freq >= min_uni_freq:
            ngrams[ng] = freq
    for ng, freq in bigrams.most_common():
        if freq >= min_bi_freq:
            ngrams[ng] = freq
    for ng, freq in trigrams.most_common():
        if freq >= min_bi_freq:
            ngrams[ng] = freq
    return ngrams


def dedup_ngrams(ngrams, equiv_threshold=0.95):
    texts = list(ngrams.keys())
    if not texts:
        return {}
    all_vectors = []
    for i in range(0, len(texts), 2048):
        batch = texts[i:i + 2048]
        response = client.embeddings.create(model="text-embedding-3-large", input=batch, dimensions=256)
        all_vectors.extend([d.embedding for d in response.data])
    vectors = np.array(all_vectors)
    norms = np.linalg.norm(vectors, axis=1, keepdims=True)
    normed = vectors / norms
    sim_matrix = normed @ normed.T
    parent = list(range(len(texts)))
    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x
    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[ra] = rb
    for i in range(len(texts)):
        for j in range(i + 1, len(texts)):
            if sim_matrix[i, j] >= equiv_threshold:
                union(i, j)
    groups = defaultdict(list)
    for i in range(len(texts)):
        groups[find(i)].append(i)
    deduped = {}
    for group_indices in groups.values():
        members = [(texts[i], ngrams[texts[i]]) for i in group_indices]
        members.sort(key=lambda x: -x[1])
        canonical = members[0][0]
        deduped[canonical] = {"freq": sum(f for _, f in members), "forms": [m[0] for m in members]}
    return deduped


def compute_match_sets(listings, deduped):
    match_sets = {}
    for canonical, info in deduped.items():
        patterns = [p.lower() for p in info["forms"]]
        matching = set()
        for i, listing in enumerate(listings):
            title_lower = listing["title"].lower()
            if any(p in title_lower for p in patterns):
                matching.add(i)
        match_sets[canonical] = matching
    return match_sets


def filter_significant_ngrams(deduped, match_sets, total_listings):
    min_matches = int(total_listings * MIN_MATCH_PCT)
    return {k: v for k, v in deduped.items() if len(match_sets[k]) >= min_matches}


# ═══════════════════════════════════════════════════════════════════
# Stage 1: Select applicable axes for this product category
# ═══════════════════════════════════════════════════════════════════
def select_axes(search_term, sample_titles):
    """LLM picks which axis types apply to this product category."""

    catalog_lines = []
    for ax in AXIS_CATALOG:
        catalog_lines.append(f"  - {ax['id']}: {ax['name']} -- {ax['description']}")
    catalog_block = "\n".join(catalog_lines)

    sample_block = "\n".join(f"  - {t}" for t in sample_titles[:20])

    response = client.chat.completions.create(
        model=MODEL,
        messages=[
            {"role": "system", "content": f"""You are classifying an eBay product category to determine which taxonomy axes apply.

Given a search term and sample listing titles, select 3-6 axes from the catalog that are relevant for distinguishing product variants.

RULES:
- Only select axes that would have 2+ distinct values in this category
- Select axes that help differentiate products for pricing (e.g., a PS5 Slim costs different from a PS5 Pro)
- Do NOT select axes that are irrelevant (e.g., "Speed/Gears" for electronics)
- For each selected axis, briefly note what values you expect"""},
            {"role": "user", "content": f"""Search term: "{search_term}"

Sample titles:
{sample_block}

Available axes:
{catalog_block}

Select 3-6 axes that apply to this product category."""},
        ],
        response_format={
            "type": "json_schema",
            "json_schema": {
                "name": "axis_selection",
                "strict": True,
                "schema": {
                    "type": "object",
                    "properties": {
                        "selected_axes": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "axis_id": {"type": "string", "description": "ID from the catalog"},
                                    "expected_values": {"type": "string", "description": "Brief note on expected values"},
                                },
                                "required": ["axis_id", "expected_values"],
                                "additionalProperties": False,
                            },
                        }
                    },
                    "required": ["selected_axes"],
                    "additionalProperties": False,
                },
            },
        },
        temperature=0.0,
    )

    result = json.loads(response.choices[0].message.content)
    selected_ids = [s["axis_id"] for s in result["selected_axes"]]

    # Map to full axis info
    catalog_map = {ax["id"]: ax for ax in AXIS_CATALOG}
    selected = []
    for s in result["selected_axes"]:
        if s["axis_id"] in catalog_map:
            selected.append({
                "id": s["axis_id"],
                "name": catalog_map[s["axis_id"]]["name"],
                "expected_values": s["expected_values"],
            })
    return selected


# ═══════════════════════════════════════════════════════════════════
# Stage 2: Assign n-grams to the predefined axes
# ═══════════════════════════════════════════════════════════════════
def assign_ngrams_to_axes(selected_axes, significant, match_sets, total_listings, search_term):
    """LLM assigns n-grams to the predefined axes. No axis invention allowed."""

    # Build n-gram lines
    ngram_lines = []
    for canonical in sorted(significant.keys(), key=lambda c: -len(match_sets[c])):
        info = significant[canonical]
        match_count = len(match_sets[canonical])
        pct = 100 * match_count / total_listings
        forms_str = ""
        if len(info["forms"]) > 1:
            forms_str = f" (also: {', '.join(info['forms'][1:])})"
        ngram_lines.append(f"  {canonical}: {match_count} matches ({pct:.1f}%){forms_str}")

    # Build axis definitions
    axis_lines = []
    for ax in selected_axes:
        axis_lines.append(f"  - {ax['name']} (expected: {ax['expected_values']})")

    response = client.chat.completions.create(
        model=MODEL,
        messages=[
            {"role": "system", "content": f"""You are assigning n-grams to predefined taxonomy axes for "{search_term}" ({total_listings} listings).

The axes have been pre-selected for this product category. Your job is ONLY to assign n-grams to these axes.

RULES:
- ONLY use the axes listed below. Do NOT create new axes.
- Each n-gram goes to AT MOST one axis.
- Skip n-grams that don't fit any axis (brand names, generic words, seller phrases).
- Each axis should have 2-15 values.
- Pick SHORT n-grams (1-2 words) that uniquely identify each value.
- Values within an axis must be mutually exclusive (a listing has only one value per axis).
- NEVER create catch-all values that match most listings."""},
            {"role": "user", "content": f"""Predefined axes for this category:
{chr(10).join(axis_lines)}

N-grams to assign (sorted by match count):
{chr(10).join(ngram_lines)}

Assign each relevant n-gram to one of the predefined axes."""},
        ],
        response_format={
            "type": "json_schema",
            "json_schema": {
                "name": "axis_assignment",
                "strict": True,
                "schema": {
                    "type": "object",
                    "properties": {
                        "axes": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "name": {"type": "string"},
                                    "values": {
                                        "type": "array",
                                        "items": {
                                            "type": "object",
                                            "properties": {
                                                "label": {"type": "string"},
                                                "ngrams": {"type": "array", "items": {"type": "string"}},
                                            },
                                            "required": ["label", "ngrams"],
                                            "additionalProperties": False,
                                        },
                                    },
                                },
                                "required": ["name", "values"],
                                "additionalProperties": False,
                            },
                        }
                    },
                    "required": ["axes"],
                    "additionalProperties": False,
                },
            },
        },
        temperature=0.0,
        max_completion_tokens=4096,
    )

    finish_reason = response.choices[0].finish_reason
    content = response.choices[0].message.content
    if finish_reason != "stop":
        raise RuntimeError(f"LLM response truncated (finish_reason={finish_reason})")

    return json.loads(content)["axes"]


# ═══════════════════════════════════════════════════════════════════
# Post-processing (same as v3)
# ═══════════════════════════════════════════════════════════════════
def pattern_matches(pattern, title_lower):
    if " " not in pattern:
        return bool(re.search(r'\b' + re.escape(pattern) + r'\b', title_lower))
    return pattern in title_lower


def compute_value_match_set(value_patterns, listings):
    matches = set()
    for i, listing in enumerate(listings):
        title_lower = listing["title"].lower()
        if any(pattern_matches(p, title_lower) for p in value_patterns):
            matches.add(i)
    return matches


def prune_overlapping_values(axes, significant, listings):
    pruned_axes = []
    for axis in axes:
        value_data = []
        for value in axis["values"]:
            patterns = []
            for ng in value["ngrams"]:
                if ng in significant:
                    patterns.extend([f.lower() for f in significant[ng]["forms"]])
                else:
                    patterns.append(ng.lower())
            patterns = [p for p in patterns if len(p) >= 2 and not (p.isdigit() and len(p) < 3)]
            match_set = compute_value_match_set(patterns, listings)
            value_data.append({"label": value["label"], "ngrams": value["ngrams"], "patterns": patterns, "match_set": match_set})

        value_data = [v for v in value_data if len(v["match_set"]) >= 10]

        remaining = list(value_data)
        while True:
            overlap_counts = {i: 0 for i in range(len(remaining))}
            overlap_pairs = []
            for i in range(len(remaining)):
                for j in range(i + 1, len(remaining)):
                    a = remaining[i]["match_set"]
                    b = remaining[j]["match_set"]
                    if len(a) == 0 or len(b) == 0:
                        continue
                    ov = len(a & b) / min(len(a), len(b))
                    if ov > 0.20:
                        overlap_counts[i] += 1
                        overlap_counts[j] += 1
                        overlap_pairs.append((i, j, ov))
            if not overlap_pairs:
                break
            worst_idx = max(overlap_counts.keys(), key=lambda i: (overlap_counts[i], -len(remaining[i]["match_set"])))
            if overlap_counts[worst_idx] == 0:
                break
            removed = remaining.pop(worst_idx)
            print(f"    Pruned '{removed['label']}' from {axis['name']}")

        if len(remaining) >= 2:
            pruned_axes.append({
                "name": axis["name"],
                "values": [{"label": v["label"], "ngrams": v["ngrams"]} for v in remaining],
            })
    return pruned_axes


def enforce_me(axes, significant, listings):
    """Drop axes where >40% of value pairs violate ME.

    After pruning, remaining violations should be minimal.
    Relaxed from 30% to 40% to allow axes with many values where
    a few borderline pairs exist (e.g., Pokemon product types).
    """
    clean = []
    for axis in axes:
        axis_values = []
        for value in axis["values"]:
            patterns = []
            for ng in value["ngrams"]:
                if ng in significant:
                    patterns.extend([f.lower() for f in significant[ng]["forms"]])
                else:
                    patterns.append(ng.lower())
            match_set = compute_value_match_set(patterns, listings)
            axis_values.append({"match_set": match_set})

        violations = 0
        total_pairs = 0
        for i in range(len(axis_values)):
            for j in range(i + 1, len(axis_values)):
                a = axis_values[i]["match_set"]
                b = axis_values[j]["match_set"]
                if len(a) == 0 or len(b) == 0:
                    continue
                total_pairs += 1
                if len(a & b) / min(len(a), len(b)) >= ME_THRESHOLD:
                    violations += 1

        if total_pairs == 0 or violations / total_pairs <= 0.40:
            clean.append(axis)
        else:
            print(f"    Dropped axis '{axis['name']}' ({violations}/{total_pairs} ME violations)")
    return clean


def assign_and_measure(listings, axes, significant):
    axis_matchers = []
    for axis in axes:
        matchers = []
        for value in axis["values"]:
            patterns = []
            for ng in value["ngrams"]:
                if ng in significant:
                    patterns.extend(significant[ng]["forms"])
                else:
                    patterns.append(ng)
            clean_pats = [p.lower() for p in patterns if len(p) >= 2 and not (p.isdigit() and len(p) < 3)]
            matchers.append({"label": value["label"], "patterns": clean_pats})
        axis_matchers.append({"name": axis["name"], "values": matchers})

    conflict_count = 0
    labeled_count = 0
    category_counter = Counter()

    for listing in listings:
        title_lower = listing["title"].lower()
        axis_values = {}
        has_conflict = False

        for axis in axis_matchers:
            matched_values = []
            for value in axis["values"]:
                if any(pattern_matches(p, title_lower) for p in value["patterns"]):
                    matched_values.append(value["label"])

            if len(matched_values) > 1:
                resolved = []
                for value in axis["values"]:
                    value_pats = [p for p in value["patterns"] if pattern_matches(p, title_lower)]
                    if not value_pats:
                        continue
                    is_subsumed = False
                    for other_value in axis["values"]:
                        if other_value["label"] == value["label"]:
                            continue
                        other_pats = [p for p in other_value["patterns"] if pattern_matches(p, title_lower)]
                        if other_pats and all(any(vp in op and vp != op for op in other_pats) for vp in value_pats):
                            is_subsumed = True
                            break
                    if not is_subsumed:
                        resolved.append(value["label"])
                matched_values = resolved

            if len(matched_values) == 1:
                axis_values[axis["name"]] = matched_values[0]
            elif len(matched_values) > 1:
                has_conflict = True

        if has_conflict:
            conflict_count += 1

        parts = [axis_values.get(ax["name"]) for ax in axis_matchers if axis_values.get(ax["name"])]
        category = " | ".join(parts) if parts else "Unlabeled"
        if category != "Unlabeled":
            labeled_count += 1
        category_counter[category] += 1

    total = len(listings)
    coverage_pct = round(100 * labeled_count / total, 1) if total > 0 else 0
    conflict_pct = round(100 * conflict_count / total, 1) if total > 0 else 0
    num_categories = len([c for c in category_counter if c != "Unlabeled"])

    return {
        "coverage_pct": coverage_pct,
        "conflict_pct": conflict_pct,
        "num_categories": num_categories,
        "category_counter": category_counter,
        "axis_matchers": axis_matchers,
    }


def main():
    search_terms = {
        1: "PlayStation 5 Console",
        1029: "Pokemon Booster Box Sealed",
        1121: "Brompton Folding Bike",
        1042: "Nike Air Jordan 1",
        1045: "LEGO Star Wars Set",
        1020: "iPhone 15 Pro Max",
        1086: "Vintage Levis 501 Jeans",
    }
    search_term = search_terms.get(JOB_ID, f"Job {JOB_ID}")

    print(f"=== Taxonomy V4: {search_term} (job {JOB_ID}) ===\n")

    print("Loading listings...")
    listings = load_listings(JOB_ID)
    total = len(listings)
    print(f"  {total} listings\n")

    # ── Stage 1: Select axes ────────────────────────────────────────
    print("Stage 1: Selecting axes from catalog...")
    sample_titles = [l["title"] for l in listings[:30]]
    selected_axes = select_axes(search_term, sample_titles)
    print(f"  Selected {len(selected_axes)} axes:")
    for ax in selected_axes:
        print(f"    {ax['name']} (expected: {ax['expected_values']})")

    # ── Deterministic pre-processing ────────────────────────────────
    print("\nExtracting n-grams...")
    ngrams = extract_ngrams(listings)
    print(f"  {len(ngrams)} raw n-grams")

    print("Deduplicating with embeddings (0.95)...")
    deduped = dedup_ngrams(ngrams, 0.95)
    print(f"  {len(deduped)} after dedup")

    print("Computing match sets...")
    match_sets = compute_match_sets(listings, deduped)

    print(f"Filtering to significant (>{MIN_MATCH_PCT:.0%})...")
    significant = filter_significant_ngrams(deduped, match_sets, total)
    print(f"  {len(significant)} significant n-grams\n")

    # ── Stage 2: Assign n-grams to axes ────────────────────────────
    print("Stage 2: Assigning n-grams to predefined axes...")
    for attempt in range(3):
        try:
            axes = assign_ngrams_to_axes(selected_axes, significant, match_sets, total, search_term)
            break
        except RuntimeError as e:
            if "truncated" in str(e) and attempt < 2:
                print(f"  Retry {attempt + 2}/3...")
                continue
            raise

    print(f"\n  LLM returned {len(axes)} axes:")
    for ax in axes:
        vals = [f"{v['label']}({','.join(v['ngrams'])})" for v in ax["values"]]
        print(f"    {ax['name']}: [{', '.join(vals)}]")

    # ── Post-processing ─────────────────────────────────────────────
    print(f"\nPruning overlapping values...")
    axes = prune_overlapping_values(axes, significant, listings)

    print(f"Enforcing ME...")
    axes = enforce_me(axes, significant, listings)

    print(f"\n  {len(axes)} axes after post-processing:")
    for ax in axes:
        vals = [f"{v['label']}({','.join(v['ngrams'])})" for v in ax["values"]]
        print(f"    {ax['name']}: [{', '.join(vals)}]")

    # ── Assign and measure ──────────────────────────────────────────
    print(f"\nAssigning listings...")
    metrics = assign_and_measure(listings, axes, significant)

    print(f"\n{'='*60}")
    print(f"RESULTS: {search_term}")
    print(f"{'='*60}")
    print(f"  Coverage:   {metrics['coverage_pct']}%")
    print(f"  Conflicts:  {metrics['conflict_pct']}%")
    print(f"  Categories: {metrics['num_categories']}")
    print(f"  Axes:       {len(axes)}")

    print(f"\n  Top 20 categories:")
    cats = [(k, v) for k, v in metrics['category_counter'].items() if k != "Unlabeled"]
    cats.sort(key=lambda x: -x[1])
    for cat, count in cats[:20]:
        print(f"    {cat}: {count}")

    unlabeled = metrics['category_counter'].get("Unlabeled", 0)
    print(f"\n  Unlabeled: {unlabeled}")

    print(f"\n  Axes detail:")
    for ax in metrics['axis_matchers']:
        print(f"    {ax['name']}:")
        for val in ax['values']:
            count = sum(1 for l in listings if any(pattern_matches(p, l["title"].lower()) for p in val["patterns"]))
            print(f"      {val['label']}: {count} matches (patterns: {val['patterns']})")


if __name__ == "__main__":
    main()
