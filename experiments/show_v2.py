import json

with open('experiments/taxonomy-ps5-v2.json', encoding='utf-8') as f:
    data = json.load(f)

print(f"Total: {data['totalListings']}, Labeled: {data['labeledListings']} ({100*data['labeledListings']/data['totalListings']:.1f}%), Unlabeled: {data['unlabeledListings']}")
print(f"Categories: {len(data['categories'])}")

# Show categories by size (top 20)
cats = [c for c in data['categories'] if c['label'] != 'Unlabeled']
cats.sort(key=lambda x: -x['total'])

print(f"\nTOP 20 CATEGORIES BY SIZE:")
print(f"{'Category':<55} {'Total':>5} {'Act':>4} {'Sold':>4} {'Spread':>8}")
print("-" * 80)
for cat in cats[:20]:
    print(f"{cat['label'][:55]:<55} {cat['total']:>5} {cat['active']:>4} {cat['sold']:>4} {cat['spread']:>8.0f}")

# Show unlabeled samples
unlabeled = [c for c in data['categories'] if c['label'] == 'Unlabeled']
if unlabeled:
    print(f"\nUNLABELED: {unlabeled[0]['total']} listings")
    print("Sample titles:")
    for t in unlabeled[0].get('sampleTitles', []):
        print(f"  - {t}")

# Axis coverage
print("\nAXIS COVERAGE:")
for ax in data['axes']:
    print(f"\n  {ax['name']}:")
    for val in ax['values']:
        # Count listings for this value
        count = 0
        for c in data['categories']:
            av = c.get('axisValues', {})
            if av.get(ax['name']) == val['label']:
                count += c['total']
        print(f"    {val['label']}: {count} listings (match: {val['ngrams']})")
