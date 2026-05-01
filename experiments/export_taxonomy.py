"""Export taxonomy results + listing assignments to JSON for the viewer UI."""

import sys, json, re
sys.argv = ['export_taxonomy.py']

from taxonomy_v5 import run_v5
from taxonomy_v3 import load_listings, pattern_matches, filter_significant_ngrams, extract_ngrams, dedup_ngrams, compute_match_sets

JOBS = [
    (1, 'PlayStation 5 Console'),
    (1042, 'Nike Air Jordan 1'),
    (1020, 'iPhone 15 Pro Max'),
    (1029, 'Pokemon Booster Box Sealed'),
    (1060, 'Adidas Yeezy Boost 350'),
    (1045, 'LEGO Star Wars Set'),
    (1136, 'Nike Dunk Low'),
]


def assign_listings(listings, axes, significant):
    """Assign each listing to a cell and return detailed results."""
    # Build matchers
    axis_matchers = []
    for axis in axes:
        matchers = []
        for value in axis['values']:
            patterns = []
            for ng in value['ngrams']:
                if ng in significant:
                    patterns.extend(significant[ng]['forms'])
                else:
                    patterns.append(ng)
            clean = [p.lower() for p in patterns if len(p) >= 2 and not (p.isdigit() and len(p) < 3)]
            matchers.append({'label': value['label'], 'patterns': clean})
        axis_matchers.append({'name': axis['name'], 'values': matchers})

    results = []
    for listing in listings:
        title_lower = listing['title'].lower()
        cell = {}
        has_conflict = False

        for axis in axis_matchers:
            matched = []
            for value in axis['values']:
                if any(pattern_matches(p, title_lower) for p in value['patterns']):
                    matched.append(value['label'])

            if len(matched) == 1:
                cell[axis['name']] = matched[0]
            elif len(matched) > 1:
                # Try substring resolution
                resolved = []
                for value in axis['values']:
                    vpats = [p for p in value['patterns'] if pattern_matches(p, title_lower)]
                    if not vpats:
                        continue
                    subsumed = False
                    for other in axis['values']:
                        if other['label'] == value['label']:
                            continue
                        opats = [p for p in other['patterns'] if pattern_matches(p, title_lower)]
                        if opats and all(any(vp in op and vp != op for op in opats) for vp in vpats):
                            subsumed = True
                            break
                    if not subsumed:
                        resolved.append(value['label'])
                if len(resolved) == 1:
                    cell[axis['name']] = resolved[0]
                elif len(resolved) > 1:
                    has_conflict = True
                    cell[axis['name']] = resolved[0]  # take first for display

        results.append({
            'title': listing['title'],
            'price': listing['price'],
            'status': listing['status'],
            'cell': cell,
            'covered': len(cell) > 0,
            'conflict': has_conflict,
        })

    return results


def compute_cell_stats(assigned_listings, axes):
    """Compute pricing stats per cell."""
    from collections import defaultdict

    cells = defaultdict(list)
    for listing in assigned_listings:
        if not listing['covered']:
            continue
        key = tuple(sorted(listing['cell'].items()))
        cells[key].append(listing)

    cell_stats = []
    for key, cell_listings in cells.items():
        cell_dict = dict(key)
        prices = [l['price'] for l in cell_listings if l['price'] and l['price'] > 0]
        active = [l for l in cell_listings if l['status'] == 'Active']
        sold = [l for l in cell_listings if l['status'] in ('Sold', 'Ended')]
        active_prices = sorted([l['price'] for l in active if l['price'] and l['price'] > 0])
        sold_prices = sorted([l['price'] for l in sold if l['price'] and l['price'] > 0])

        cell_stats.append({
            'cell': cell_dict,
            'count': len(cell_listings),
            'active': len(active),
            'sold': len(sold),
            'sell_through': round(100 * len(sold) / len(cell_listings)) if cell_listings else 0,
            'median_price': round(sorted(prices)[len(prices)//2], 2) if prices else None,
            'median_active': round(active_prices[len(active_prices)//2], 2) if active_prices else None,
            'median_sold': round(sold_prices[len(sold_prices)//2], 2) if sold_prices else None,
            'min_price': round(min(prices), 2) if prices else None,
            'max_price': round(max(prices), 2) if prices else None,
        })

    cell_stats.sort(key=lambda c: -c['count'])
    return cell_stats


def export_category(job_id, search_term):
    """Run V5 and export full results."""
    print(f"\nExporting: {search_term}")

    result = run_v5(job_id, search_term)
    listings = load_listings(job_id)
    total = len(listings)

    # Rebuild significant for assignment
    ngrams = extract_ngrams(listings)
    deduped = dedup_ngrams(ngrams, 0.95)
    match_sets_all = compute_match_sets(listings, deduped)
    significant = filter_significant_ngrams(deduped, match_sets_all, total)

    # Assign listings
    assigned = assign_listings(listings, result['axes'], significant)

    # Add modifier flags to each listing
    modifiers = [m for m in result.get('modifiers', []) if m in match_sets_all]
    for listing_data in assigned:
        title_lower = listing_data['title'].lower()
        listing_data['modifiers'] = [m for m in modifiers if m.lower() in title_lower]

    cell_stats = compute_cell_stats(assigned, result['axes'])

    ms = result['match_sets']

    # Build axis data with match stats
    axes_data = []
    for axis in result['axes']:
        values = []
        for v in axis['values']:
            count = len(ms.get(v['label'], set()))
            values.append({
                'label': v['label'],
                'count': count,
                'pct': round(100 * count / total, 1),
            })
        values.sort(key=lambda x: -x['count'])
        axes_data.append({'name': axis['name'], 'values': values})

    # Build modifier stats
    modifier_data = []
    for m in modifiers:
        count = len(match_sets_all.get(m, set()))
        modifier_data.append({
            'label': m,
            'count': count,
            'pct': round(100 * count / total, 1),
        })
    modifier_data.sort(key=lambda x: -x['count'])

    return {
        'job_id': job_id,
        'search_term': search_term,
        'total': total,
        'coverage': result['coverage'],
        'conflicts': result['conflicts'],
        'axes': axes_data,
        'modifier_filters': modifier_data,
        'contaminants': result.get('contaminants', []),
        'cells': cell_stats,
        'listings': assigned,
    }


if __name__ == '__main__':
    data = {'categories': []}

    for job_id, search_term in JOBS:
        try:
            cat_data = export_category(job_id, search_term)
            data['categories'].append(cat_data)
            print(f"  Done: {cat_data['total']} listings, {len(cat_data['cells'])} cells")
        except Exception as e:
            print(f"  ERROR: {e}")
            import traceback
            traceback.print_exc()

    with open('taxonomy_data.json', 'w') as f:
        json.dump(data, f, indent=2)

    print(f"\nExported to taxonomy_data.json ({len(data['categories'])} categories)")
