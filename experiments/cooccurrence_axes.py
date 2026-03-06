"""
V3 Experiment: Co-occurrence + Semantic Similarity axis discovery.

Key insight from V3a failure: mutual exclusivity alone is too weak.
Most rare n-grams are ME by chance. Need BOTH:
  1. Mutually exclusive in data (overlap < 5%)
  2. Semantically similar (cosine > 0.3)

Pipeline:
1. Extract n-grams + dedup with embeddings
2. Compute match sets + pairwise overlap
3. Compute pairwise semantic similarity (from dedup embeddings)
4. Build axis graph: edge if ME AND semantically similar
5. Find connected components -> candidate axes
6. LLM names the axes
7. Assign listings via substring match
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
ME_THRESHOLD = 0.05       # Max overlap to consider "mutually exclusive"
SIM_THRESHOLDS = [0.4, 0.5, 0.6]  # Test multiple similarity thresholds
MIN_CLIQUE_SIZE = 2


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


def dedup_ngrams_with_vectors(ngrams, equiv_threshold=0.95):
    """Dedup AND return embedding vectors for downstream semantic similarity."""
    texts = list(ngrams.keys())
    if len(texts) == 0:
        return {}, np.array([]), []

    # Batch embed (may need multiple calls for large sets)
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
                union(i, j)

    groups = defaultdict(list)
    for i in range(len(texts)):
        groups[find(i)].append(i)

    deduped = {}
    canonical_vectors = []
    canonical_list = []
    for group_indices in groups.values():
        members = [(texts[i], ngrams[texts[i]]) for i in group_indices]
        members.sort(key=lambda x: -x[1])
        canonical = members[0][0]
        deduped[canonical] = {
            "freq": sum(f for _, f in members),
            "forms": [m[0] for m in members],
        }
        # Use the highest-freq member's vector as canonical
        best_idx = group_indices[0]
        for idx in group_indices:
            if ngrams[texts[idx]] > ngrams[texts[best_idx]]:
                best_idx = idx
        canonical_vectors.append(normed[best_idx])
        canonical_list.append(canonical)

    return deduped, np.array(canonical_vectors), canonical_list


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


def compute_overlap_matrix(canonicals, match_sets):
    n = len(canonicals)
    overlap = np.zeros((n, n))
    for i in range(n):
        for j in range(i + 1, n):
            a = match_sets[canonicals[i]]
            b = match_sets[canonicals[j]]
            if len(a) == 0 or len(b) == 0:
                continue
            intersection = len(a & b)
            denom = min(len(a), len(b))
            overlap[i][j] = intersection / denom
            overlap[j][i] = overlap[i][j]
    return overlap


def find_axes(canonicals, match_sets, overlap, semantic_sim, sim_threshold, min_match=20):
    """Find groups of n-grams that are BOTH mutually exclusive AND semantically similar."""
    n = len(canonicals)

    # Filter to n-grams with enough matches
    valid = [i for i in range(n) if len(match_sets[canonicals[i]]) >= min_match]

    # Build combined graph: edge if ME AND semantically similar
    adj = defaultdict(set)
    me_count = 0
    both_count = 0

    for idx_a in range(len(valid)):
        for idx_b in range(idx_a + 1, len(valid)):
            i, j = valid[idx_a], valid[idx_b]
            is_me = overlap[i][j] < ME_THRESHOLD
            is_sim = semantic_sim[i][j] > sim_threshold

            if is_me:
                me_count += 1
            if is_me and is_sim:
                both_count += 1
                adj[i].add(j)
                adj[j].add(i)

    # Find connected components
    visited = set()
    components = []

    def bfs(start):
        queue = [start]
        component = set()
        while queue:
            node = queue.pop(0)
            if node in visited:
                continue
            visited.add(node)
            component.add(node)
            for neighbor in adj.get(node, set()):
                if neighbor not in visited:
                    queue.append(neighbor)
        return component

    for i in valid:
        if i not in visited and i in adj:
            comp = bfs(i)
            if len(comp) >= MIN_CLIQUE_SIZE:
                components.append(sorted(comp, key=lambda x: -len(match_sets[canonicals[x]])))

    # Sort components by total match coverage
    components.sort(key=lambda comp: -sum(len(match_sets[canonicals[i]]) for i in comp))

    return components, valid, me_count, both_count


def pick_axis_values(component, canonicals, match_sets, overlap):
    """From a connected component, pick the best non-overlapping values.

    A component may have many n-grams that overlap with each other
    (e.g., "disc", "disc edition", "ps5 disc edition" are all connected
    via semantic similarity + ME with other axis values).

    Pick the smallest set of values that are pairwise ME within the axis.
    """
    # Sort by match count descending — prefer popular n-grams
    members = sorted(component, key=lambda i: -len(match_sets[canonicals[i]]))

    selected = []
    for candidate in members:
        # Check ME with all already-selected values
        if all(overlap[candidate][s] < ME_THRESHOLD for s in selected):
            selected.append(candidate)

    return selected


def name_axes_with_llm(axes_with_values, canonicals, deduped_ngrams, match_sets, search_term):
    """LLM names each axis. Trivial task."""
    axis_descriptions = []
    for i, values in enumerate(axes_with_values):
        members = []
        for idx in values:
            ng = canonicals[idx]
            count = len(match_sets[ng])
            members.append(f"{ng} ({count} matches)")
        axis_descriptions.append(f"Axis {i+1}: [{', '.join(members)}]")

    prompt_block = "\n".join(axis_descriptions)

    response = client.chat.completions.create(
        model=MODEL,
        messages=[
            {"role": "system", "content": f"""You are naming product taxonomy axes for eBay listings of "{search_term}".
Each group contains values of a single product dimension - they are mutually exclusive (never appear in the same listing).

Your ONLY job: give each axis a short, descriptive name (1-3 words).
Examples: "Storage", "Color", "Model", "Edition Type", "Speed", "Language"

Do NOT reorganize, split, or merge the groups. Just name them."""},
            {"role": "user", "content": f"Groups to name:\n{prompt_block}"},
        ],
        response_format={
            "type": "json_schema",
            "json_schema": {
                "name": "axis_names",
                "strict": True,
                "schema": {
                    "type": "object",
                    "properties": {
                        "axes": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "axis_index": {"type": "integer"},
                                    "name": {"type": "string"},
                                },
                                "required": ["axis_index", "name"],
                                "additionalProperties": False,
                            },
                        }
                    },
                    "required": ["axes"],
                    "additionalProperties": False,
                },
            },
        },
        temperature=0,
    )

    result = json.loads(response.choices[0].message.content)
    names = {}
    for ax in result["axes"]:
        names[ax["axis_index"] - 1] = ax["name"]
    return names


def assign_listings(listings, axes_with_values, canonicals, deduped_ngrams, axis_names):
    """Assign listings to categories via substring matching."""
    axis_matchers = []
    for ax_idx, values in enumerate(axes_with_values):
        axis_name = axis_names.get(ax_idx, f"Axis {ax_idx+1}")
        val_list = []
        for ng_idx in values:
            canonical = canonicals[ng_idx]
            patterns = [p.lower() for p in deduped_ngrams[canonical]["forms"]]
            val_list.append({"label": canonical, "patterns": patterns})
        axis_matchers.append({"name": axis_name, "values": val_list})

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
                if any(p in title_lower for p in value["patterns"]):
                    matched_values.append(value["label"])

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

    print(f"=== Co-occurrence + Semantic Axis Discovery: {search_term} (job {JOB_ID}) ===\n")

    print("Loading listings...")
    listings = load_listings(JOB_ID)
    print(f"  {len(listings)} listings\n")

    print("Extracting n-grams...")
    ngrams = extract_ngrams(listings)
    print(f"  {len(ngrams)} n-grams\n")

    print("Deduplicating with embeddings + getting vectors...")
    deduped, vectors, canonicals = dedup_ngrams_with_vectors(ngrams, 0.95)
    print(f"  {len(deduped)} after dedup, vectors shape: {vectors.shape}\n")

    # Compute semantic similarity between canonical n-grams
    semantic_sim = vectors @ vectors.T
    print(f"  Semantic similarity matrix: {semantic_sim.shape}")

    print("\nComputing match sets...")
    match_sets = compute_match_sets(listings, deduped)

    print(f"\nComputing pairwise overlap matrix...")
    overlap = compute_overlap_matrix(canonicals, match_sets)

    min_match = max(20, len(listings) // 200)

    # Sweep similarity thresholds
    for sim_thresh in SIM_THRESHOLDS:
        print(f"\n{'='*60}")
        print(f"SIM_THRESHOLD = {sim_thresh}")
        print(f"{'='*60}")

        components, valid, me_count, both_count = find_axes(
            canonicals, match_sets, overlap, semantic_sim, sim_thresh, min_match=min_match
        )
        print(f"  {len(valid)} valid n-grams, ME pairs: {me_count}, ME+Sim pairs: {both_count}")
        print(f"  {len(components)} connected components:")
        for i, comp in enumerate(components[:12]):
            members = [(canonicals[idx], len(match_sets[canonicals[idx]])) for idx in comp[:6]]
            member_str = ", ".join(f"{ng} ({ct})" for ng, ct in members)
            suffix = f" +{len(comp)-6} more" if len(comp) > 6 else ""
            print(f"    C{i+1} ({len(comp)}): {member_str}{suffix}")
        if len(components) > 12:
            print(f"    ... and {len(components) - 12} more")

        # Pick axis values and run assignment
        axes_with_values = []
        for comp in components:
            values = pick_axis_values(comp, canonicals, match_sets, overlap)
            if len(values) >= MIN_CLIQUE_SIZE:
                axes_with_values.append(values)
        axes_with_values = axes_with_values[:8]

        if not axes_with_values:
            print("  No usable axes!")
            continue

        print(f"\n  {len(axes_with_values)} axes (after picking ME values):")
        for i, values in enumerate(axes_with_values):
            members = [(canonicals[idx], len(match_sets[canonicals[idx]])) for idx in values]
            member_str = ", ".join(f"{ng} ({ct})" for ng, ct in members)
            print(f"    Axis {i+1} ({len(values)} vals): {member_str}")

        # Quick assignment without LLM naming
        metrics = assign_listings(listings, axes_with_values, canonicals, deduped, {i: f"Axis{i+1}" for i in range(len(axes_with_values))})
        print(f"\n  Coverage: {metrics['coverage_pct']}%  Conflicts: {metrics['conflict_pct']}%  Categories: {metrics['num_categories']}  Median: {metrics['median_cat_size']}")


if __name__ == "__main__":
    main()
