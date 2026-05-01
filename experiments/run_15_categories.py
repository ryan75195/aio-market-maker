import sys
sys.argv = ['run_15.py']
from taxonomy_v5 import run_v5

JOBS = [
    (1136, 'Nike Dunk Low'),
    (1032, 'Funko Pop Chase'),
    (1045, 'LEGO Star Wars Set'),
    (1033, 'Vintage Star Wars Figure'),
    (1086, 'Vintage Levis 501 Jeans'),
    (1065, 'Birkenstock Arizona Sandals'),
    (1064, 'Dr Martens 1460 Boots'),
    (1054, 'Ray-Ban Wayfarer Sunglasses'),
    (1031, 'First Edition Harry Potter'),
    (1060, 'Adidas Yeezy Boost 350'),
    (1046, 'Omega Seamaster Watch'),
    (1023, 'Nintendo Switch OLED'),
    (1100, 'MTG Magic The Gathering Booster Box'),
    (1034, 'KitchenAid Stand Mixer'),
    (1030, 'Rolex Submariner'),
]

results = []
for job_id, search_term in JOBS:
    print(f"\n{'='*70}")
    print(f"V5: {search_term} (job {job_id})")
    print(f"{'='*70}")

    try:
        result = run_v5(job_id, search_term)
        ms = result['match_sets']
        total = result['total']

        n_axes = len(result['axes'])
        n_values = sum(len(ax['values']) for ax in result['axes'])

        print(f"\n  AXES:")
        for ax in result['axes']:
            vals = ', '.join(f"{v['label']} ({100*len(ms.get(v['label'], set()))/total:.0f}%)" for v in ax['values'])
            print(f"    {ax['name']}: {vals}")

        if result['modifiers']:
            valid_mods = [m for m in result['modifiers'] if m in ms][:8]
            if valid_mods:
                mod_str = ', '.join(f"{m} ({100*len(ms[m])/total:.0f}%)" for m in valid_mods)
                print(f"  MODIFIERS: {mod_str}")

        print(f"\n  Coverage: {result['coverage']}%  Conflicts: {result['conflicts']}%  Axes: {n_axes}  Values: {n_values}")

        results.append({
            'name': search_term,
            'total': total,
            'coverage': result['coverage'],
            'conflicts': result['conflicts'],
            'n_axes': n_axes,
            'n_values': n_values,
        })
    except Exception as e:
        print(f"  ERROR: {e}")
        results.append({
            'name': search_term,
            'total': 0,
            'coverage': 0,
            'conflicts': 0,
            'n_axes': 0,
            'n_values': 0,
        })

print(f"\n\n{'='*70}")
print(f"SUMMARY")
print(f"{'='*70}")
print(f"{'Category':<35} {'Total':>6} {'Cover':>6} {'Confl':>6} {'Axes':>5} {'Vals':>5}")
print(f"{'-'*35} {'-'*6} {'-'*6} {'-'*6} {'-'*5} {'-'*5}")
for r in results:
    print(f"{r['name']:<35} {r['total']:>6} {r['coverage']:>5.1f}% {r['conflicts']:>5.1f}% {r['n_axes']:>5} {r['n_values']:>5}")

valid = [r for r in results if r['coverage'] > 0]
if valid:
    avg_cov = sum(r['coverage'] for r in valid) / len(valid)
    avg_con = sum(r['conflicts'] for r in valid) / len(valid)
    print(f"\n  Average coverage: {avg_cov:.1f}%")
    print(f"  Average conflicts: {avg_con:.1f}%")
    print(f"  Categories with >80% coverage: {sum(1 for r in valid if r['coverage'] >= 80)}/{len(valid)}")
    print(f"  Categories with <5% conflicts: {sum(1 for r in valid if r['conflicts'] < 5)}/{len(valid)}")
