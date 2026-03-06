"""
Taxonomy V2: N-gram extraction -> Embedding dedup -> LLM axis grouping -> Substring matching

Pipeline:
1. Extract n-grams from all titles (algorithmic)
2. Embed n-grams, merge semantic equivalents (embedding, no LLM)
3. LLM groups deduplicated n-grams into axes (one LLM call)
4. Match listings via substring on n-gram groups (algorithmic)
5. Compute stats per category
6. Output JSON for UI
"""

import pyodbc
import json
import re
import os
import time
import numpy as np
from collections import Counter, defaultdict
from openai import OpenAI

# ── Config ──────────────────────────────────────────────────────────────────

CONN_STR = (
    r"Driver={ODBC Driver 17 for SQL Server};"
    r"Server=(localdb)\MSSQLLocalDB;"
    r"Database=AIOMarketMaker;"
    r"Trusted_Connection=yes;"
)
import sys
JOB_ID = int(sys.argv[1]) if len(sys.argv) > 1 else 1
MIN_NGRAM_FREQ = 20
EQUIV_THRESHOLD = 0.95  # Cosine similarity to merge n-grams (0.90 merges 1tb/2tb)
OUTPUT_FILE = f"experiments/taxonomy-job{JOB_ID}.json"

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


# ── Step 1: Load listings ──────────────────────────────────────────────────

def load_listings():
    print(f"Loading listings for job {JOB_ID}...")
    conn = pyodbc.connect(CONN_STR)
    cursor = conn.cursor()
    cursor.execute("""
        SELECT l.Title, l.Price, l.ListingStatus, l.Condition, l.ListingId
        FROM Listings l
        WHERE l.ScrapeJobId = ? AND l.Title IS NOT NULL
    """, JOB_ID)
    listings = []
    for row in cursor.fetchall():
        listings.append({
            "title": row.Title,
            "price": float(row.Price) if row.Price else None,
            "status": row.ListingStatus,
            "condition": row.Condition,
            "id": str(row.ListingId),
        })
    conn.close()

    # Also get search term
    conn2 = pyodbc.connect(CONN_STR)
    cursor2 = conn2.cursor()
    cursor2.execute("SELECT SearchTerm FROM ScrapeJobs WHERE Id = ?", JOB_ID)
    search_term = cursor2.fetchone().SearchTerm
    conn2.close()

    print(f"  Loaded {len(listings)} listings for '{search_term}'")
    return listings, search_term


# ── Step 2: Extract n-grams ────────────────────────────────────────────────

MIN_UNIGRAM_FREQ = 50  # Higher threshold for unigrams (noisier)

def extract_ngrams(listings):
    print("Extracting n-grams (n=1,2,3)...")
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
        if freq >= MIN_UNIGRAM_FREQ:
            ngrams[ng] = freq
    for ng, freq in bigrams.most_common():
        if freq >= MIN_NGRAM_FREQ:
            ngrams[ng] = freq
    for ng, freq in trigrams.most_common():
        if freq >= MIN_NGRAM_FREQ:
            ngrams[ng] = freq

    uni_count = sum(1 for ng in ngrams if ' ' not in ng)
    bi_count = sum(1 for ng in ngrams if ng.count(' ') == 1)
    tri_count = sum(1 for ng in ngrams if ng.count(' ') == 2)
    print(f"  Found {len(ngrams)} significant n-grams: {uni_count} unigrams (>={MIN_UNIGRAM_FREQ}), {bi_count} bigrams, {tri_count} trigrams (>={MIN_NGRAM_FREQ})")
    return ngrams


# ── Step 3: Embed and deduplicate n-grams ──────────────────────────────────

def dedup_ngrams(ngrams):
    """Embed n-grams, merge semantic equivalents into groups."""
    print(f"Embedding {len(ngrams)} n-grams for deduplication...")
    texts = list(ngrams.keys())

    response = client.embeddings.create(
        model="text-embedding-3-large",
        input=texts,
        dimensions=256,
    )
    vectors = np.array([d.embedding for d in response.data])

    # Normalize and compute cosine similarity
    norms = np.linalg.norm(vectors, axis=1, keepdims=True)
    normed = vectors / norms
    sim_matrix = normed @ normed.T

    # Union-find to merge equivalent n-grams
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
            if sim_matrix[i, j] >= EQUIV_THRESHOLD:
                union(i, j)

    # Build groups: pick highest-frequency member as canonical
    groups = defaultdict(list)
    for i in range(len(texts)):
        groups[find(i)].append(i)

    deduped = {}
    merge_count = 0
    for group_indices in groups.values():
        members = [(texts[i], ngrams[texts[i]]) for i in group_indices]
        members.sort(key=lambda x: -x[1])  # Highest freq first
        canonical = members[0][0]
        total_freq = sum(f for _, f in members)
        all_forms = [m[0] for m in members]
        deduped[canonical] = {
            "freq": total_freq,
            "forms": all_forms,
        }
        if len(members) > 1:
            merge_count += 1

    print(f"  Merged into {len(deduped)} groups ({merge_count} groups have multiple forms)")

    # Show interesting merges
    for canonical, info in sorted(deduped.items(), key=lambda x: -len(x[1]["forms"])):
        if len(info["forms"]) > 1:
            print(f"    {canonical} <- {info['forms'][1:]}")
        if len(info["forms"]) <= 1:
            break  # Stop after showing all multi-form groups

    return deduped


# ── Step 4: LLM groups n-grams into axes ──────────────────────────────────

def identify_axes(deduped_ngrams, search_term):
    """One LLM call: group deduplicated n-grams into taxonomy axes."""
    print("Identifying taxonomy axes via LLM...")

    # Build compact n-gram list for the prompt
    ngram_lines = []
    for canonical, info in sorted(deduped_ngrams.items(), key=lambda x: -x[1]["freq"]):
        forms_str = ""
        if len(info["forms"]) > 1:
            forms_str = f" (also: {', '.join(info['forms'][1:])})"
        ngram_lines.append(f"  {canonical} ({info['freq']}x){forms_str}")

    ngram_block = "\n".join(ngram_lines)

    response = client.chat.completions.create(
        model=MODEL,
        messages=[
            {"role": "system", "content": f"""You are analyzing eBay listing data for the search term "{search_term}".
You are given {len(deduped_ngrams)} deduplicated n-grams extracted from listing titles, with frequency counts.

Your task: organize these n-grams into TAXONOMY AXES - independent dimensions that differentiate products.

Rules:
- Each axis represents ONE independent product dimension (e.g., "Model", "Storage", "Color")
- Assign each n-gram to AT MOST one axis. Most n-grams won't belong to any axis (they're noise like "sony playstation", "brand sealed", "excellent condition"). Only assign n-grams that represent a specific VALUE on an axis.
- The n-gram becomes the MATCH PATTERN. When you assign "digital edition" to the Edition axis, listings will be matched by checking if "digital edition" appears in the title. So only assign n-grams that are good substring match patterns.
- If an n-gram has multiple forms (shown in parentheses), all forms will be used for matching.
- NEVER create catch-all values like "Other". Every value must be specific.
- Keep to 3-5 axes maximum.

CRITICAL MATCHING RULES:
- Each axis value should have 1-2 n-grams MAXIMUM. Pick the SHORTEST n-gram that uniquely identifies this value. "825gb" is sufficient — do NOT also add "825gb disc", "825gb console", "edition 825gb" etc. Those are compound phrases that belong to other axes.
- Axis values must be MUTUALLY EXCLUSIVE within an axis. A listing should match AT MOST one value per axis.
- Do NOT create compound values that span multiple axes. "Slim Digital Edition" combines Form Factor + Drive Type — use separate axes.
- Be wary of n-grams that are FRAGMENTS of longer phrases. "ray edition" is a fragment of "blu ray edition" which is another way of saying "disc edition". Don't create spurious values.
- NEVER create a "default" or "standard" value that matches the product name itself (e.g., "playstation", "ps5", "edition", "console"). These match EVERY listing and are useless. If a listing doesn't match any specific value on an axis, it simply has no value for that axis — that's fine.
- Every n-gram you assign must be DISCRIMINATING — it should match a SUBSET of listings, not all of them.

SKIP these n-grams entirely:
- Brand names ("sony playstation", "sony ps5", "playstation", "ps5")
- The search term or its components
- Generic words ("edition", "console", "game", "gaming")
- Condition descriptions ("excellent condition", "good condition")
- Seller phrases ("brand sealed", "free shipping", "next day")
- Packaging/accessories ("cables controller", "boxed", "carry case")
- Compound n-grams where a shorter version already captures the same axis value"""},
            {"role": "user", "content": f"N-grams:\n{ngram_block}"}
        ],
        response_format={
            "type": "json_schema",
            "json_schema": {
                "name": "taxonomy_axes",
                "strict": True,
                "schema": {
                    "type": "object",
                    "properties": {
                        "axes": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "name": {"type": "string", "description": "Axis name e.g. 'Edition', 'Storage'"},
                                    "description": {"type": "string", "description": "What this axis differentiates"},
                                    "values": {
                                        "type": "array",
                                        "items": {
                                            "type": "object",
                                            "properties": {
                                                "label": {"type": "string", "description": "Human-readable value name"},
                                                "ngrams": {
                                                    "type": "array",
                                                    "items": {"type": "string"},
                                                    "description": "The n-gram(s) from the input list that represent this value. These will be used as substring match patterns."
                                                }
                                            },
                                            "required": ["label", "ngrams"],
                                            "additionalProperties": False
                                        }
                                    }
                                },
                                "required": ["name", "description", "values"],
                                "additionalProperties": False
                            }
                        }
                    },
                    "required": ["axes"],
                    "additionalProperties": False
                }
            }
        },
        temperature=0.2,
    )

    result = json.loads(response.choices[0].message.content)
    axes = result["axes"]

    print(f"  Identified {len(axes)} axes:")
    for ax in axes:
        print(f"    {ax['name']}: {ax['description']}")
        for val in ax["values"]:
            print(f"      - {val['label']}: match on {val['ngrams']}")

    return axes


# ── Step 5: Assign listings to categories ──────────────────────────────────

def assign_categories(listings, axes, deduped_ngrams):
    """Match listings to axis values using substring matching on n-grams."""
    print("Assigning listings to categories...")

    # Build match patterns: for each axis value, collect all n-gram forms
    axis_matchers = []
    for axis in axes:
        matchers = []
        for value in axis["values"]:
            patterns = []
            for ng in value["ngrams"]:
                # Include all equivalent forms from dedup
                if ng in deduped_ngrams:
                    patterns.extend(deduped_ngrams[ng]["forms"])
                else:
                    patterns.append(ng)
            matchers.append({
                "label": value["label"],
                "patterns": [p.lower() for p in patterns],
            })
        axis_matchers.append({
            "name": axis["name"],
            "values": matchers,
        })

    # For each listing, match ALL values per axis (multi-match detection)
    unsorted_count = 0
    for listing in listings:
        title_lower = listing["title"].lower()
        listing["axis_values"] = {}
        listing["axis_conflicts"] = {}

        for axis in axis_matchers:
            matched_values = []
            matched_patterns = []
            for value in axis["values"]:
                matching_pats = [p for p in value["patterns"] if p in title_lower]
                if matching_pats:
                    matched_values.append(value["label"])
                    matched_patterns.extend(matching_pats)

            # Resolve substring conflicts: if one matched pattern is a substring of another,
            # keep only the longer (more specific) match.
            # e.g., "midnight black" contains "black" — keep "Midnight Black", drop "Black"
            if len(matched_values) > 1:
                # Check if any matched pattern is a substring of another matched pattern
                resolved = []
                for value in axis["values"]:
                    value_pats = [p for p in value["patterns"] if p in title_lower]
                    if not value_pats:
                        continue
                    # Keep this value unless ALL its patterns are substrings of another value's patterns
                    is_subsumed = False
                    for other_value in axis["values"]:
                        if other_value["label"] == value["label"]:
                            continue
                        other_pats = [p for p in other_value["patterns"] if p in title_lower]
                        if other_pats and all(
                            any(vp in op and vp != op for op in other_pats)
                            for vp in value_pats
                        ):
                            is_subsumed = True
                            break
                    if not is_subsumed:
                        resolved.append(value["label"])
                matched_values = resolved

            if len(matched_values) == 1:
                listing["axis_values"][axis["name"]] = matched_values[0]
            elif len(matched_values) > 1:
                listing["axis_conflicts"][axis["name"]] = matched_values
                unsorted_count += 1

    # Build category label from clean axis values only
    axis_names = [ax["name"] for ax in axes]
    for listing in listings:
        parts = []
        for ax_name in axis_names:
            val = listing["axis_values"].get(ax_name)
            if val:
                parts.append(val)
        listing["category"] = " | ".join(parts) if parts else "Unlabeled"

    labeled = sum(1 for l in listings if l["category"] != "Unlabeled")
    categories = Counter(l["category"] for l in listings)
    print(f"  Labeled: {labeled}/{len(listings)} ({100*labeled/len(listings):.1f}%)")
    print(f"  Unlabeled: {len(listings) - labeled}")
    print(f"  Distinct categories: {len(categories)}")
    print(f"  Multi-match conflicts: {unsorted_count} (listings with >1 match on some axis)")

    # Show per-axis coverage
    for ax_name in axis_names:
        clean = sum(1 for l in listings if ax_name in l.get("axis_values", {}))
        conflicts = sum(1 for l in listings if ax_name in l.get("axis_conflicts", {}))
        unmatched = len(listings) - clean - conflicts
        print(f"    {ax_name}: {clean} clean, {conflicts} conflicts, {unmatched} unmatched")

    return listings


# ── Step 6: Compute stats ──────────────────────────────────────────────────

def compute_stats(listings, axes):
    print("Computing category stats...")

    categories = defaultdict(list)
    for listing in listings:
        categories[listing["category"]].append(listing)

    def median(arr):
        if not arr:
            return 0
        return arr[len(arr) // 2]

    def iqr(arr):
        if len(arr) < 4:
            return 0
        return arr[len(arr) * 3 // 4] - arr[len(arr) // 4]

    stats = []
    for cat_name, cat_listings in sorted(categories.items(), key=lambda x: -len(x[1])):
        prices = sorted([l["price"] for l in cat_listings if l.get("price") and l["price"] > 0])
        active = [l for l in cat_listings if l.get("status") == "Active"]
        sold = [l for l in cat_listings if l.get("status") in ("Sold", "Ended")]
        active_prices = sorted([l["price"] for l in active if l.get("price") and l["price"] > 0])
        sold_prices = sorted([l["price"] for l in sold if l.get("price") and l["price"] > 0])

        med_active = median(active_prices)
        med_sold = median(sold_prices)
        spread = med_sold - med_active if med_active > 0 and med_sold > 0 else 0
        sell_through = (
            round(100 * len(sold) / (len(active) + len(sold)))
            if (len(active) + len(sold)) > 0 else 0
        )

        axis_values = {}
        if cat_listings and cat_name != "Unlabeled":
            axis_values = cat_listings[0].get("axis_values", {})

        stats.append({
            "label": cat_name,
            "axisValues": axis_values,
            "total": len(cat_listings),
            "active": len(active),
            "sold": len(sold),
            "sellThroughPct": sell_through,
            "medianPrice": median(prices),
            "iqr": iqr(prices),
            "medianActivePrice": med_active,
            "medianSoldPrice": med_sold,
            "spread": round(spread, 2),
            "minPrice": min(prices) if prices else 0,
            "maxPrice": max(prices) if prices else 0,
            "sampleTitles": [l["title"] for l in cat_listings[:5]],
        })

    stats.sort(key=lambda x: -x["spread"])
    print(f"  Computed stats for {len(stats)} categories")
    return stats


# ── Step 7: Output ─────────────────────────────────────────────────────────

def output_json(axes, stats, listings, search_term, deduped_ngrams):
    output = {
        "jobId": JOB_ID,
        "searchTerm": search_term,
        "generatedAt": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "totalListings": len(listings),
        "labeledListings": sum(1 for l in listings if l["category"] != "Unlabeled"),
        "unlabeledListings": sum(1 for l in listings if l["category"] == "Unlabeled"),
        "axes": axes,
        "ngramGroups": {
            k: v for k, v in deduped_ngrams.items()
            if len(v["forms"]) > 1
        },
        "categories": stats,
    }

    os.makedirs(os.path.dirname(OUTPUT_FILE) or ".", exist_ok=True)
    with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
        json.dump(output, f, indent=2, ensure_ascii=False)

    print(f"\nOutput written to {OUTPUT_FILE}")
    print(f"  File size: {os.path.getsize(OUTPUT_FILE) / 1024:.1f} KB")


# ── Main ───────────────────────────────────────────────────────────────────

def main():
    start = time.time()

    listings, search_term = load_listings()
    ngrams = extract_ngrams(listings)
    deduped = dedup_ngrams(ngrams)
    axes = identify_axes(deduped, search_term)
    listings = assign_categories(listings, axes, deduped)
    stats = compute_stats(listings, axes)
    output_json(axes, stats, listings, search_term, deduped)

    elapsed = time.time() - start
    print(f"\nDone in {elapsed:.1f}s")

    # Print summary
    print(f"\n=== TOP OPPORTUNITIES (by spread, min 5 listings) ===")
    print(f"{'Category':<55} {'Act':>4} {'Sold':>4} {'MedAct':>8} {'MedSold':>8} {'Spread':>8} {'ST%':>4}")
    print("-" * 95)
    shown = 0
    for cat in stats:
        if cat["label"] == "Unlabeled" or cat["total"] < 5:
            continue
        print(f"{cat['label'][:55]:<55} {cat['active']:>4} {cat['sold']:>4} "
              f"{cat['medianActivePrice']:>8.0f} {cat['medianSoldPrice']:>8.0f} "
              f"{cat['spread']:>8.0f} {cat['sellThroughPct']:>3}%")
        shown += 1
        if shown >= 25:
            break


if __name__ == "__main__":
    main()
