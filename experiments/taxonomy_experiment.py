"""
Taxonomy Experiment: N-gram → LLM → Regex → Category Assignment

End-to-end pipeline:
1. Extract n-gram frequencies from all listing titles
2. Sample titles per n-gram (price-diverse)
3. LLM identifies taxonomy axes from n-gram data
4. LLM generates match/exclude regex per category (parallel)
5. Apply regex to all listings, assign categories
6. Compute stats per category
7. Output structured JSON for UI consumption
"""

import pyodbc
import json
import re
import os
import sys
import time
from collections import Counter, defaultdict
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime
from openai import OpenAI

# ── Config ──────────────────────────────────────────────────────────────────

CONN_STR = (
    r"Driver={ODBC Driver 17 for SQL Server};"
    r"Server=(localdb)\MSSQLLocalDB;"
    r"Database=AIOMarketMaker;"
    r"Trusted_Connection=yes;"
)
JOB_ID = 1
MIN_NGRAM_FREQ = 15
SAMPLES_PER_NGRAM = 20
OUTPUT_FILE = "experiments/taxonomy-ps5.json"

# Read API key from local.settings.json
settings_path = os.path.join(os.path.dirname(__file__), "..", "AIOMarketMaker.Console", "local.settings.json")
with open(settings_path) as f:
    settings = json.load(f)
OPENAI_API_KEY = settings["OpenAi"]["ApiKey"]

client = OpenAI(api_key=OPENAI_API_KEY)
MODEL = "gpt-4.1-mini"


# ── Step 1: Load listings from DB ──────────────────────────────────────────

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
    print(f"  Loaded {len(listings)} listings")
    return listings


# ── Step 2: Extract n-grams ────────────────────────────────────────────────

STOP_WORDS = {
    "the", "a", "an", "and", "or", "of", "for", "in", "on", "at", "to",
    "is", "it", "by", "as", "be", "no", "not", "so", "up", "if", "my",
    "new", "free", "with", "this", "that", "from", "was", "are", "has",
}

def extract_ngrams(listings):
    print("Extracting n-grams...")
    bigrams = Counter()
    trigrams = Counter()

    for listing in listings:
        words = re.findall(r'\b\w+\b', listing["title"].lower())
        # Filter stop words for cleaner n-grams
        words_filtered = [w for w in words if w not in STOP_WORDS and len(w) > 1]

        for i in range(len(words_filtered) - 1):
            bigrams[f"{words_filtered[i]} {words_filtered[i+1]}"] += 1
        for i in range(len(words_filtered) - 2):
            trigrams[f"{words_filtered[i]} {words_filtered[i+1]} {words_filtered[i+2]}"] += 1

    significant = {}
    for ngram, freq in bigrams.most_common():
        if freq >= MIN_NGRAM_FREQ:
            significant[ngram] = {"freq": freq, "type": "bigram"}
    for ngram, freq in trigrams.most_common():
        if freq >= MIN_NGRAM_FREQ:
            significant[ngram] = {"freq": freq, "type": "trigram"}

    print(f"  Found {len(significant)} significant n-grams (freq >= {MIN_NGRAM_FREQ})")
    return significant


# ── Step 3: Sample titles per n-gram (price-diverse) ───────────────────────

def sample_titles_for_ngram(listings, ngram, n=SAMPLES_PER_NGRAM):
    """Sample n titles containing this n-gram, spread across the price range."""
    matching = [l for l in listings if ngram in l["title"].lower()]
    if len(matching) <= n:
        return matching

    with_price = sorted(
        [l for l in matching if l["price"] and l["price"] > 0],
        key=lambda x: x["price"]
    )

    if len(with_price) >= n:
        # Sample evenly across price range
        indices = [int(i * (len(with_price) - 1) / (n - 1)) for i in range(n)]
        # Deduplicate indices
        indices = sorted(set(indices))
        sampled = [with_price[i] for i in indices]
        # Fill up if dedup reduced count
        remaining = [l for l in with_price if l not in sampled]
        while len(sampled) < n and remaining:
            sampled.append(remaining.pop(0))
        return sampled[:n]
    else:
        without_price = [l for l in matching if not l["price"] or l["price"] == 0]
        return (with_price + without_price)[:n]


def build_ngram_samples(listings, ngrams):
    print("Sampling titles per n-gram...")
    samples = {}
    for ngram in ngrams:
        sampled = sample_titles_for_ngram(listings, ngram)
        samples[ngram] = [
            {"title": s["title"], "price": s["price"], "status": s["status"]}
            for s in sampled
        ]
    print(f"  Built samples for {len(samples)} n-grams")
    return samples


# ── Step 4: LLM identifies taxonomy axes ──────────────────────────────────

def identify_axes(ngrams, samples, search_term):
    """Send n-gram frequency table + samples to LLM to identify taxonomy axes."""
    print("Identifying taxonomy axes via LLM...")

    # Build the n-gram data block
    ngram_lines = []
    for ngram, info in sorted(ngrams.items(), key=lambda x: -x[1]["freq"]):
        sample_titles = samples.get(ngram, [])
        title_examples = []
        for s in sample_titles[:5]:  # 5 examples for the axis identification step
            price_str = f"£{s['price']:.0f}" if s["price"] else "no price"
            title_examples.append(f"    - {s['title']} ({price_str}, {s['status']})")
        examples_block = "\n".join(title_examples)
        ngram_lines.append(f'"{ngram}" ({info["freq"]}x):\n{examples_block}')

    ngram_block = "\n".join(ngram_lines)

    response = client.chat.completions.create(
        model=MODEL,
        messages=[
            {"role": "system", "content": f"""You are analyzing eBay listing data for the search term "{search_term}".
You are given n-gram frequency data extracted from {len(ngrams)} listing titles, with sample titles for each n-gram.

Your task: identify the TAXONOMY AXES — the independent dimensions that differentiate products in this search.

For each axis:
- name: A clear axis name (e.g., "Edition", "Storage", "Product Type")
- values: All distinct values for this axis, ordered by frequency
- description: What this axis differentiates

Rules:
- Only include axes that represent meaningful PRODUCT differences (not seller noise like "free shipping", "fast delivery")
- Each axis should be ORTHOGONAL — values on one axis shouldn't imply values on another
- Include a "Product Type" axis if the search contains fundamentally different products (e.g., consoles vs controllers vs accessories)
- Be exhaustive — capture all significant values, even uncommon ones
- Don't create axes for condition (New/Used) — that's handled separately as a DB field
- NEVER generate catch-all values like "Other", "Other Limited Edition", "Various", "Misc". Every value must be a specific, named product variant. If a variant is rare (< 5 occurrences), omit it rather than lumping into "Other".
- Bundles should be a separate axis from product editions. "30th Anniversary Edition" is an edition. "Fortnite Bundle" is a bundle. Don't mix them.
- Keep axes to 3-4 maximum. More axes creates combinatorial explosion of empty categories."""},
            {"role": "user", "content": f"N-gram frequency data:\n\n{ngram_block}"}
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
                                    "name": {"type": "string"},
                                    "description": {"type": "string"},
                                    "values": {
                                        "type": "array",
                                        "items": {"type": "string"}
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
        print(f"    {ax['name']}: {len(ax['values'])} values — {ax['description']}")
    return axes


# ── Step 5: Generate categories + regex (parallel per-category LLM calls) ─

def generate_category_regex(axis_name, axis_value, search_term, sample_titles):
    """Generate match + exclude regex for one category value."""
    titles_block = "\n".join(
        f"  - {s['title']} (£{s['price']:.0f}, {s['status']})"
        if s["price"] else f"  - {s['title']} (no price, {s['status']})"
        for s in sample_titles
    )

    response = client.chat.completions.create(
        model=MODEL,
        messages=[
            {"role": "system", "content": f"""You are generating regex patterns for categorizing eBay listings from a search for "{search_term}".

Axis: {axis_name}
Value: {axis_value}

Generate:
1. match_regex: A case-insensitive regex that matches listings belonging to this category value.
   - CRITICAL: eBay sellers use many abbreviations. Always include alternations for common variants:
     * "PlayStation 5" / "PS5" / "Playstation5" / "Play Station 5"
     * Use (playstation\\s*5|ps5) not just "playstation.*?5"
   - Use .*? between key terms since title word order varies
   - Be specific enough to avoid false positives from OTHER axis values on the SAME axis
     * e.g., "Digital Edition" regex must NOT match "Disc Edition" or just "Edition"
   - The regex will be applied with re.IGNORECASE

2. exclude_regex: A case-insensitive regex that should REJECT false matches.
   - Catch parts, accessories, skins, covers, cases, repairs, replacements, faceplates
   - Catch bulk lots (x2, x3, lot of, wholesale, bundle of \\d+)
   - Catch controller-only listings if this is a console category
   - Leave empty string if no exclusions needed

Look at the sample titles carefully. Some may be contamination (parts, accessories, bundles). Use those to inform your exclude pattern."""},
            {"role": "user", "content": f"Sample titles matching '{axis_value}':\n{titles_block}"}
        ],
        response_format={
            "type": "json_schema",
            "json_schema": {
                "name": "category_regex",
                "strict": True,
                "schema": {
                    "type": "object",
                    "properties": {
                        "match_regex": {"type": "string"},
                        "exclude_regex": {"type": "string"}
                    },
                    "required": ["match_regex", "exclude_regex"],
                    "additionalProperties": False
                }
            }
        },
        temperature=0.1,
    )

    result = json.loads(response.choices[0].message.content)
    return result


def generate_all_regex(axes, listings, search_term):
    """Generate regex for every axis value, in parallel."""
    print("Generating regex patterns per category (parallel)...")

    # For each axis value, find sample titles
    tasks = []
    for axis in axes:
        for value in axis["values"]:
            # Find titles containing this value (case-insensitive)
            value_lower = value.lower()
            # Use word-boundary-ish matching
            matching = [
                l for l in listings
                if value_lower in l["title"].lower()
            ]
            # Price-diverse sample
            sampled = _price_diverse_sample(matching, SAMPLES_PER_NGRAM)
            tasks.append({
                "axis": axis["name"],
                "value": value,
                "samples": sampled,
            })

    results = {}
    total = len(tasks)
    completed = 0

    with ThreadPoolExecutor(max_workers=20) as executor:
        futures = {}
        for task in tasks:
            future = executor.submit(
                generate_category_regex,
                task["axis"], task["value"], search_term,
                task["samples"]
            )
            futures[future] = (task["axis"], task["value"], len(task["samples"]))

        for future in as_completed(futures):
            axis_name, value, sample_count = futures[future]
            completed += 1
            try:
                regex_result = future.result()
                key = f"{axis_name}::{value}"
                results[key] = {
                    "axis": axis_name,
                    "value": value,
                    "match_regex": regex_result["match_regex"],
                    "exclude_regex": regex_result["exclude_regex"],
                    "sample_count": sample_count,
                }
                print(f"  [{completed}/{total}] {axis_name}: {value} — "
                      f"match=/{regex_result['match_regex']}/ "
                      f"exclude=/{regex_result['exclude_regex']}/")
            except Exception as e:
                print(f"  [{completed}/{total}] FAILED {axis_name}: {value} — {e}")

    print(f"  Generated regex for {len(results)}/{total} categories")
    return results


def _price_diverse_sample(listings, n):
    """Sample n listings spread across the price range."""
    if len(listings) <= n:
        return [{"title": l["title"], "price": l["price"], "status": l["status"]} for l in listings]

    with_price = sorted(
        [l for l in listings if l.get("price") and l["price"] > 0],
        key=lambda x: x["price"]
    )

    if len(with_price) >= n:
        indices = sorted(set(int(i * (len(with_price) - 1) / (n - 1)) for i in range(n)))
        sampled = [with_price[i] for i in indices]
        remaining = [l for l in with_price if l not in sampled]
        while len(sampled) < n and remaining:
            sampled.append(remaining.pop(0))
        return [{"title": s["title"], "price": s["price"], "status": s["status"]} for s in sampled[:n]]
    else:
        without_price = [l for l in listings if not l.get("price") or l["price"] == 0]
        combined = with_price + without_price
        return [{"title": s["title"], "price": s["price"], "status": s["status"]} for s in combined[:n]]


# ── Step 6: Apply regex to all listings ────────────────────────────────────

def assign_categories(listings, axes, regex_map):
    """Assign each listing to axis values based on regex matching."""
    print("Assigning listings to categories...")

    # Pre-compile regex patterns
    compiled = {}
    for key, info in regex_map.items():
        try:
            match_re = re.compile(info["match_regex"], re.IGNORECASE) if info["match_regex"] else None
            exclude_re = re.compile(info["exclude_regex"], re.IGNORECASE) if info["exclude_regex"] else None
            compiled[key] = (match_re, exclude_re, info["axis"], info["value"])
        except re.error as e:
            print(f"  WARNING: Invalid regex for {key}: {e}")

    # For each listing, determine axis values
    for listing in listings:
        title = listing["title"]
        listing["axis_values"] = {}

        for key, (match_re, exclude_re, axis, value) in compiled.items():
            if match_re and match_re.search(title):
                if exclude_re and exclude_re.search(title):
                    continue  # Excluded
                # Assign to this axis value (first match wins per axis)
                if axis not in listing["axis_values"]:
                    listing["axis_values"][axis] = value

    # Build category key from axis values
    axis_names = [ax["name"] for ax in axes]
    for listing in listings:
        parts = []
        for ax_name in axis_names:
            val = listing["axis_values"].get(ax_name)
            if val:
                parts.append(val)
        listing["category"] = " | ".join(parts) if parts else "Unlabeled"

    # Count assignments
    categories = Counter(l["category"] for l in listings)
    labeled = sum(1 for l in listings if l["category"] != "Unlabeled")
    print(f"  Labeled: {labeled}/{len(listings)} ({100*labeled/len(listings):.1f}%)")
    print(f"  Unlabeled: {len(listings) - labeled}")
    print(f"  Distinct categories: {len(categories)}")

    return listings


# ── Step 7: Compute stats per category ─────────────────────────────────────

def compute_category_stats(listings, axes, regex_map):
    """Compute pricing/volume stats per category."""
    print("Computing category stats...")

    categories = defaultdict(list)
    for listing in listings:
        categories[listing["category"]].append(listing)

    stats = []
    for cat_name, cat_listings in sorted(categories.items(), key=lambda x: -len(x[1])):
        prices = sorted([l["price"] for l in cat_listings if l.get("price") and l["price"] > 0])
        active = [l for l in cat_listings if l.get("status") == "Active"]
        sold = [l for l in cat_listings if l.get("status") in ("Sold", "Ended")]

        active_prices = sorted([l["price"] for l in active if l.get("price") and l["price"] > 0])
        sold_prices = sorted([l["price"] for l in sold if l.get("price") and l["price"] > 0])

        def median(arr):
            if not arr:
                return 0
            return arr[len(arr) // 2]

        def iqr(arr):
            if len(arr) < 4:
                return 0
            return arr[len(arr) * 3 // 4] - arr[len(arr) // 4]

        med_active = median(active_prices)
        med_sold = median(sold_prices)
        spread = med_sold - med_active if med_active > 0 and med_sold > 0 else 0

        sell_through = (
            round(100 * len(sold) / (len(active) + len(sold)))
            if (len(active) + len(sold)) > 0 else 0
        )

        # Extract axis values for this category
        axis_values = {}
        if cat_listings and cat_name != "Unlabeled":
            axis_values = cat_listings[0].get("axis_values", {})

        # Collect the match/exclude regex used
        regex_info = {}
        for ax_name, ax_value in axis_values.items():
            key = f"{ax_name}::{ax_value}"
            if key in regex_map:
                regex_info[ax_name] = {
                    "value": ax_value,
                    "match": regex_map[key]["match_regex"],
                    "exclude": regex_map[key]["exclude_regex"],
                }

        cat_stat = {
            "label": cat_name,
            "axisValues": axis_values,
            "regex": regex_info,
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
        }
        stats.append(cat_stat)

    # Sort by spread descending (best opportunities first)
    stats.sort(key=lambda x: -x["spread"])
    print(f"  Computed stats for {len(stats)} categories")
    return stats


# ── Step 8: Output JSON ────────────────────────────────────────────────────

def output_taxonomy(axes, category_stats, listings, search_term):
    output = {
        "jobId": JOB_ID,
        "searchTerm": search_term,
        "generatedAt": datetime.now().isoformat(),
        "totalListings": len(listings),
        "labeledListings": sum(1 for l in listings if l["category"] != "Unlabeled"),
        "unlabeledListings": sum(1 for l in listings if l["category"] == "Unlabeled"),
        "axes": axes,
        "categories": category_stats,
    }

    os.makedirs(os.path.dirname(OUTPUT_FILE) or ".", exist_ok=True)
    with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
        json.dump(output, f, indent=2, ensure_ascii=False)

    print(f"\nOutput written to {OUTPUT_FILE}")
    print(f"  File size: {os.path.getsize(OUTPUT_FILE) / 1024:.1f} KB")
    return output


# ── Main ───────────────────────────────────────────────────────────────────

def main():
    start = time.time()

    # Step 1: Load data
    listings = load_listings()
    search_term = "PlayStation 5 Console"

    # Step 2: Extract n-grams
    ngrams = extract_ngrams(listings)

    # Step 3: Sample titles per n-gram
    samples = build_ngram_samples(listings, ngrams)

    # Step 4: Identify taxonomy axes
    axes = identify_axes(ngrams, samples, search_term)

    # Step 5: Generate regex per category value (parallel)
    regex_map = generate_all_regex(axes, listings, search_term)

    # Step 6: Assign categories to listings
    listings = assign_categories(listings, axes, regex_map)

    # Step 7: Compute stats
    category_stats = compute_category_stats(listings, axes, regex_map)

    # Step 8: Output
    output = output_taxonomy(axes, category_stats, listings, search_term)

    elapsed = time.time() - start
    print(f"\nDone in {elapsed:.1f}s")

    # Print opportunity summary
    print("\n=== TOP OPPORTUNITIES (by spread) ===")
    print(f"{'Category':<50} {'Act':>4} {'Sold':>4} {'MedAct':>8} {'MedSold':>8} {'Spread':>8} {'ST%':>4}")
    print("─" * 90)
    for cat in category_stats[:20]:
        if cat["label"] == "Unlabeled":
            continue
        print(f"{cat['label'][:50]:<50} {cat['active']:>4} {cat['sold']:>4} "
              f"£{cat['medianActivePrice']:>7.0f} £{cat['medianSoldPrice']:>7.0f} "
              f"£{cat['spread']:>7.0f} {cat['sellThroughPct']:>3}%")


if __name__ == "__main__":
    main()
