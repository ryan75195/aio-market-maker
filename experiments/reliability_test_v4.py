"""
Reliability test for V4 two-stage taxonomy.

Stage 1 (axis selection) runs once per category.
Stage 2 (n-gram assignment) runs N times to measure consistency.
"""

import sys
sys.argv = ['reliability_test_v4.py']  # prevent taxonomy_v4 from parsing args

from taxonomy_v4 import *

RUNS = 5

JOBS = [
    (1, "PlayStation 5 Console"),
    (1029, "Pokemon Booster Box Sealed"),
    (1121, "Brompton Folding Bike"),
    (1042, "Nike Air Jordan 1"),
    (1045, "LEGO Star Wars Set"),
    (1020, "iPhone 15 Pro Max"),
    (1086, "Vintage Levis 501 Jeans"),
]


def measure(axes, significant, listings):
    """Prune, enforce ME, and measure."""
    axes = prune_overlapping_values(axes, significant, listings)
    axes = enforce_me(axes, significant, listings)
    metrics = assign_and_measure(listings, axes, significant)
    n_axes = len(axes)
    n_values = sum(len(ax["values"]) for ax in axes)
    axis_summary = ", ".join(f"{ax['name']}({len(ax['values'])})" for ax in axes)
    return metrics["coverage_pct"], metrics["conflict_pct"], n_axes, n_values, axis_summary


def main():
    for job_id, search_term in JOBS:
        print(f"\n{'='*70}")
        print(f"V4 RELIABILITY: {search_term} (job {job_id}) x {RUNS} runs")
        print(f"{'='*70}")

        listings = load_listings(job_id)
        total = len(listings)

        # Stage 1: Select axes (once)
        sample_titles = [l["title"] for l in listings[:30]]
        selected_axes = select_axes(search_term, sample_titles)
        ax_ids = [ax["id"] for ax in selected_axes]
        print(f"  Stage 1 axes: {ax_ids}")

        # Pre-compute deterministic steps
        ngrams = extract_ngrams(listings)
        deduped = dedup_ngrams(ngrams, 0.95)
        match_sets = compute_match_sets(listings, deduped)
        significant = filter_significant_ngrams(deduped, match_sets, total)
        print(f"  {total} listings, {len(significant)} significant n-grams")

        results = []
        for run in range(RUNS):
            for attempt in range(3):
                try:
                    axes = assign_ngrams_to_axes(selected_axes, significant, match_sets, total, search_term)
                    break
                except RuntimeError:
                    if attempt < 2:
                        print(f"    (retry {attempt+2}/3)")
                        continue
                    axes = []
                    break

            if not axes:
                results.append({"coverage": 0, "conflicts": 0, "axis_summary": "FAILED"})
                print(f"  Run {run+1}: FAILED")
                continue

            coverage, conflicts, n_axes, n_values, axis_summary = measure(axes, significant, listings)
            results.append({"coverage": coverage, "conflicts": conflicts, "axis_summary": axis_summary})
            print(f"  Run {run+1}: Cover={coverage}% Confl={conflicts}% [{axis_summary}]")

        valid = [r for r in results if r["axis_summary"] != "FAILED"]
        if len(valid) < 2:
            print(f"  Too few valid runs")
            continue

        coverages = [r["coverage"] for r in valid]
        summaries = [r["axis_summary"] for r in valid]
        print(f"\n  CONSISTENCY ({len(valid)}/{RUNS} valid):")
        print(f"    Coverage: {min(coverages)}-{max(coverages)}% (range {max(coverages)-min(coverages):.1f}pp)")

        unique = set(summaries)
        print(f"    Unique structures: {len(unique)} of {len(valid)}")
        for s in unique:
            print(f"      [{s}] x{summaries.count(s)}")


if __name__ == "__main__":
    main()
