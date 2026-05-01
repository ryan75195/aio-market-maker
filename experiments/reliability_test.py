"""
Reliability test: Run the v3 pipeline N times on each dataset
and measure consistency of the LLM axis grouping step.

Tests both old (baseline) and new (ME-filtered + substring dedup) pipelines.
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

ME_THRESHOLD = 0.05
MIN_MATCH_PCT = 0.03
MIN_ME_PAIRS = 3
RUNS = 5


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


def compute_pairs(sig_canonicals, match_sets):
    me_pairs = []
    cooccur_pairs = []
    for i in range(len(sig_canonicals)):
        for j in range(i + 1, len(sig_canonicals)):
            a_set = match_sets[sig_canonicals[i]]
            b_set = match_sets[sig_canonicals[j]]
            if len(a_set) == 0 or len(b_set) == 0:
                continue
            intersection = len(a_set & b_set)
            overlap = intersection / min(len(a_set), len(b_set))
            if overlap < ME_THRESHOLD:
                me_pairs.append((sig_canonicals[i], sig_canonicals[j], overlap))
            elif overlap > 0.5:
                cooccur_pairs.append((sig_canonicals[i], sig_canonicals[j], overlap))
    return me_pairs, cooccur_pairs


def filter_to_me_participants(significant, me_pairs):
    me_counts = Counter()
    for a, b, _ in me_pairs:
        me_counts[a] += 1
        me_counts[b] += 1
    return {k: v for k, v in significant.items() if me_counts.get(k, 0) >= MIN_ME_PAIRS}


def dedup_substrings(candidates, match_sets):
    """Smart substring dedup: standalone words remove longer forms,
    fragment words are removed in favor of longer forms."""
    canonicals = sorted(candidates.keys(), key=lambda k: len(k))
    to_remove = set()
    for i in range(len(canonicals)):
        if canonicals[i] in to_remove:
            continue
        shorter = canonicals[i]
        shorter_set = match_sets[shorter]
        if len(shorter_set) == 0:
            continue
        for j in range(i + 1, len(canonicals)):
            if canonicals[j] in to_remove:
                continue
            longer = canonicals[j]
            shorter_words = set(shorter.split())
            longer_words = set(longer.split())
            if not shorter_words.issubset(longer_words):
                continue
            longer_set = match_sets[longer]
            if len(longer_set) == 0:
                continue
            coverage = len(shorter_set & longer_set) / len(longer_set)
            if coverage < 0.80:
                continue
            standalone_ratio = len(shorter_set - longer_set) / len(shorter_set)
            if standalone_ratio > 0.20:
                to_remove.add(longer)
            else:
                to_remove.add(shorter)
                break
    return {k: v for k, v in candidates.items() if k not in to_remove}


def call_llm(candidates, match_sets, me_pairs, cooccur_pairs, search_term, total):
    ngram_lines = []
    for canonical in sorted(candidates.keys(), key=lambda c: -len(match_sets[c])):
        info = candidates[canonical]
        match_count = len(match_sets[canonical])
        pct = 100 * match_count / total
        forms_str = ""
        if len(info["forms"]) > 1:
            forms_str = f" (also: {', '.join(info['forms'][1:])})"
        ngram_lines.append(f"  {canonical}: {match_count} matches ({pct:.1f}%){forms_str}")

    # Filter pairs to only include candidates
    me_filtered = [(a, b, ov) for a, b, ov in me_pairs if a in candidates and b in candidates]
    me_sorted = sorted(me_filtered, key=lambda x: -(len(match_sets[x[0]]) + len(match_sets[x[1]])))
    me_lines = [f"  {a} <-> {b} (overlap {ov:.1%})" for a, b, ov in me_sorted[:60]]

    cooccur_filtered = [(a, b, ov) for a, b, ov in cooccur_pairs if a in candidates and b in candidates]
    cooccur_sorted = sorted(cooccur_filtered, key=lambda x: -x[2])
    cooccur_lines = [f"  {a} + {b} (co-occur {ov:.0%})" for a, b, ov in cooccur_sorted[:20]]

    system_prompt = f"""You are organizing eBay listing data for "{search_term}" ({total} listings).

You are given:
1. {len(candidates)} axis-value candidates (pre-filtered to only n-grams with strong mutual exclusivity evidence)
2. Pre-computed MUTUALLY EXCLUSIVE pairs (never appear in the same title)
3. Pre-computed CO-OCCURRING pairs (frequently appear together)

Your task: group n-grams into 3-8 TAXONOMY AXES (independent product dimensions).

CRITICAL RULES:
- Each axis is an independent dimension (e.g., "Color", "Storage", "Model")
- Values within an axis MUST be mutually exclusive — use the ME pairs as evidence
- Values on DIFFERENT axes should be able to co-occur — use the co-occur pairs as evidence
- If two n-grams co-occur frequently, they CANNOT be on the same axis
- If two n-grams are mutually exclusive, they MIGHT be on the same axis (but not necessarily)
- Each n-gram goes to AT MOST one axis. Only assign n-grams that clearly represent axis VALUES.
- Each axis: at most 15 values. Pick SHORT n-grams (1-2 words) that uniquely identify each value.
- SKIP: brand names, the search term itself, generic words, condition words, seller phrases
- NEVER create catch-all or default values that match most listings
- Every assigned n-gram must be DISCRIMINATING — matching a SUBSET, not all listings"""

    user_prompt = f"""N-grams (sorted by match count):
{chr(10).join(ngram_lines)}

MUTUALLY EXCLUSIVE pairs (never in same title — candidates for same axis):
{chr(10).join(me_lines)}

CO-OCCURRING pairs (frequently together — must be on DIFFERENT axes):
{chr(10).join(cooccur_lines)}"""

    response = client.chat.completions.create(
        model=MODEL,
        messages=[
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": user_prompt},
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


def enforce_me(axes, significant, listings):
    """Drop axes where >30% of value pairs violate ME."""
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

        if total_pairs == 0 or violations / total_pairs <= 0.30:
            clean.append(axis)
    return clean


def prune_and_measure(axes, significant, listings):
    """Prune overlapping values, enforce ME, and measure assignment metrics."""
    total = len(listings)

    # Prune overlapping values within each axis
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
            remaining.pop(worst_idx)

        if len(remaining) >= 2:
            pruned_axes.append({
                "name": axis["name"],
                "values": [{"label": v["label"], "ngrams": v["ngrams"]} for v in remaining],
            })

    # Enforce ME — drop non-ME axes
    pruned_axes = enforce_me(pruned_axes, significant, listings)

    # Measure
    axis_matchers = []
    for axis in pruned_axes:
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
    for listing in listings:
        title_lower = listing["title"].lower()
        has_label = False
        has_conflict = False
        for axis in axis_matchers:
            matched = [v["label"] for v in axis["values"] if any(pattern_matches(p, title_lower) for p in v["patterns"])]
            if len(matched) == 1:
                has_label = True
            elif len(matched) > 1:
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
                if len(resolved) == 1:
                    has_label = True
                elif len(resolved) > 1:
                    has_conflict = True
                    has_label = True
        if has_label:
            labeled_count += 1
        if has_conflict:
            conflict_count += 1

    coverage = round(100 * labeled_count / total, 1)
    conflicts = round(100 * conflict_count / total, 1)
    n_axes = len(pruned_axes)
    n_values = sum(len(ax["values"]) for ax in pruned_axes)
    axis_summary = ", ".join(f"{ax['name']}({len(ax['values'])})" for ax in pruned_axes)

    return coverage, conflicts, n_axes, n_values, axis_summary


def main():
    jobs = [
        (1, "PlayStation 5 Console"),
        (1029, "Pokemon Booster Box Sealed"),
        (1121, "Brompton Folding Bike"),
        (1042, "Nike Air Jordan 1"),
        (1045, "LEGO Star Wars Set"),
        (1020, "iPhone 15 Pro Max"),
        (1086, "Vintage Levis 501 Jeans"),
    ]

    for job_id, search_term in jobs:
        print(f"\n{'='*70}")
        print(f"RELIABILITY TEST: {search_term} (job {job_id}) x {RUNS} runs")
        print(f"{'='*70}")

        # Pre-compute deterministic steps once
        listings = load_listings(job_id)
        total = len(listings)
        ngrams = extract_ngrams(listings)
        deduped = dedup_ngrams(ngrams, 0.95)
        match_sets = compute_match_sets(listings, deduped)

        min_matches = int(total * MIN_MATCH_PCT)
        significant = {k: v for k, v in deduped.items() if len(match_sets[k]) >= min_matches}
        sig_canonicals = list(significant.keys())
        me_pairs, cooccur_pairs = compute_pairs(sig_canonicals, match_sets)

        # NEW: ME participant filter + smart substring dedup
        me_significant = filter_to_me_participants(significant, me_pairs)
        me_significant = dedup_substrings(me_significant, match_sets)

        print(f"  {total} listings, {len(significant)} sig -> {len(me_significant)} ME+dedup candidates, {len(me_pairs)} ME pairs")

        results = []
        for run in range(RUNS):
            for attempt in range(3):
                try:
                    axes = call_llm(me_significant, match_sets, me_pairs, cooccur_pairs, search_term, total)
                    break
                except RuntimeError:
                    if attempt < 2:
                        print(f"    (retry {attempt+2}/3)")
                        continue
                    print(f"  Run {run+1}: FAILED (repetition loop)")
                    axes = []
                    break

            if not axes:
                results.append({"run": run + 1, "coverage": 0, "conflicts": 0, "n_axes": 0, "n_values": 0, "axis_summary": "FAILED"})
                continue

            coverage, conflicts, n_axes, n_values, axis_summary = prune_and_measure(axes, me_significant, listings)
            results.append({
                "run": run + 1,
                "coverage": coverage,
                "conflicts": conflicts,
                "n_axes": n_axes,
                "n_values": n_values,
                "axis_summary": axis_summary,
            })
            print(f"  Run {run+1}: Cover={coverage}% Confl={conflicts}% Axes={n_axes} Vals={n_values} [{axis_summary}]")

        # Consistency analysis
        valid = [r for r in results if r["axis_summary"] != "FAILED"]
        if len(valid) < 2:
            print(f"  Too few valid runs for analysis")
            continue

        coverages = [r["coverage"] for r in valid]
        conflicts_list = [r["conflicts"] for r in valid]
        summaries = [r["axis_summary"] for r in valid]

        print(f"\n  CONSISTENCY ({len(valid)}/{RUNS} valid):")
        print(f"    Coverage:  {min(coverages)}-{max(coverages)}% (range {max(coverages)-min(coverages):.1f}pp)")
        print(f"    Conflicts: {min(conflicts_list)}-{max(conflicts_list)}%")

        unique_summaries = set(summaries)
        print(f"    Unique axis structures: {len(unique_summaries)} of {len(valid)}")
        for s in unique_summaries:
            count = summaries.count(s)
            print(f"      [{s}] x{count}")


if __name__ == "__main__":
    main()
