"""
Slot-based recovery: promote sub-threshold n-grams to existing axes
using positional context (left/right neighbor words in titles).

V2 approach — targets the 1-3% significance range:
- After V5 pipeline, identify n-grams in the 1-3% range (below significance threshold)
- For each, compute positional context (left/right neighbor words)
- Compare to the slot fingerprint of each existing axis
- If slot match is strong AND n-gram is ME with axis values, promote it
"""

import sys, re
from collections import Counter, defaultdict

sys.argv = ['slot_recovery.py']
from taxonomy_v5 import run_v5
from taxonomy_v3 import (load_listings, extract_ngrams, dedup_ngrams,
                          compute_match_sets, filter_significant_ngrams,
                          compute_value_match_set, pattern_matches,
                          assign_and_measure, ME_THRESHOLD)


STOP_WORDS = {
    "the", "a", "an", "and", "or", "of", "for", "in", "on", "at", "to",
    "is", "it", "by", "as", "be", "no", "not", "so", "up", "if", "my",
    "new", "free", "with", "this", "that", "from", "was", "are", "has",
}

# Words that are never axis values — generic condition/seller terms
NEVER_AXIS_WORDS = {
    "used", "worn", "excellent", "good", "condition", "brand", "ds",
    "vnds", "bnib", "boxed", "box", "authentic", "genuine", "original",
    "rare", "limited", "edition", "fast", "shipping", "dispatch",
    "free", "post", "delivery", "royal", "mail", "uk", "us",
}


def compute_slot_contexts(listings, ngrams_to_check):
    """For each n-gram, collect left/right word context across all titles.

    Returns dict: ngram -> {'left': Counter, 'right': Counter}
    """
    contexts = {ng: {'left': Counter(), 'right': Counter()} for ng in ngrams_to_check}

    for listing in listings:
        title_lower = listing['title'].lower()
        words = re.findall(r'\b\w+\b', title_lower)
        words = [w for w in words if w not in STOP_WORDS and len(w) > 1]

        for ng in ngrams_to_check:
            ng_words = ng.split()
            ng_len = len(ng_words)

            # Find all positions where this n-gram occurs in the word list
            for i in range(len(words) - ng_len + 1):
                if words[i:i + ng_len] == ng_words:
                    # Left context: word before the n-gram
                    if i > 0:
                        contexts[ng]['left'][words[i - 1]] += 1
                    else:
                        contexts[ng]['left']['__START__'] += 1

                    # Right context: word after the n-gram
                    right_idx = i + ng_len
                    if right_idx < len(words):
                        contexts[ng]['right'][words[right_idx]] += 1
                    else:
                        contexts[ng]['right']['__END__'] += 1

    return contexts


def slot_similarity(ctx_a, ctx_b):
    """Compute similarity between two slot contexts using weighted Jaccard.

    Combines left and right context similarity.
    """
    def weighted_jaccard(counter_a, counter_b):
        if not counter_a or not counter_b:
            return 0.0
        all_keys = set(counter_a.keys()) | set(counter_b.keys())
        intersection = sum(min(counter_a.get(k, 0), counter_b.get(k, 0)) for k in all_keys)
        union = sum(max(counter_a.get(k, 0), counter_b.get(k, 0)) for k in all_keys)
        return intersection / union if union > 0 else 0.0

    left_sim = weighted_jaccard(ctx_a['left'], ctx_b['left'])
    right_sim = weighted_jaccard(ctx_a['right'], ctx_b['right'])

    # Average of left and right similarity
    return (left_sim + right_sim) / 2


def compute_axis_slot_fingerprint(axis, contexts):
    """Compute the average slot fingerprint for an axis from its assigned values."""
    # Merge all left/right counters from axis values
    merged_left = Counter()
    merged_right = Counter()
    count = 0

    for value in axis['values']:
        for ng in value['ngrams']:
            if ng in contexts:
                merged_left.update(contexts[ng]['left'])
                merged_right.update(contexts[ng]['right'])
                count += 1

    if count == 0:
        return None

    return {'left': merged_left, 'right': merged_right}


def check_me_with_axis(ng, axis, significant, match_sets, listings):
    """Check if an n-gram is mutually exclusive with values in an axis.

    Returns (is_me, violation_ratio) where violation_ratio is the fraction
    of axis values that overlap with the n-gram.
    """
    # Compute match set for the candidate n-gram
    if ng in significant:
        patterns = [f.lower() for f in significant[ng]['forms']]
    else:
        patterns = [ng.lower()]
    patterns = [p for p in patterns if len(p) >= 2 and not (p.isdigit() and len(p) < 3)]
    ng_match_set = compute_value_match_set(patterns, listings)

    if not ng_match_set:
        return False, 1.0

    violations = 0
    for value in axis['values']:
        v_patterns = []
        for v_ng in value['ngrams']:
            if v_ng in significant:
                v_patterns.extend([f.lower() for f in significant[v_ng]['forms']])
            else:
                v_patterns.append(v_ng.lower())
        v_patterns = [p for p in v_patterns if len(p) >= 2 and not (p.isdigit() and len(p) < 3)]
        v_match_set = compute_value_match_set(v_patterns, listings)

        if not v_match_set:
            continue

        overlap = len(ng_match_set & v_match_set) / min(len(ng_match_set), len(v_match_set))
        if overlap > ME_THRESHOLD:
            violations += 1

    n_values = len(axis['values'])
    violation_ratio = violations / n_values if n_values > 0 else 1.0

    # ME if overlaps with at most 1 value (some overlap OK for adjacent variants)
    return violations <= 1, violation_ratio


def find_sub_threshold_candidates(deduped, match_sets, total, assigned_words):
    """Find n-grams in the 1-3% range that could be axis values.

    Filters:
    - Only unigrams (single words) — compound n-grams are usually cross-axis combos
    - Not in NEVER_AXIS_WORDS
    - Not a word already used in any axis
    - Between 1% and 3% of listings
    """
    min_matches = int(total * 0.01)
    max_matches = int(total * 0.03)
    candidates = []

    for ng, info in deduped.items():
        count = len(match_sets.get(ng, set()))
        if count < min_matches or count >= max_matches:
            continue
        # Only unigrams
        if ' ' in ng:
            continue
        # Not a generic term
        if ng in NEVER_AXIS_WORDS:
            continue
        # Not already assigned
        if ng in assigned_words:
            continue
        # Not too short (single char) or pure number
        if len(ng) < 2:
            continue
        if ng.isdigit():
            continue
        candidates.append(ng)

    return candidates


def recover_sub_threshold(result, deduped, listings, match_sets, min_slot_sim=0.20):
    """Promote sub-threshold n-grams (1-3%) to existing axes using slot context.

    Uses individual value comparison instead of merged axis fingerprint —
    if a candidate's slot context matches ANY existing axis value above
    the threshold, it's a candidate for that axis.

    Returns augmented axes with recovered values.
    """
    axes = result['axes']
    total = result['total']

    # Collect all assigned words
    assigned_words = set()
    for axis in axes:
        for value in axis['values']:
            for ng in value['ngrams']:
                for w in ng.split():
                    assigned_words.add(w)
    for m in result.get('modifiers', []):
        for w in m.split():
            assigned_words.add(w)

    # Find sub-threshold candidates
    candidates = find_sub_threshold_candidates(deduped, match_sets, total, assigned_words)

    if not candidates:
        print("  No sub-threshold candidates to evaluate")
        return axes, []

    print(f"  {len(candidates)} sub-threshold candidates (1-3% unigrams)")

    # Compute slot contexts for candidates + assigned axis values
    axis_ngrams = []
    for axis in axes:
        for value in axis['values']:
            axis_ngrams.extend(value['ngrams'])

    all_ngrams = list(set(axis_ngrams) | set(candidates))
    contexts = compute_slot_contexts(listings, all_ngrams)

    # Build a "significant" dict for candidates (for ME check)
    candidate_significant = {}
    for ng in candidates:
        if ng in deduped:
            candidate_significant[ng] = deduped[ng]
        else:
            candidate_significant[ng] = {'forms': [ng]}

    # Try to assign each candidate using individual value comparison
    recovered = []

    for ng in sorted(candidates, key=lambda k: -len(match_sets.get(k, set()))):
        if ng not in contexts:
            continue
        ng_ctx = contexts[ng]
        if not ng_ctx['left'] and not ng_ctx['right']:
            continue

        # Compare to each individual axis value, track best
        best_axis_idx = -1
        best_sim = 0.0
        best_match_val = None

        for ax_idx, axis in enumerate(axes):
            for value in axis['values']:
                for v_ng in value['ngrams']:
                    v_ctx = contexts.get(v_ng, {'left': Counter(), 'right': Counter()})
                    if not v_ctx['left'] and not v_ctx['right']:
                        continue
                    sim = slot_similarity(ng_ctx, v_ctx)
                    if sim > best_sim:
                        best_sim = sim
                        best_axis_idx = ax_idx
                        best_match_val = v_ng

        if best_axis_idx < 0 or best_sim < min_slot_sim:
            continue

        # ME check — must not overlap with existing axis values
        is_me, violation_ratio = check_me_with_axis(
            ng, axes[best_axis_idx], candidate_significant, match_sets, listings)

        match_count = len(match_sets.get(ng, set()))
        match_pct = 100 * match_count / total

        if is_me:
            recovered.append((ng, best_axis_idx, best_sim, match_count))
            print(f"    PROMOTE '{ng}' -> {axes[best_axis_idx]['name']} "
                  f"(slot={best_sim:.3f} via '{best_match_val}', {match_pct:.1f}%)")
        elif best_sim >= 0.25:
            print(f"    REJECT  '{ng}' -> {axes[best_axis_idx]['name']} "
                  f"(slot={best_sim:.3f} via '{best_match_val}', {match_pct:.1f}%, ME viol={violation_ratio:.2f})")

    # Apply recoveries
    import copy
    augmented = copy.deepcopy(axes)
    for ng, ax_idx, sim, count in recovered:
        augmented[ax_idx]['values'].append({'label': ng, 'ngrams': [ng]})

    return augmented, recovered


def run_experiment(job_id, search_term):
    """Run V5, then try sub-threshold slot recovery, compare before/after."""
    print(f"\n{'='*70}")
    print(f"SLOT RECOVERY: {search_term} (job {job_id})")
    print(f"{'='*70}")

    result = run_v5(job_id, search_term)

    ms = result['match_sets']
    total = result['total']

    print(f"\n  BEFORE recovery:")
    print(f"    Coverage: {result['coverage']}%  Conflicts: {result['conflicts']}%")
    for ax in result['axes']:
        vals = ', '.join(f"{v['label']} ({100*len(ms.get(v['label'], set()))/total:.0f}%)" for v in ax['values'])
        print(f"    {ax['name']}: {vals}")

    # Load raw deduped n-grams (includes sub-threshold)
    listings = load_listings(1060 if job_id == 1060 else job_id)  # avoid re-loading
    listings = load_listings(job_id)
    ngrams = extract_ngrams(listings)
    deduped = dedup_ngrams(ngrams, 0.95)
    match_sets = compute_match_sets(listings, deduped)
    significant = filter_significant_ngrams(deduped, match_sets, total)

    # Try sub-threshold recovery
    print(f"\n  Sub-threshold slot recovery:")
    augmented, recovered = recover_sub_threshold(result, deduped, listings, match_sets)

    if not recovered:
        print(f"\n  No recoveries made")
        return result['coverage'], result['conflicts'], 0

    # Re-run post-processing on augmented axes
    from taxonomy_v5 import prune_overlapping_values_v5, enforce_me_per_value
    augmented = prune_overlapping_values_v5(augmented, significant, listings)
    augmented, demoted = enforce_me_per_value(augmented, significant, listings)
    metrics = assign_and_measure(listings, augmented, significant)

    print(f"\n  AFTER recovery ({len(recovered)} promoted, {len(demoted)} demoted post-process):")
    print(f"    Coverage: {metrics['coverage_pct']}%  Conflicts: {metrics['conflict_pct']}%")
    for ax in augmented:
        vals = ', '.join(f"{v['label']} ({100*len(ms.get(v['label'], set()))/total:.0f}%)" for v in ax['values'])
        print(f"    {ax['name']}: {vals}")

    delta_cov = metrics['coverage_pct'] - result['coverage']
    delta_conf = metrics['conflict_pct'] - result['conflicts']
    print(f"\n  DELTA: Coverage {delta_cov:+.1f}pp  Conflicts {delta_conf:+.1f}pp")

    return metrics['coverage_pct'], metrics['conflict_pct'], len(recovered)


if __name__ == '__main__':
    JOBS = [
        (1, 'PlayStation 5 Console'),
        (1042, 'Nike Air Jordan 1'),
        (1020, 'iPhone 15 Pro Max'),
        (1029, 'Pokemon Booster Box Sealed'),
        (1060, 'Adidas Yeezy Boost 350'),
        (1045, 'LEGO Star Wars Set'),
        (1136, 'Nike Dunk Low'),
    ]

    results = []
    for job_id, search_term in JOBS:
        try:
            cov, conf, n_recovered = run_experiment(job_id, search_term)
            results.append({
                'name': search_term,
                'coverage': cov,
                'conflicts': conf,
                'recovered': n_recovered,
            })
        except Exception as e:
            print(f"  ERROR: {e}")
            import traceback
            traceback.print_exc()

    print(f"\n\n{'='*70}")
    print(f"SUMMARY")
    print(f"{'='*70}")
    print(f"{'Category':<30} {'Cover':>6} {'Confl':>6} {'Recov':>6}")
    print(f"{'-'*30} {'-'*6} {'-'*6} {'-'*6}")
    for r in results:
        print(f"{r['name']:<30} {r['coverage']:>5.1f}% {r['conflicts']:>5.1f}% {r['recovered']:>6}")
