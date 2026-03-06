"""
V5 Taxonomy: Graph-based discovery + LLM cleanup.

Pipeline:
1. N-gram extraction + significance filter (deterministic)
2. ME pairs + embedding similarity -> raw cliques (deterministic)
3. LLM merges/cleans cliques into final axes (one call)
4. Post-processing: prune overlaps, assign listings
"""

import sys, json, re, numpy as np
from collections import defaultdict, Counter
from openai import OpenAI
from sklearn.metrics.pairwise import cosine_similarity

# ── Settings ──
MIN_MATCH_PCT = 0.03
ME_THRESHOLD = 0.05
MIN_ME_PAIRS = 3
MIN_COHERENCE = 0.28
MIN_AXIS_COVERAGE = 0.08

sys.argv = ['taxonomy_v5.py']
from taxonomy_v3 import (load_listings, extract_ngrams, dedup_ngrams, compute_match_sets,
                          filter_significant_ngrams, compute_me_pairs, filter_to_me_participants,
                          dedup_substrings, assign_and_measure, compute_value_match_set)

with open('../AIOMarketMaker.Console/local.settings.json') as f:
    _settings = json.load(f)
_openai = OpenAI(api_key=_settings['OpenAi']['ApiKey'])


def get_embeddings(terms):
    if not terms:
        return {}
    resp = _openai.embeddings.create(input=terms, model='text-embedding-3-small')
    return {terms[i]: np.array(resp.data[i].embedding) for i in range(len(terms))}


def bron_kerbosch(R, P, X, adj, cliques):
    if not P and not X:
        if len(R) >= 2:
            cliques.append(frozenset(R))
        return
    pivot = max(P | X, key=lambda v: len(adj[v] & P))
    for v in list(P - adj[pivot]):
        bron_kerbosch(R | {v}, P & adj[v], X & adj[v], adj, cliques)
        P.remove(v)
        X.add(v)


def find_raw_communities(candidates, me_pairs, me_significant, match_sets, total):
    """Find communities in ME+embedding graph using Louvain, then validate."""
    import networkx as nx
    import community as community_louvain

    embeddings = get_embeddings(candidates)

    # Build ME adjacency
    me_adj = defaultdict(set)
    for a, b, ov in me_pairs:
        if a in me_significant and b in me_significant:
            me_adj[a].add(b)
            me_adj[b].add(a)

    # Build graph: ME edge weighted by embedding similarity
    G = nx.Graph()
    G.add_nodes_from(candidates)
    for a in candidates:
        for b in me_adj[a]:
            if a < b:
                emb_sim = float(cosine_similarity([embeddings[a]], [embeddings[b]])[0][0])
                if emb_sim > 0.15:
                    G.add_edge(a, b, weight=emb_sim)

    if G.number_of_edges() == 0:
        return [], candidates

    # Louvain community detection (higher resolution = smaller communities)
    partition = community_louvain.best_partition(G, resolution=2.0, random_state=42)

    # Group by community
    comm_groups = defaultdict(list)
    for node, comm_id in partition.items():
        comm_groups[comm_id].append(node)

    # Score each community
    selected = []
    for comm_id, members in comm_groups.items():
        if len(members) < 2:
            continue

        members_sorted = sorted(members, key=lambda k: -len(match_sets[k]))

        # ME density
        me_count = 0
        total_pairs = 0
        for i in range(len(members_sorted)):
            for j in range(i + 1, len(members_sorted)):
                total_pairs += 1
                if members_sorted[j] in me_adj[members_sorted[i]]:
                    me_count += 1
        me_density = me_count / total_pairs if total_pairs > 0 else 0

        # Embedding coherence
        embs = np.array([embeddings[m] for m in members_sorted])
        sim_matrix = cosine_similarity(embs)
        n = len(members_sorted)
        avg_sim = (sim_matrix.sum() - n) / (n * (n - 1))

        # Coverage
        covered = set()
        for m in members_sorted:
            covered |= match_sets[m]
        coverage = len(covered) / total

        selected.append({
            'members': members_sorted,
            'sim': avg_sim,
            'me_density': me_density,
            'coverage': coverage,
        })

    selected.sort(key=lambda x: -x['coverage'])

    # Unassigned = singletons from Louvain
    assigned = set()
    for s in selected:
        assigned.update(s['members'])
    modifiers = [c for c in candidates if c not in assigned and len(match_sets[c]) / total >= 0.04]

    return selected, modifiers


def llm_clean_communities(raw_communities, modifiers, match_sets, total, search_term):
    """LLM merges/cleans raw communities into named axes. One call."""

    # Build community descriptions
    clique_lines = []
    for i, cl in enumerate(raw_communities):
        members_str = ', '.join(f'{m} ({100*len(match_sets[m])/total:.0f}%)' for m in cl['members'])
        me_pct = f"{cl['me_density']:.0%}" if 'me_density' in cl else "?"
        clique_lines.append(f"  Group {i+1} (ME density {me_pct}, embedding sim {cl['sim']:.2f}, coverage {cl['coverage']*100:.0f}%): [{members_str}]")

    modifier_lines = []
    for m in sorted(modifiers, key=lambda k: -len(match_sets[k])):
        modifier_lines.append(f"  {m} ({100*len(match_sets[m])/total:.0f}%)")

    clique_block = "\n".join(clique_lines)
    modifier_block = "\n".join(modifier_lines) if modifier_lines else "  (none)"

    system_prompt = f"""You are cleaning up auto-discovered product taxonomy groups for "{search_term}" ({total} listings).

The system found candidate axis groups using mutual-exclusivity statistics and embedding similarity.
Your job: merge, split, or relabel these into clean taxonomy axes.

RULES:
1. MERGE groups that represent the same axis (e.g., if one group has "825gb, 1tb" and another has "2tb", merge into one Storage axis)
2. MOVE misplaced members between groups (e.g., "pro" in a storage group should move to a model axis)
3. PROMOTE modifiers to axes if they clearly belong (e.g., "slim" as a modifier should join a model axis with "pro")
4. Each axis: 2-10 values, name it clearly. Values within an axis must be alternatives (you would pick ONE, not combine them). If a group has too many mixed concepts, SPLIT into separate axes rather than merging everything.
5. Skip generic terms (brand names, the search term itself, "sony", "apple", etc.)
6. Leftover n-grams that modify but do not define variants -> list as "modifiers" (e.g., "bundle", "sealed", "boxed")
7. CONTAMINANTS: ONLY flag n-grams that represent genuinely DIFFERENT products (e.g., "portal" in a PS5 search = different product, "case" in an iPhone search = accessory). Do NOT flag product-relevant terms as contaminants. When in doubt, keep it as a modifier, not a contaminant.
8. Be CONSERVATIVE with contaminants. Most n-grams are relevant - they describe the product or its variants. Only flag clear wrong-product matches.
9. CRITICAL: Do NOT mix different attribute types on one axis. Colorway names (bred, royal, chicago, shadow) go on a Color/Colorway axis. Model variants (se, retro og, gs) go on a Model axis. Sizes go on a Size axis. If a group mixes these, SPLIT it."""

    user_prompt = f"""Raw groups (auto-discovered via ME statistics + embedding similarity):
{clique_block}

Unassigned modifiers:
{modifier_block}

Return JSON with this schema:
{{
  "axes": [
    {{"name": "axis name", "values": ["ngram1", "ngram2", ...]}},
    ...
  ],
  "modifiers": ["ngram1", "ngram2", ...],
  "contaminants": ["ngram1", "ngram2", ...]
}}"""

    response = _openai.chat.completions.create(
        model="gpt-4.1-mini",
        messages=[
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": user_prompt}
        ],
        response_format={"type": "json_schema", "json_schema": {
            "name": "taxonomy",
            "strict": True,
            "schema": {
                "type": "object",
                "properties": {
                    "axes": {"type": "array", "items": {
                        "type": "object",
                        "properties": {
                            "name": {"type": "string"},
                            "values": {"type": "array", "items": {"type": "string"}}
                        },
                        "required": ["name", "values"],
                        "additionalProperties": False
                    }},
                    "modifiers": {"type": "array", "items": {"type": "string"}},
                    "contaminants": {"type": "array", "items": {"type": "string"}}
                },
                "required": ["axes", "modifiers", "contaminants"],
                "additionalProperties": False
            }
        }},
        max_completion_tokens=2048,
        temperature=0.0,
    )

    result = json.loads(response.choices[0].message.content)

    # Convert to standard axis format
    axes = []
    for ax in result['axes']:
        valid_values = [v for v in ax['values'] if v in match_sets]
        if len(valid_values) >= 2:
            axes.append({
                'name': ax['name'],
                'values': [{'label': v, 'ngrams': [v]} for v in valid_values]
            })

    return axes, result.get('modifiers', []), result.get('contaminants', [])


def llm_suggest_regex_axes(axes, listings, search_term, total):
    """Ask LLM if there are structured patterns (model numbers, set codes, etc.)
    that n-grams missed because values are too fragmented.

    Constraints:
    - Only for structured identifiers the LLM knows from world knowledge
    - Must not duplicate existing axes
    - Validated against data before injection
    """
    # Build context: current axes + sample titles
    axis_summary = []
    for ax in axes:
        vals = [v['label'] for v in ax['values']]
        axis_summary.append(f"  {ax['name']}: {', '.join(vals)}")
    axis_block = "\n".join(axis_summary) if axis_summary else "  (none discovered)"

    # Sample diverse titles (first 80, skip duplicates)
    seen = set()
    sample_titles = []
    for l in listings:
        t = l['title'].strip()
        if t not in seen:
            seen.add(t)
            sample_titles.append(t)
        if len(sample_titles) >= 80:
            break
    title_block = "\n".join(f"  {t}" for t in sample_titles[:80])

    system_prompt = f"""You are analyzing eBay listing titles for "{search_term}" to find structured product identifiers that statistical analysis missed.

The n-gram pipeline discovers axis values above 3% frequency. But some critical product identifiers have HUNDREDS of unique values each below 3% — these are invisible to n-gram analysis but essential for distinguishing products.

YOUR TASK: Look at the sample titles and identify any structured identifier patterns (numeric codes, model numbers, style codes) that appear frequently across listings. These are product-level identifiers that sellers consistently include because they matter.

RULES:
1. Suggest patterns where you recognize a domain-specific identifier in the titles
2. The regex MUST capture a structured pattern (numbers, codes) — NOT free-text
3. Do NOT duplicate already discovered axes
4. Use contextual anchors to avoid false positives (e.g., match "Set\\s*(\\d{{4,6}})" not bare "(\\d+)")
5. Each regex: Python-compatible, exactly ONE capture group for the value
6. Return empty array ONLY if you genuinely see no structured identifiers in the titles

EXAMPLES of identifiers to look for:
- LEGO: 4-6 digit set numbers (e.g., 75387, 40765). These appear as bare numbers in titles, NOT always after "Set" — use a pattern like \\b(\\d{{5}})\\b to match them broadly.
- Shoes: alphanumeric style codes (e.g., DD1391, FQ6965, 555088-106)
- Trading cards: set codes, collector numbers
- Electronics: model numbers (e.g., CFI-1215A, A2849)

IMPORTANT: Prefer BROADER patterns that capture the identifier regardless of surrounding words. Do NOT require context anchors like "Set" or "#" if the identifier appears as a bare number/code in most titles. The validation step will reject noisy patterns — your job is to capture the real identifiers. Suggest ONE best pattern per identifier type, not multiple variants."""

    user_prompt = f"""Already discovered axes:
{axis_block}

Sample titles ({total} total listings):
{title_block}

If there are structured identifier patterns in these titles that the existing axes missed, suggest them.
Return JSON — empty array is the expected default for most categories."""

    response = _openai.chat.completions.create(
        model="gpt-4.1-mini",
        messages=[
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": user_prompt}
        ],
        response_format={"type": "json_schema", "json_schema": {
            "name": "regex_axes",
            "strict": True,
            "schema": {
                "type": "object",
                "properties": {
                    "regex_axes": {"type": "array", "items": {
                        "type": "object",
                        "properties": {
                            "name": {"type": "string", "description": "Axis name (e.g. 'Set Number')"},
                            "pattern": {"type": "string", "description": "Python regex with ONE capture group"},
                            "description": {"type": "string", "description": "Why this pattern exists in this category"}
                        },
                        "required": ["name", "pattern", "description"],
                        "additionalProperties": False
                    }}
                },
                "required": ["regex_axes"],
                "additionalProperties": False
            }
        }},
        max_completion_tokens=1024,
        temperature=0.0,
    )

    suggestions = json.loads(response.choices[0].message.content)
    return suggestions.get('regex_axes', [])


def validate_and_inject_regex_axes(suggestions, axes, listings, total,
                                    min_unique=3, max_unique_pct=0.80,
                                    min_coverage_pct=0.05, max_coverage_pct=0.98):
    """Validate LLM-suggested regex patterns against actual data.

    Rejects patterns that:
    - Don't compile as valid regex
    - Match <5% or >90% of listings (too rare or too noisy)
    - Produce <3 unique values (not an axis)
    - Produce unique values > 80% of matches (each listing unique = noise)
    - Have no values with enough listings to price from
    """
    injected = []
    seen_patterns = set()

    for suggestion in suggestions:
        name = suggestion['name']
        pattern_str = suggestion['pattern']
        desc = suggestion['description']

        # 0. Dedup identical patterns
        if pattern_str in seen_patterns:
            print(f"    REJECT regex '{name}': duplicate pattern")
            continue
        seen_patterns.add(pattern_str)

        # 1. Compile regex
        try:
            regex = re.compile(pattern_str, re.IGNORECASE)
        except re.error as e:
            print(f"    REJECT regex '{name}': invalid regex — {e}")
            continue

        # 2. Run against all titles, extract values
        value_counts = Counter()
        match_count = 0
        for listing in listings:
            m = regex.search(listing['title'])
            if m:
                match_count += 1
                val = m.group(1).strip() if m.lastindex else m.group(0).strip()
                value_counts[val] += 1

        coverage = match_count / total
        unique_values = len(value_counts)

        # 3. Coverage gates
        if coverage < min_coverage_pct:
            print(f"    REJECT regex '{name}': coverage {coverage:.1%} < {min_coverage_pct:.0%} minimum")
            continue
        if coverage > max_coverage_pct:
            print(f"    REJECT regex '{name}': coverage {coverage:.1%} > {max_coverage_pct:.0%} maximum (too noisy)")
            continue

        # 4. Unique value gates
        if unique_values < min_unique:
            print(f"    REJECT regex '{name}': only {unique_values} unique values (need {min_unique}+)")
            continue

        fragmentation = unique_values / match_count
        if fragmentation > max_unique_pct:
            print(f"    REJECT regex '{name}': {unique_values} unique values from {match_count} matches ({fragmentation:.0%} fragmentation)")
            continue

        # 5. Check overlap with existing axes
        regex_match_indices = set()
        for i, listing in enumerate(listings):
            if regex.search(listing['title']):
                regex_match_indices.add(i)

        # 6. Build axis values — each value needs enough listings to be priceable
        # Filter out obvious years and common noise numbers
        noise_numbers = {str(y) for y in range(1990, 2035)}
        min_value_count = max(3, int(total * 0.001))
        axis_values = []
        for val, count in value_counts.most_common():
            if val in noise_numbers:
                continue
            if count >= min_value_count:
                axis_values.append({
                    'label': val,
                    'ngrams': [val],
                    '_regex_pattern': pattern_str,
                    '_count': count,
                })

        if len(axis_values) < min_unique:
            print(f"    REJECT regex '{name}': only {len(axis_values)} values above minimum count threshold")
            continue

        # Show top values
        top_vals = ', '.join(f"{v['label']} ({v['_count']})" for v in axis_values[:10])
        trail = f"... +{len(axis_values)-10} more" if len(axis_values) > 10 else ""
        print(f"    INJECT regex '{name}': {len(axis_values)} values, {coverage:.0%} coverage — {top_vals}{trail}")
        print(f"      Pattern: {pattern_str}")
        print(f"      Reason: {desc}")

        # Clean up internal fields before injecting
        clean_values = [{'label': v['label'], 'ngrams': [v['label']]} for v in axis_values]
        injected.append({
            'name': name,
            'values': clean_values,
            '_is_regex_axis': True,
        })

    return injected


def dedup_axis_values(axes, match_sets):
    """Remove near-duplicate values within each axis using listing overlap.

    If two values match >85% the same listings, they're effectively the same
    thing (e.g., 'ascended hero' / 'ascended heroes'). Keep the one with
    more matches (more common form).
    """
    cleaned = []
    for axis in axes:
        values = axis['values']
        to_remove = set()

        for i in range(len(values)):
            if values[i]['label'] in to_remove:
                continue
            a = values[i]['label']
            a_set = match_sets.get(a, set())
            if not a_set:
                continue

            for j in range(i + 1, len(values)):
                if values[j]['label'] in to_remove:
                    continue
                b = values[j]['label']
                b_set = match_sets.get(b, set())
                if not b_set:
                    continue

                # High mutual overlap = same thing
                overlap = len(a_set & b_set) / min(len(a_set), len(b_set))
                if overlap < 0.85:
                    continue

                # Keep the one with more matches (more common form)
                if len(a_set) >= len(b_set):
                    to_remove.add(b)
                else:
                    to_remove.add(a)
                    break

        kept = [v for v in values if v['label'] not in to_remove]
        if to_remove:
            print(f"    Deduped from {axis['name']}: {sorted(to_remove)}")
        if len(kept) >= 2:
            cleaned.append({'name': axis['name'], 'values': kept})

    return cleaned


def prune_overlapping_values_v5(axes, significant, listings):
    """Prune overlapping values with adaptive threshold.

    Small axes (2-4 values): strict 20% threshold
    Large axes (5+ values): relaxed 35% threshold (some overlap expected)
    """
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
            value_data.append({
                "label": value["label"],
                "ngrams": value["ngrams"],
                "patterns": patterns,
                "match_set": match_set,
            })

        value_data = [v for v in value_data if len(v["match_set"]) >= 10]

        # Adaptive threshold based on axis size
        threshold = 0.35 if len(value_data) >= 5 else 0.20

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
                    intersection = len(a & b)
                    ov = intersection / min(len(a), len(b))
                    if ov > threshold:
                        overlap_counts[i] += 1
                        overlap_counts[j] += 1
                        overlap_pairs.append((i, j, ov))

            if not overlap_pairs:
                break

            worst_idx = max(
                overlap_counts.keys(),
                key=lambda i: (overlap_counts[i], -len(remaining[i]["match_set"]))
            )
            if overlap_counts[worst_idx] == 0:
                break
            removed = remaining.pop(worst_idx)
            print(f"    Pruned '{removed['label']}' from {axis['name']} (overlap {threshold:.0%} threshold)")

        if len(remaining) >= 2:
            pruned_axes.append({
                "name": axis["name"],
                "values": [{"label": v["label"], "ngrams": v["ngrams"]} for v in remaining],
            })

    return pruned_axes


def enforce_me_per_value(axes, significant, listings):
    """Iteratively remove values that violate ME within their axis.

    Uses pattern-based match sets (same as pruning) to avoid false positives
    from raw n-gram matching. If a value overlaps (>5%) with more than 40%
    of other values in the same axis, it's too generic — demote it.
    """
    demoted = []
    cleaned = []

    for axis in axes:
        # Compute pattern-based match sets for each value
        value_data = []
        for v in axis['values']:
            patterns = []
            for ng in v['ngrams']:
                if ng in significant:
                    patterns.extend([f.lower() for f in significant[ng]['forms']])
                else:
                    patterns.append(ng.lower())
            patterns = [p for p in patterns if len(p) >= 2 and not (p.isdigit() and len(p) < 3)]
            ms = compute_value_match_set(patterns, listings)
            value_data.append({'label': v['label'], 'ngrams': v['ngrams'], 'match_set': ms})

        changed = True
        while changed and len(value_data) >= 2:
            changed = False
            violations = {v['label']: 0 for v in value_data}
            total_pairs = 0

            for i in range(len(value_data)):
                a_set = value_data[i]['match_set']
                if not a_set:
                    continue
                for j in range(i + 1, len(value_data)):
                    b_set = value_data[j]['match_set']
                    if not b_set:
                        continue
                    total_pairs += 1
                    overlap = len(a_set & b_set) / min(len(a_set), len(b_set))
                    if overlap > ME_THRESHOLD:
                        violations[value_data[i]['label']] += 1
                        violations[value_data[j]['label']] += 1

            if not total_pairs:
                break

            worst = max(value_data, key=lambda v: violations[v['label']])
            worst_ratio = violations[worst['label']] / (len(value_data) - 1)

            # Demote if value overlaps with 2+ other values, but only for
            # axes with 5+ values. Small axes (2-4 values) are exempt because
            # 2 violations out of 3 peers can be legitimate (e.g., "se" and "gs"
            # in a 4-value model axis). Large axes with repeated violations
            # indicate generic/parent terms (white across colors, scarlet across sets).
            worst_count = violations[worst['label']]
            if worst_count >= 2 and len(value_data) >= 5:
                print(f"    Demoted '{worst['label']}' from {axis['name']} (overlaps {worst_count}/{len(value_data)-1} values)")
                demoted.append(worst['label'])
                value_data = [v for v in value_data if v['label'] != worst['label']]
                changed = True

        if len(value_data) >= 2:
            cleaned.append({
                'name': axis['name'],
                'values': [{'label': v['label'], 'ngrams': v['ngrams']} for v in value_data],
            })

    return cleaned, demoted


def run_v5(job_id, search_term):
    """Full V5 pipeline."""
    listings = load_listings(job_id)
    total = len(listings)

    # Stage 1: Deterministic n-gram pipeline
    ngrams = extract_ngrams(listings)
    deduped = dedup_ngrams(ngrams, 0.95)
    match_sets = compute_match_sets(listings, deduped)
    significant = filter_significant_ngrams(deduped, match_sets, total)
    canonicals = list(significant.keys())
    me_pairs = compute_me_pairs(canonicals, match_sets)
    me_significant = filter_to_me_participants(significant, me_pairs)
    me_significant = dedup_substrings(me_significant, match_sets)
    candidates = list(me_significant.keys())

    print(f"  {total} listings, {len(significant)} significant, {len(candidates)} ME candidates")

    # Stage 2: Community detection (deterministic)
    raw_communities, modifiers = find_raw_communities(candidates, me_pairs, me_significant, match_sets, total)
    print(f"  {len(raw_communities)} communities, {len(modifiers)} unassigned modifiers")

    for i, cl in enumerate(raw_communities):
        members_str = ', '.join(cl['members'])
        print(f"    Community {i+1} [ME={cl['me_density']:.0%}, sim={cl['sim']:.2f}]: {members_str}")

    # Stage 3: LLM cleans up (one call)
    axes, mod_list, contaminants = llm_clean_communities(raw_communities, modifiers, match_sets, total, search_term)

    # Stage 3b: LLM regex injection (structured patterns n-grams missed)
    regex_suggestions = llm_suggest_regex_axes(axes, listings, search_term, total)
    if regex_suggestions:
        print(f"  LLM suggested {len(regex_suggestions)} regex pattern(s):")
        regex_axes = validate_and_inject_regex_axes(regex_suggestions, axes, listings, total)
        axes.extend(regex_axes)
    else:
        print(f"  LLM suggested no regex patterns (expected for most categories)")

    # Stage 4: Post-processing (skip regex axes — they're pre-validated identifiers)
    ngram_axes = [ax for ax in axes if not ax.get('_is_regex_axis')]
    regex_axes = [ax for ax in axes if ax.get('_is_regex_axis')]

    ngram_axes = dedup_axis_values(ngram_axes, match_sets)
    ngram_axes = prune_overlapping_values_v5(ngram_axes, significant, listings)
    ngram_axes, demoted = enforce_me_per_value(ngram_axes, significant, listings)
    axes = ngram_axes + regex_axes
    metrics = assign_and_measure(listings, axes, significant)

    return {
        'axes': axes,
        'modifiers': mod_list,
        'contaminants': contaminants,
        'coverage': metrics['coverage_pct'],
        'conflicts': metrics['conflict_pct'],
        'total': total,
        'match_sets': match_sets,
    }


if __name__ == '__main__':
    JOBS = [
        (1, 'PlayStation 5 Console'),
        (1042, 'Nike Air Jordan 1'),
        (1020, 'iPhone 15 Pro Max'),
        (1029, 'Pokemon Booster Box Sealed'),
        (1121, 'Brompton Folding Bike'),
    ]

    for job_id, search_term in JOBS:
        print(f"\n{'='*70}")
        print(f"V5: {search_term} (job {job_id})")
        print(f"{'='*70}")

        result = run_v5(job_id, search_term)

        if result['contaminants']:
            print(f"\n  CONTAMINANTS: {result['contaminants']}")

        ms = result['match_sets']
        total = result['total']

        print(f"\n  AXES:")
        for ax in result['axes']:
            vals = ', '.join(f"{v['label']} ({100*len(ms.get(v['label'], set()))/total:.0f}%)" for v in ax['values'])
            print(f"    {ax['name']}: {vals}")

        if result['modifiers']:
            valid_mods = [m for m in result['modifiers'] if m in ms]
            if valid_mods:
                mod_str = ', '.join(f"{m} ({100*len(ms[m])/total:.0f}%)" for m in valid_mods)
                print(f"\n  MODIFIERS: {mod_str}")

        print(f"\n  Coverage: {result['coverage']}%  Conflicts: {result['conflicts']}%")
