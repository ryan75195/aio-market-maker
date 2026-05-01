"""
Diagnostic: What do the actual pairwise cosine similarities look like
between key n-grams we KNOW should be on different axes?

This tells us if there's a natural threshold that separates
"same axis values" from "different axis values".
"""

import pyodbc
import json
import re
import os
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

# Key n-grams we know the correct axis for (PS5 ground truth)
KEY_NGRAMS = {
    "Edition Type": ["digital", "disc", "disk"],
    "Model": ["slim", "pro"],
    "Storage": ["825gb", "1tb", "2tb"],
    "Color": ["white", "black", "red", "blue", "midnight"],
}

# Flatten for embedding
all_ngrams = []
ngram_to_axis = {}
for axis, ngrams in KEY_NGRAMS.items():
    for ng in ngrams:
        all_ngrams.append(ng)
        ngram_to_axis[ng] = axis

print(f"Embedding {len(all_ngrams)} key n-grams...\n")
response = client.embeddings.create(
    model="text-embedding-3-large", input=all_ngrams, dimensions=256
)
vectors = np.array([d.embedding for d in response.data])
norms = np.linalg.norm(vectors, axis=1, keepdims=True)
normed = vectors / norms
sim_matrix = normed @ normed.T

# Print full similarity matrix
print("COSINE SIMILARITY MATRIX:")
print(f"{'':>12}", end="")
for ng in all_ngrams:
    print(f"{ng:>10}", end="")
print()

for i, ng_i in enumerate(all_ngrams):
    print(f"{ng_i:>12}", end="")
    for j, ng_j in enumerate(all_ngrams):
        sim = sim_matrix[i][j]
        marker = ""
        if i != j:
            if ngram_to_axis[ng_i] == ngram_to_axis[ng_j]:
                marker = "*"  # Same axis
        print(f"{sim:>9.3f}{marker}", end="")
    print()

# Summary stats
print("\n\nSAME-AXIS pairs (should be HIGH similarity):")
same_axis_sims = []
for i in range(len(all_ngrams)):
    for j in range(i+1, len(all_ngrams)):
        if ngram_to_axis[all_ngrams[i]] == ngram_to_axis[all_ngrams[j]]:
            sim = sim_matrix[i][j]
            same_axis_sims.append(sim)
            print(f"  {all_ngrams[i]:>10} <-> {all_ngrams[j]:<10}  ({ngram_to_axis[all_ngrams[i]]}): {sim:.3f}")

print(f"\n  Range: {min(same_axis_sims):.3f} - {max(same_axis_sims):.3f}")
print(f"  Mean:  {np.mean(same_axis_sims):.3f}")

print("\nCROSS-AXIS pairs (should be LOW similarity):")
cross_axis_sims = []
for i in range(len(all_ngrams)):
    for j in range(i+1, len(all_ngrams)):
        if ngram_to_axis[all_ngrams[i]] != ngram_to_axis[all_ngrams[j]]:
            sim = sim_matrix[i][j]
            cross_axis_sims.append((all_ngrams[i], all_ngrams[j], sim))

cross_axis_sims.sort(key=lambda x: -x[2])
for a, b, sim in cross_axis_sims[:10]:
    print(f"  {a:>10} <-> {b:<10}  ({ngram_to_axis[a]} vs {ngram_to_axis[b]}): {sim:.3f}")
print(f"  ...")
for a, b, sim in cross_axis_sims[-5:]:
    print(f"  {a:>10} <-> {b:<10}  ({ngram_to_axis[a]} vs {ngram_to_axis[b]}): {sim:.3f}")

all_cross = [s for _, _, s in cross_axis_sims]
print(f"\n  Range: {min(all_cross):.3f} - {max(all_cross):.3f}")
print(f"  Mean:  {np.mean(all_cross):.3f}")

# Separability
print(f"\n\nSEPARABILITY:")
print(f"  Same-axis min:  {min(same_axis_sims):.3f}")
print(f"  Cross-axis max: {max(all_cross):.3f}")
if min(same_axis_sims) > max(all_cross):
    print(f"  CLEAN SEPARATION at threshold ~{(min(same_axis_sims) + max(all_cross))/2:.3f}")
else:
    overlap_range = f"{max(all_cross):.3f} to {min(same_axis_sims):.3f}"
    print(f"  OVERLAP! Same-axis min < Cross-axis max")
    print(f"  Cross-axis pairs above same-axis min ({min(same_axis_sims):.3f}):")
    for a, b, sim in cross_axis_sims:
        if sim >= min(same_axis_sims):
            print(f"    {a} <-> {b}: {sim:.3f}")
