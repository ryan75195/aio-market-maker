"""
V3: Hybrid approach — deterministic heavy lifting + constrained LLM.

Changes from v2:
1. Pre-filter: only send n-grams matching >3% of listings (cuts noise)
2. Pre-compute ME pairs from data and send as hints to LLM
3. Post-validate: check axis values are actually ME in the data, flag violations
4. Tighter prompt: LLM sees ME pairs, knows which n-grams compete
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
MIN_MATCH_PCT = 0.03  # Only send n-grams matching >3% of listings


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


def _are_numeric_variants(a, b):
    """Check if two n-grams differ only in their numeric parts.

    E.g., '256gb' vs '512gb', 'size 10' vs 'size 11'.
    These are semantically similar but functionally different products.
    """
    pattern_a = re.sub(r'\d+', '#', a)
    pattern_b = re.sub(r'\d+', '#', b)
    if pattern_a != pattern_b:
        return False
    if '#' not in pattern_a:
        return False
    nums_a = re.findall(r'\d+', a)
    nums_b = re.findall(r'\d+', b)
    return nums_a != nums_b


def dedup_ngrams(ngrams, equiv_threshold=0.95):
    texts = list(ngrams.keys())
    if len(texts) == 0:
        return {}

    all_vectors = []
    batch_size = 2048
    for i in range(0, len(texts), batch_size):
        batch = texts[i:i + batch_size]
        response = client.embeddings.create(
            model="text-embedding-3-large", input=batch, dimensions=256
        )
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
                # Never merge n-grams that are numeric variants
                # (same structure, different numbers: 256gb vs 512gb)
                if _are_numeric_variants(texts[i], texts[j]):
                    continue
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


def compute_match_sets(listings, deduped_ngrams):
    match_sets = {}
    for canonical, info in deduped_ngrams.items():
        patterns = [p.lower() for p in info["forms"]]
        matching = set()
        for i, listing in enumerate(listings):
            title_lower = listing["title"].lower()
            if any(p in title_lower for p in patterns):
                matching.add(i)
        match_sets[canonical] = matching
    return match_sets


def filter_significant_ngrams(deduped, match_sets, total_listings):
    """Keep only n-grams matching >MIN_MATCH_PCT of listings."""
    min_matches = int(total_listings * MIN_MATCH_PCT)
    significant = {}
    for canonical, info in deduped.items():
        if len(match_sets[canonical]) >= min_matches:
            significant[canonical] = info
    return significant


def compute_me_pairs(canonicals, match_sets):
    """Find all mutually exclusive pairs (overlap < threshold)."""
    me_pairs = []
    for i in range(len(canonicals)):
        for j in range(i + 1, len(canonicals)):
            a_set = match_sets[canonicals[i]]
            b_set = match_sets[canonicals[j]]
            if len(a_set) == 0 or len(b_set) == 0:
                continue
            intersection = len(a_set & b_set)
            overlap = intersection / min(len(a_set), len(b_set))
            if overlap < ME_THRESHOLD:
                me_pairs.append((canonicals[i], canonicals[j], overlap))
    return me_pairs


def compute_cooccur_pairs(canonicals, match_sets):
    """Find pairs that frequently co-occur (overlap > 50%)."""
    cooccur = []
    for i in range(len(canonicals)):
        for j in range(i + 1, len(canonicals)):
            a_set = match_sets[canonicals[i]]
            b_set = match_sets[canonicals[j]]
            if len(a_set) == 0 or len(b_set) == 0:
                continue
            intersection = len(a_set & b_set)
            overlap = intersection / min(len(a_set), len(b_set))
            if overlap > 0.5:
                cooccur.append((canonicals[i], canonicals[j], overlap))
    return cooccur


MIN_ME_PAIRS = 3  # Require participation in at least N ME pairs to be a candidate

def filter_to_me_participants(significant, me_pairs):
    """Keep only n-grams that participate in at least MIN_ME_PAIRS ME pairs."""
    me_counts = Counter()
    for a, b, _ in me_pairs:
        me_counts[a] += 1
        me_counts[b] += 1
    filtered = {k: v for k, v in significant.items() if me_counts.get(k, 0) >= MIN_ME_PAIRS}
    return filtered


def dedup_substrings(candidates, match_sets):
    """Smart substring dedup: remove redundant n-grams, but distinguish
    standalone words from fragments of multi-word names.

    Strategy:
    - If shorter A is contained in longer B (word-level), and A covers >=80% of B's listings:
      - Check if A is standalone (appears in >30% of cases WITHOUT being part of B)
      - If standalone: remove B (A is a complete concept, e.g., "disc" > "disc edition")
      - If fragment: remove A (A is always part of B, e.g., "ascended" < "ascended heroes")
    """
    canonicals = sorted(candidates.keys(), key=lambda k: len(k))  # shortest first
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
            # Check if shorter is a substring of longer (word-level)
            shorter_words = set(shorter.split())
            longer_words = set(longer.split())
            if not shorter_words.issubset(longer_words):
                continue
            # Check listing overlap
            longer_set = match_sets[longer]
            if len(longer_set) == 0:
                continue
            # Does shorter cover most of longer's listings?
            coverage = len(shorter_set & longer_set) / len(longer_set)
            if coverage < 0.80:
                continue

            # Key decision: is shorter a standalone concept or a fragment?
            # standalone_ratio = fraction of shorter's matches that DON'T also match longer
            standalone_matches = len(shorter_set - longer_set)
            standalone_ratio = standalone_matches / len(shorter_set)

            if standalone_ratio > 0.20:
                # Shorter is standalone (e.g., "disc" often appears without "edition")
                # → remove the longer form
                to_remove.add(longer)
            else:
                # Shorter is a fragment (e.g., "ascended" almost always comes with "heroes")
                # → remove the shorter form, keep the longer
                to_remove.add(shorter)
                break  # shorter is removed, skip remaining longer candidates

    filtered = {k: v for k, v in candidates.items() if k not in to_remove}
    if to_remove:
        print(f"    Substring dedup removed {len(to_remove)}: {sorted(to_remove)[:10]}{'...' if len(to_remove) > 10 else ''}")
    return filtered


def identify_axes_with_hints(me_significant, match_sets, me_pairs, cooccur_pairs, search_term, total_listings):
    """LLM groups pre-filtered axis-value candidates into axes.

    me_significant: only n-grams that participate in ME pairs (all are axis values).
    """

    # Build n-gram lines with match counts
    ngram_lines = []
    for canonical in sorted(me_significant.keys(), key=lambda c: -len(match_sets[c])):
        info = me_significant[canonical]
        match_count = len(match_sets[canonical])
        pct = 100 * match_count / total_listings
        forms_str = ""
        if len(info["forms"]) > 1:
            forms_str = f" (also: {', '.join(info['forms'][1:])})"
        ngram_lines.append(f"  {canonical}: {match_count} matches ({pct:.1f}%){forms_str}")

    # Build ME pairs block — only pairs where BOTH members are in me_significant
    me_filtered = [(a, b, ov) for a, b, ov in me_pairs if a in me_significant and b in me_significant]
    me_pairs_sorted = sorted(me_filtered, key=lambda x: -(len(match_sets[x[0]]) + len(match_sets[x[1]])))
    me_lines = []
    for a, b, ov in me_pairs_sorted[:60]:
        me_lines.append(f"  {a} <-> {b} (overlap {ov:.1%})")

    # Build co-occur block — only pairs where BOTH members are in me_significant
    cooccur_filtered = [(a, b, ov) for a, b, ov in cooccur_pairs if a in me_significant and b in me_significant]
    cooccur_sorted = sorted(cooccur_filtered, key=lambda x: -x[2])
    cooccur_lines = []
    for a, b, ov in cooccur_sorted[:20]:
        cooccur_lines.append(f"  {a} + {b} (co-occur {ov:.0%})")

    ngram_block = "\n".join(ngram_lines)
    me_block = "\n".join(me_lines)
    cooccur_block = "\n".join(cooccur_lines)

    system_prompt = f"""You are organizing eBay listing data for "{search_term}" ({total_listings} listings).

You are given:
1. {len(me_significant)} axis-value candidates (pre-filtered to only n-grams with strong mutual exclusivity evidence)
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
{ngram_block}

MUTUALLY EXCLUSIVE pairs (never in same title — candidates for same axis):
{me_block}

CO-OCCURRING pairs (frequently together — must be on DIFFERENT axes):
{cooccur_block}"""

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
    print(f"  LLM response: {len(content)} chars, finish_reason={finish_reason}")
    if finish_reason != "stop":
        print(f"  WARNING: Response truncated! First 500 chars: {content[:500]}")
        raise RuntimeError(f"LLM response truncated (finish_reason={finish_reason})")

    return json.loads(content)["axes"]


def pattern_matches(pattern, title_lower):
    """Match using word boundaries for single-word patterns, substring for multi-word."""
    if " " not in pattern:
        return bool(re.search(r'\b' + re.escape(pattern) + r'\b', title_lower))
    return pattern in title_lower


def compute_value_match_set(value_patterns, listings):
    """Compute which listings match a set of patterns."""
    matches = set()
    for i, listing in enumerate(listings):
        title_lower = listing["title"].lower()
        if any(pattern_matches(p, title_lower) for p in value_patterns):
            matches.add(i)
    return matches


def prune_overlapping_values(axes, significant, listings):
    """Auto-prune values within each axis that overlap >20%.

    Strategy:
    - Compute actual match sets for each value (using word-boundary matching)
    - If two values overlap >20%, keep the more specific (fewer matches) if one
      is a substring of the other, otherwise keep the one with more matches
    - Remove values with <10 matches after word-boundary matching
    """
    pruned_axes = []
    for axis in axes:
        # Compute match sets for each value
        value_data = []
        for value in axis["values"]:
            patterns = []
            for ng in value["ngrams"]:
                if ng in significant:
                    patterns.extend([f.lower() for f in significant[ng]["forms"]])
                else:
                    patterns.append(ng.lower())
            # Remove patterns that are too short (< 2 chars) or pure numbers < 3 digits
            patterns = [p for p in patterns if len(p) >= 2 and not (p.isdigit() and len(p) < 3)]
            match_set = compute_value_match_set(patterns, listings)
            value_data.append({
                "label": value["label"],
                "ngrams": value["ngrams"],
                "patterns": patterns,
                "match_set": match_set,
            })

        # Remove values with too few matches
        value_data = [v for v in value_data if len(v["match_set"]) >= 10]

        # Iterative prune: remove the value with the most overlapping partners
        # until no pair overlaps >20%
        remaining = list(value_data)
        while True:
            # Compute overlap counts for each remaining value
            overlap_counts = {i: 0 for i in range(len(remaining))}
            overlap_pairs = []
            for i in range(len(remaining)):
                for j in range(i + 1, len(remaining)):
                    a = remaining[i]["match_set"]
                    b = remaining[j]["match_set"]
                    if len(a) == 0 or len(b) == 0:
                        continue
                    intersection = len(a & b)
                    ov = intersection / min(len(a), len(b))
                    if ov > 0.20:
                        overlap_counts[i] += 1
                        overlap_counts[j] += 1
                        overlap_pairs.append((i, j, ov))

            if not overlap_pairs:
                break

            # Remove the value involved in the most overlaps
            # Tie-break: prefer removing the one with FEWER unique matches
            worst_idx = max(
                overlap_counts.keys(),
                key=lambda i: (overlap_counts[i], -len(remaining[i]["match_set"]))
            )
            if overlap_counts[worst_idx] == 0:
                break
            removed = remaining.pop(worst_idx)
            print(f"    Pruned '{removed['label']}' from {axis['name']} (overlapped with {overlap_counts[worst_idx]} values)")

        kept = remaining

        if len(kept) >= 2:
            pruned_axes.append({
                "name": axis["name"],
                "values": [{"label": v["label"], "ngrams": v["ngrams"]} for v in kept],
            })

    return pruned_axes


def validate_axes(axes, significant, listings, drop_bad=False):
    """Post-validate: check that axis values are actually ME in the data.

    If drop_bad=True, drop entire axes where >30% of value pairs violate ME.
    """
    print(f"\n  POST-VALIDATION:")
    total_violations = 0
    clean_axes = []

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
            axis_values.append({"label": value["label"], "patterns": patterns, "match_set": match_set})

        violations = 0
        total_pairs = 0
        for i in range(len(axis_values)):
            for j in range(i + 1, len(axis_values)):
                a = axis_values[i]["match_set"]
                b = axis_values[j]["match_set"]
                if len(a) == 0 or len(b) == 0:
                    continue
                total_pairs += 1
                intersection = len(a & b)
                overlap = intersection / min(len(a), len(b))
                if overlap >= ME_THRESHOLD:
                    violations += 1
                    total_violations += 1
                    print(f"    WARNING: {axis['name']}: '{axis_values[i]['label']}' and '{axis_values[j]['label']}' overlap {overlap:.1%}")

        violation_pct = violations / total_pairs if total_pairs > 0 else 0
        if drop_bad and violation_pct > 0.30:
            print(f"    DROPPED: {axis['name']} ({violations}/{total_pairs} pairs violate ME = {violation_pct:.0%})")
        else:
            clean_axes.append(axis)

    if total_violations == 0:
        print(f"    All axis values are mutually exclusive in the data.")

    if drop_bad:
        return clean_axes
    return total_violations


def assign_and_measure(listings, axes, significant):
    """Assign listings using word-boundary matching and return metrics."""
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
            # Filter short/numeric patterns
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
                # Substring resolution (still useful for multi-word patterns)
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

    total = len(listings)
    coverage_pct = 100 * labeled_count / total if total > 0 else 0
    conflict_pct = 100 * conflict_count / total if total > 0 else 0
    num_categories = len([c for c in category_counter if c != "Unlabeled"])
    cat_sizes = [v for k, v in category_counter.items() if k != "Unlabeled"]
    median_cat_size = sorted(cat_sizes)[len(cat_sizes) // 2] if cat_sizes else 0

    return {
        "coverage_pct": round(coverage_pct, 1),
        "conflict_pct": round(conflict_pct, 1),
        "num_categories": num_categories,
        "median_cat_size": median_cat_size,
        "category_counter": category_counter,
        "axis_matchers": axis_matchers,
    }


def main():
    search_terms = {1: "PlayStation 5 Console", 1029: "Pokemon Booster Box Sealed", 1121: "Brompton Folding Bike"}
    search_term = search_terms.get(JOB_ID, f"Job {JOB_ID}")

    print(f"=== Taxonomy V3: {search_term} (job {JOB_ID}) ===\n")

    print("Loading listings...")
    listings = load_listings(JOB_ID)
    total = len(listings)
    print(f"  {total} listings\n")

    print("Extracting n-grams...")
    ngrams = extract_ngrams(listings)
    print(f"  {len(ngrams)} raw n-grams\n")

    print("Deduplicating with embeddings (0.95)...")
    deduped = dedup_ngrams(ngrams, 0.95)
    print(f"  {len(deduped)} after dedup\n")

    print("Computing match sets...")
    match_sets = compute_match_sets(listings, deduped)

    print(f"Filtering to significant n-grams (>{MIN_MATCH_PCT:.0%} of listings = {int(total * MIN_MATCH_PCT)} matches)...")
    significant = filter_significant_ngrams(deduped, match_sets, total)
    print(f"  {len(significant)} significant n-grams (from {len(deduped)})\n")

    sig_canonicals = list(significant.keys())

    print("Computing ME pairs among significant n-grams...")
    me_pairs = compute_me_pairs(sig_canonicals, match_sets)
    print(f"  {len(me_pairs)} ME pairs\n")

    print("Computing co-occurring pairs...")
    cooccur_pairs = compute_cooccur_pairs(sig_canonicals, match_sets)
    print(f"  {len(cooccur_pairs)} co-occurring pairs\n")

    print("Filtering to ME-participating n-grams only...")
    me_significant = filter_to_me_participants(significant, me_pairs)
    print(f"  {len(me_significant)} ME participants (from {len(significant)} significant)")

    print("Smart substring dedup...")
    me_significant = dedup_substrings(me_significant, match_sets)
    print(f"  {len(me_significant)} after substring dedup\n")

    print("Calling LLM (tighter prompt — pre-filtered candidates)...")
    for attempt in range(3):
        try:
            axes = identify_axes_with_hints(me_significant, match_sets, me_pairs, cooccur_pairs, search_term, total)
            break
        except RuntimeError as e:
            if "truncated" in str(e) and attempt < 2:
                print(f"  Retry {attempt + 2}/3 (repetition loop)...")
                continue
            raise

    print(f"\n  LLM returned {len(axes)} axes:")
    for ax in axes:
        vals = [f"{v['label']}({','.join(v['ngrams'])})" for v in ax["values"]]
        print(f"    {ax['name']}: [{', '.join(vals)}]")

    # Post-validate BEFORE pruning
    print(f"\n  Pre-prune validation:")
    validate_axes(axes, me_significant, listings)

    # Auto-prune overlapping values
    print(f"\nPruning overlapping values (word-boundary matching + overlap >20% removal)...")
    axes = prune_overlapping_values(axes, significant, listings)
    print(f"  {len(axes)} axes after pruning:")
    for ax in axes:
        vals = [f"{v['label']}({','.join(v['ngrams'])})" for v in ax["values"]]
        print(f"    {ax['name']}: [{', '.join(vals)}]")

    # Post-validate AFTER pruning — drop axes where >30% of pairs violate ME
    axes = validate_axes(axes, me_significant, listings, drop_bad=True)
    print(f"  {len(axes)} axes after ME validation:")
    for ax in axes:
        vals = [f"{v['label']}({','.join(v['ngrams'])})" for v in ax["values"]]
        print(f"    {ax['name']}: [{', '.join(vals)}]")

    # Assign and measure
    print(f"\nAssigning listings (word-boundary matching)...")
    metrics = assign_and_measure(listings, axes, me_significant)

    print(f"\n{'='*60}")
    print(f"RESULTS: {search_term}")
    print(f"{'='*60}")
    print(f"  Coverage:   {metrics['coverage_pct']}%")
    print(f"  Conflicts:  {metrics['conflict_pct']}%")
    print(f"  Categories: {metrics['num_categories']}")
    print(f"  Median cat: {metrics['median_cat_size']}")
    print(f"  Axes:       {len(axes)}")

    print(f"\n  Top 20 categories:")
    cats = [(k, v) for k, v in metrics['category_counter'].items() if k != "Unlabeled"]
    cats.sort(key=lambda x: -x[1])
    for cat, count in cats[:20]:
        print(f"    {cat}: {count}")

    unlabeled = metrics['category_counter'].get("Unlabeled", 0)
    print(f"\n  Unlabeled: {unlabeled}")

    # Show axis detail
    print(f"\n  Axes detail:")
    for ax in metrics['axis_matchers']:
        print(f"    {ax['name']}:")
        for val in ax['values']:
            # Count matches
            count = 0
            for listing in listings:
                if any(p in listing["title"].lower() for p in val["patterns"]):
                    count += 1
            print(f"      {val['label']}: {count} matches (patterns: {val['patterns']})")


if __name__ == "__main__":
    main()
