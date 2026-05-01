"""
Test: Do n-gram embeddings naturally cluster into taxonomy axes?

1. Extract significant n-grams from PS5 titles
2. Embed them via OpenAI text-embedding-3-large
3. Compute cosine similarity matrix
4. Cluster with Ward linkage
5. Show clusters — do axes emerge?
"""

import pyodbc
import json
import re
import os
import numpy as np
from collections import Counter
from openai import OpenAI
from scipy.cluster.hierarchy import linkage, fcluster
from scipy.spatial.distance import pdist

CONN_STR = (
    r"Driver={ODBC Driver 17 for SQL Server};"
    r"Server=(localdb)\MSSQLLocalDB;"
    r"Database=AIOMarketMaker;"
    r"Trusted_Connection=yes;"
)
JOB_ID = 1
MIN_NGRAM_FREQ = 20  # Higher threshold for cleaner set

settings_path = os.path.join(os.path.dirname(__file__), "..", "AIOMarketMaker.Console", "local.settings.json")
with open(settings_path) as f:
    settings = json.load(f)

client = OpenAI(api_key=settings["OpenAi"]["ApiKey"])

STOP_WORDS = {
    "the", "a", "an", "and", "or", "of", "for", "in", "on", "at", "to",
    "is", "it", "by", "as", "be", "no", "not", "so", "up", "if", "my",
    "new", "free", "with", "this", "that", "from", "was", "are", "has",
}


def load_titles():
    conn = pyodbc.connect(CONN_STR)
    cursor = conn.cursor()
    cursor.execute("SELECT Title FROM Listings WHERE ScrapeJobId = ? AND Title IS NOT NULL", JOB_ID)
    titles = [row.Title for row in cursor.fetchall()]
    conn.close()
    print(f"Loaded {len(titles)} titles")
    return titles


def extract_ngrams(titles):
    bigrams = Counter()
    trigrams = Counter()
    for title in titles:
        words = re.findall(r'\b\w+\b', title.lower())
        words = [w for w in words if w not in STOP_WORDS and len(w) > 1]
        for i in range(len(words) - 1):
            bigrams[f"{words[i]} {words[i+1]}"] += 1
        for i in range(len(words) - 2):
            trigrams[f"{words[i]} {words[i+1]} {words[i+2]}"] += 1

    ngrams = {}
    for ng, freq in bigrams.most_common():
        if freq >= MIN_NGRAM_FREQ:
            ngrams[ng] = freq
    for ng, freq in trigrams.most_common():
        if freq >= MIN_NGRAM_FREQ:
            ngrams[ng] = freq

    print(f"Found {len(ngrams)} significant n-grams (freq >= {MIN_NGRAM_FREQ})")
    return ngrams


def embed_ngrams(ngrams):
    texts = list(ngrams.keys())
    print(f"Embedding {len(texts)} n-grams...")

    # Batch embed (all fit in one call)
    response = client.embeddings.create(
        model="text-embedding-3-large",
        input=texts,
        dimensions=256,  # Small dims for clustering - plenty for short text
    )

    vectors = np.array([d.embedding for d in response.data])
    print(f"  Got {vectors.shape} embedding matrix")
    return texts, vectors


def find_equivalents(texts, vectors, threshold=0.85):
    """Find semantically equivalent n-gram pairs."""
    print(f"\nSEMANTIC EQUIVALENTS (cosine sim > {threshold}):")
    # Normalize for cosine similarity
    norms = np.linalg.norm(vectors, axis=1, keepdims=True)
    normed = vectors / norms
    sim_matrix = normed @ normed.T

    pairs = []
    for i in range(len(texts)):
        for j in range(i + 1, len(texts)):
            sim = sim_matrix[i, j]
            if sim > threshold:
                pairs.append((texts[i], texts[j], sim))

    pairs.sort(key=lambda x: -x[2])
    for a, b, sim in pairs[:30]:
        print(f"  {sim:.3f}  {a}  <->  {b}")
    print(f"  Total pairs above {threshold}: {len(pairs)}")
    return sim_matrix


def cluster_ngrams(texts, vectors, freqs):
    """Cluster n-grams with Ward linkage at several thresholds."""
    # Use cosine distance for clustering
    norms = np.linalg.norm(vectors, axis=1, keepdims=True)
    normed = vectors / norms
    distances = pdist(normed, metric='cosine')

    Z = linkage(distances, method='ward')

    for threshold in [1.0, 1.5, 2.0, 2.5, 3.0]:
        labels = fcluster(Z, t=threshold, criterion='distance')
        n_clusters = len(set(labels))

        print(f"\n{'='*60}")
        print(f"WARD THRESHOLD = {threshold} -> {n_clusters} clusters")
        print(f"{'='*60}")

        # Group n-grams by cluster
        clusters = {}
        for i, label in enumerate(labels):
            if label not in clusters:
                clusters[label] = []
            clusters[label].append((texts[i], freqs[texts[i]]))

        # Sort clusters by total frequency
        for cid in sorted(clusters, key=lambda c: -sum(f for _, f in clusters[c])):
            members = sorted(clusters[cid], key=lambda x: -x[1])
            total_freq = sum(f for _, f in members)
            member_strs = [f"{ng} ({f})" for ng, f in members[:8]]
            suffix = f" +{len(members)-8} more" if len(members) > 8 else ""
            print(f"  Cluster {cid} ({len(members)} members, freq={total_freq}): "
                  f"{', '.join(member_strs)}{suffix}")


def main():
    titles = load_titles()
    ngrams = extract_ngrams(titles)
    texts, vectors = embed_ngrams(ngrams)
    sim_matrix = find_equivalents(texts, vectors, threshold=0.80)
    cluster_ngrams(texts, vectors, ngrams)


if __name__ == "__main__":
    main()
