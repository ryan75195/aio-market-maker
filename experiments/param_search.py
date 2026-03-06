"""
Parameter search across 3 datasets x parameter combinations.
Measures: coverage %, conflict %, category count, median category size.
"""

import pyodbc
import json
import re
import os
import time
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

JOBS = [
    (1, "PlayStation 5 Console"),
    (1029, "Pokemon Booster Box Sealed"),
    (1121, "Brompton Folding Bike"),
]

FREQ_DIVISORS = [100, 200, 300, 500]
EQUIV_THRESHOLDS = [0.93, 0.95, 0.97]


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


def extract_ngrams(listings, min_uni_freq, min_bi_freq):
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


def dedup_ngrams(ngrams, equiv_threshold):
    texts = list(ngrams.keys())
    if len(texts) == 0:
        return {}

    response = client.embeddings.create(
        model="text-embedding-3-large", input=texts, dimensions=256
    )
    vectors = np.array([d.embedding for d in response.data])
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
        deduped[canonical] = {
            "freq": sum(f for _, f in members),
            "forms": [m[0] for m in members],
        }
    return deduped


def identify_axes(deduped_ngrams, search_term):
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
- Assign each n-gram to AT MOST one axis. Most n-grams won't belong to any axis (they're noise). Only assign n-grams that represent a specific VALUE on an axis.
- The n-gram becomes the MATCH PATTERN for substring matching against titles.
- If an n-gram has multiple forms (shown in parentheses), all forms will be used for matching.
- NEVER create catch-all values like "Other". Every value must be specific.
- Keep to 3-5 axes maximum.
- Each axis must have AT MOST 15 values. If a dimension has more than 15 distinct values, keep only the 15 most frequent and ignore the rest.

CRITICAL MATCHING RULES:
- Each axis value should have 1-2 n-grams MAXIMUM. Pick the SHORTEST n-gram that uniquely identifies this value.
- Axis values must be MUTUALLY EXCLUSIVE within an axis.
- NEVER create a "default" value that matches the product name itself or generic words like "edition", "console", "box".
- Every n-gram you assign must be DISCRIMINATING - it should match a SUBSET of listings, not all of them.
- Do NOT duplicate values within an axis.

SKIP these n-grams entirely:
- Brand names, the search term or its components
- Generic words ("edition", "console", "game", "box", "sealed", "brand")
- Condition descriptions, seller phrases, packaging/accessories"""},
            {"role": "user", "content": f"N-grams:\n{ngram_block}"},
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
                                        "items": {
                                            "type": "object",
                                            "properties": {
                                                "label": {"type": "string"},
                                                "ngrams": {
                                                    "type": "array",
                                                    "items": {"type": "string"},
                                                },
                                            },
                                            "required": ["label", "ngrams"],
                                            "additionalProperties": False,
                                        },
                                    },
                                },
                                "required": ["name", "description", "values"],
                                "additionalProperties": False,
                            },
                        }
                    },
                    "required": ["axes"],
                    "additionalProperties": False,
                },
            },
        },
        temperature=0.2,
    )

    return json.loads(response.choices[0].message.content)["axes"]


def assign_and_measure(listings, axes, deduped_ngrams):
    """Assign listings and return metrics."""
    axis_matchers = []
    for axis in axes:
        matchers = []
        for value in axis["values"]:
            patterns = []
            for ng in value["ngrams"]:
                if ng in deduped_ngrams:
                    patterns.extend(deduped_ngrams[ng]["forms"])
                else:
                    patterns.append(ng)
            matchers.append({"label": value["label"], "patterns": [p.lower() for p in patterns]})
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
            matched_patterns = []
            for value in axis["values"]:
                matching_pats = [p for p in value["patterns"] if p in title_lower]
                if matching_pats:
                    matched_values.append(value["label"])
                    matched_patterns.extend(matching_pats)

            if len(matched_values) > 1:
                # Substring resolution
                resolved = []
                for value in axis["values"]:
                    value_pats = [p for p in value["patterns"] if p in title_lower]
                    if not value_pats:
                        continue
                    is_subsumed = False
                    for other_value in axis["values"]:
                        if other_value["label"] == value["label"]:
                            continue
                        other_pats = [p for p in other_value["patterns"] if p in title_lower]
                        if other_pats and all(
                            any(vp in op and vp != op for op in other_pats) for vp in value_pats
                        ):
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

    # Compute metrics
    total = len(listings)
    coverage_pct = 100 * labeled_count / total if total > 0 else 0
    conflict_pct = 100 * conflict_count / total if total > 0 else 0
    num_categories = len([c for c in category_counter if c != "Unlabeled"])

    cat_sizes = [v for k, v in category_counter.items() if k != "Unlabeled"]
    median_cat_size = sorted(cat_sizes)[len(cat_sizes) // 2] if cat_sizes else 0

    num_axes = len(axes)
    total_values = sum(len(ax["values"]) for ax in axes)

    return {
        "coverage_pct": round(coverage_pct, 1),
        "conflict_pct": round(conflict_pct, 1),
        "num_categories": num_categories,
        "median_cat_size": median_cat_size,
        "num_axes": num_axes,
        "total_axis_values": total_values,
    }


def run_search():
    # Pre-load all datasets
    datasets = {}
    for job_id, search_term in JOBS:
        print(f"Loading job {job_id} ({search_term})...")
        datasets[job_id] = load_listings(job_id)
        print(f"  {len(datasets[job_id])} listings")

    results = []

    for divisor in FREQ_DIVISORS:
        for equiv_thresh in EQUIV_THRESHOLDS:
            for job_id, search_term in JOBS:
                listings = datasets[job_id]
                n = len(listings)

                # Scale frequency thresholds
                min_bi_freq = max(10, n // divisor)
                min_uni_freq = max(20, min_bi_freq * 2)

                label = f"d={divisor} eq={equiv_thresh} job={job_id}"
                print(f"\n--- {label} (min_uni={min_uni_freq}, min_bi={min_bi_freq}) ---")

                try:
                    ngrams = extract_ngrams(listings, min_uni_freq, min_bi_freq)
                    print(f"  {len(ngrams)} n-grams")

                    deduped = dedup_ngrams(ngrams, equiv_thresh)
                    print(f"  {len(deduped)} after dedup")

                    axes = identify_axes(deduped, search_term)
                    ax_summary = ", ".join(f"{ax['name']}({len(ax['values'])})" for ax in axes)
                    print(f"  Axes: {ax_summary}")

                    metrics = assign_and_measure(listings, axes, deduped)
                    print(f"  Coverage={metrics['coverage_pct']}% Conflicts={metrics['conflict_pct']}% "
                          f"Cats={metrics['num_categories']} MedSize={metrics['median_cat_size']}")

                    results.append({
                        "job_id": job_id,
                        "search_term": search_term,
                        "listing_count": n,
                        "divisor": divisor,
                        "equiv_threshold": equiv_thresh,
                        "min_uni_freq": min_uni_freq,
                        "min_bi_freq": min_bi_freq,
                        "ngram_count": len(ngrams),
                        "dedup_count": len(deduped),
                        **metrics,
                    })
                except Exception as e:
                    print(f"  FAILED: {e}")
                    results.append({
                        "job_id": job_id,
                        "search_term": search_term,
                        "listing_count": n,
                        "divisor": divisor,
                        "equiv_threshold": equiv_thresh,
                        "error": str(e),
                    })

    # Save results
    with open("experiments/param_search_results.json", "w") as f:
        json.dump(results, f, indent=2)

    # Print summary table
    print("\n\n" + "=" * 130)
    print("PARAMETER SEARCH RESULTS")
    print("=" * 130)
    print(f"{'Job':<12} {'Div':>4} {'EqTh':>5} {'MinUni':>6} {'MinBi':>5} {'NGrams':>6} {'Dedup':>5} "
          f"{'Cover%':>7} {'Confl%':>7} {'Cats':>5} {'MedSz':>5} {'Axes':>5} {'Vals':>5}")
    print("-" * 130)

    for r in results:
        if "error" in r:
            continue
        job_label = {1: "PS5", 1029: "Pokemon", 1121: "Brompton"}[r["job_id"]]
        print(f"{job_label:<12} {r['divisor']:>4} {r['equiv_threshold']:>5} {r['min_uni_freq']:>6} "
              f"{r['min_bi_freq']:>5} {r['ngram_count']:>6} {r['dedup_count']:>5} "
              f"{r['coverage_pct']:>6.1f}% {r['conflict_pct']:>6.1f}% {r['num_categories']:>5} "
              f"{r['median_cat_size']:>5} {r['num_axes']:>5} {r['total_axis_values']:>5}")


if __name__ == "__main__":
    run_search()
