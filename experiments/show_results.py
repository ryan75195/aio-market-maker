import json
import sys

with open('experiments/taxonomy-ps5.json', encoding='utf-8') as f:
    data = json.load(f)

cats = [c for c in data['categories'] if c['label'] != 'Unlabeled']
cats.sort(key=lambda x: -x['total'])

print('TOP 30 CATEGORIES BY SIZE:')
header = f"{'Category':<65} {'Total':>5}"
print(header)
print('-' * 72)
for cat in cats[:30]:
    label = cat['label'][:65]
    print(f"{label:<65} {cat['total']:>5}")

print()
print("AXIS VALUE COVERAGE:")
for ax in data['axes']:
    ax_name = ax['name']
    for val in ax['values']:
        count = 0
        for c in data['categories']:
            av = c.get('axisValues', {})
            if av.get(ax_name) == val:
                count += c['total']
        print(f"  {ax_name}: {val} -> {count} listings")

# Unlabeled sample titles (more of them)
unlabeled = [c for c in data['categories'] if c['label'] == 'Unlabeled']
if unlabeled:
    print(f"\nUNLABELED: {unlabeled[0]['total']} listings")
    print("Sample titles (first 20):")
    for t in unlabeled[0].get('sampleTitles', [])[:20]:
        print(f"  - {t}")
