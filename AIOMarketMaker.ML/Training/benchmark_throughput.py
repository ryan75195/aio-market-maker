"""
Benchmark ONNX variant classifier throughput with different configurations.
Tests batch sizes, sequence lengths, and measures GPU utilization.

Usage:
    py -3.12 benchmark_throughput.py
"""

import sys
import io
import time
import json
import pyodbc
import numpy as np
import onnxruntime as ort
from transformers import AutoTokenizer

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace", line_buffering=True)
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace", line_buffering=True)

MODEL_PATH = "E:/Dev/ml-training/variant-classifier/v10/onnx/model.onnx"
CONN_STR = (
    "Driver={ODBC Driver 17 for SQL Server};"
    "Server=(localdb)\\MSSQLLocalDB;"
    "Database=AIOMarketMaker;"
    "Trusted_Connection=yes;"
)
SAMPLE_SIZE = 5000


def load_sample_pairs(n=SAMPLE_SIZE):
    """Load N relationship pairs with listing text from the database.
    Two-phase load: IDs first (fast), then text (avoids slow JOIN on nvarchar(max)).
    """
    print(f"Loading {n} pairs from database...", flush=True)
    conn = pyodbc.connect(CONN_STR)
    cursor = conn.cursor()

    # Phase 1: Get relationship IDs + listing IDs (fast, clustered index scan)
    print("  Phase 1: Loading relationship IDs...", flush=True)
    cursor.execute(f"""
        SELECT TOP {n} Id, ListingIdA, ListingIdB, SimilarityScore, IsComparable
        FROM ListingRelationships
        WHERE Id >= (SELECT MAX(Id) / 2 FROM ListingRelationships)
        ORDER BY Id
    """)
    rels = cursor.fetchall()
    print(f"  Got {len(rels)} relationships", flush=True)

    # Collect unique listing IDs
    listing_ids = set()
    for r in rels:
        listing_ids.add(r.ListingIdA)
        listing_ids.add(r.ListingIdB)
    print(f"  Need {len(listing_ids)} unique listings", flush=True)

    # Phase 2: Load listing text in chunks (avoids huge IN clause)
    print("  Phase 2: Loading listing text...", flush=True)
    listings = {}
    id_list = list(listing_ids)
    chunk_size = 500
    for i in range(0, len(id_list), chunk_size):
        chunk = id_list[i:i + chunk_size]
        placeholders = ",".join(str(x) for x in chunk)
        cursor.execute(f"SELECT Id, Title, Description FROM Listings WHERE Id IN ({placeholders})")
        for row in cursor.fetchall():
            listings[row.Id] = (row.Title or "", row.Description or "")
        if (i // chunk_size) % 5 == 0:
            print(f"    {min(i + chunk_size, len(id_list))}/{len(id_list)} listings loaded", flush=True)

    # Phase 3: Assemble pairs
    pairs = []
    for r in rels:
        a = listings.get(r.ListingIdA)
        b = listings.get(r.ListingIdB)
        if a and b:
            pairs.append({
                "id": r.Id,
                "sim_score": r.SimilarityScore,
                "is_comparable": r.IsComparable,
                "text_a": f"{a[0]} | {a[1]}",
                "text_b": f"{b[0]} | {b[1]}",
            })

    conn.close()
    print(f"  Loaded {len(pairs)} complete pairs", flush=True)
    return pairs


def tokenize_batch(tokenizer, pairs, max_length):
    """Tokenize a batch of pairs with truncation and padding."""
    texts_a = [p["text_a"] for p in pairs]
    texts_b = [p["text_b"] for p in pairs]

    encoded = tokenizer(
        texts_a, texts_b,
        return_tensors="np",
        max_length=max_length,
        padding=True,      # pad to longest in batch (dynamic)
        truncation=True,
    )

    return encoded["input_ids"].astype(np.int64), encoded["attention_mask"].astype(np.int64)


def benchmark_config(session, tokenizer, pairs, batch_size, max_length, warmup=True):
    """Benchmark a specific batch_size + max_length configuration."""

    # Warmup with 1 batch
    if warmup:
        batch = pairs[:batch_size]
        input_ids, attention_mask = tokenize_batch(tokenizer, batch, max_length)
        session.run(["logits"], {"input_ids": input_ids, "attention_mask": attention_mask})

    # Time the full run
    total_pairs = 0
    total_tokenize_ms = 0
    total_inference_ms = 0
    actual_seq_lengths = []

    start = time.perf_counter()

    for i in range(0, len(pairs), batch_size):
        batch = pairs[i:i + batch_size]

        # Tokenize
        t0 = time.perf_counter()
        input_ids, attention_mask = tokenize_batch(tokenizer, batch, max_length)
        t1 = time.perf_counter()
        total_tokenize_ms += (t1 - t0) * 1000

        actual_seq_lengths.append(input_ids.shape[1])

        # Inference
        logits = session.run(["logits"], {"input_ids": input_ids, "attention_mask": attention_mask})[0]
        t2 = time.perf_counter()
        total_inference_ms += (t2 - t1) * 1000

        total_pairs += len(batch)

    elapsed = time.perf_counter() - start

    return {
        "batch_size": batch_size,
        "max_length": max_length,
        "total_pairs": total_pairs,
        "elapsed_sec": elapsed,
        "pairs_per_sec": total_pairs / elapsed,
        "tokenize_ms": total_tokenize_ms,
        "inference_ms": total_inference_ms,
        "avg_seq_len": np.mean(actual_seq_lengths),
        "max_seq_len": max(actual_seq_lengths),
        "tokenize_pct": total_tokenize_ms / (total_tokenize_ms + total_inference_ms) * 100,
    }


def estimate_full_run(pairs_per_sec, total_pairs=4_186_429):
    """Estimate time for full backfill."""
    seconds = total_pairs / pairs_per_sec
    hours = seconds / 3600
    return hours


def main():
    print("=" * 70)
    print("ONNX Variant Classifier Throughput Benchmark")
    print("=" * 70)

    # Load data
    pairs = load_sample_pairs()

    # Check token length distribution
    print("\nLoading tokenizer...")
    tokenizer = AutoTokenizer.from_pretrained("FacebookAI/roberta-large")

    print("Analyzing token lengths (sample of 500)...")
    sample = pairs[:500]
    token_lens = []
    for p in sample:
        enc = tokenizer(p["text_a"], p["text_b"], truncation=False)
        token_lens.append(len(enc["input_ids"]))

    token_lens = np.array(token_lens)
    print(f"  Token lengths: min={token_lens.min()}, median={np.median(token_lens):.0f}, "
          f"mean={token_lens.mean():.0f}, p90={np.percentile(token_lens, 90):.0f}, "
          f"p95={np.percentile(token_lens, 95):.0f}, max={token_lens.max()}")
    for ml in [128, 192, 256, 384, 512]:
        truncated = (token_lens > ml).sum()
        print(f"  At max_length={ml}: {truncated}/{len(token_lens)} ({100*truncated/len(token_lens):.1f}%) truncated")

    # Load ONNX
    print(f"\nLoading ONNX model from {MODEL_PATH}...")
    session_options = ort.SessionOptions()
    session_options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL

    # Try fp16 mode
    session = ort.InferenceSession(
        MODEL_PATH,
        session_options,
        providers=["CUDAExecutionProvider", "CPUExecutionProvider"]
    )
    provider = session.get_providers()[0]
    print(f"Using provider: {provider}")

    # Benchmark configurations
    configs = [
        # (batch_size, max_length)
        # Current production settings
        (128, 512),
        (256, 512),

        # Reduce sequence length (biggest lever for attention-based models)
        (128, 256),
        (256, 256),
        (512, 256),

        # Aggressive: shorter sequences + larger batches
        (128, 192),
        (256, 192),
        (512, 192),

        # Very aggressive: title-only territory
        (256, 128),
        (512, 128),
        (1024, 128),

        # Large batches with medium length
        (512, 384),
        (512, 512),
    ]

    print(f"\n{'=' * 70}")
    print(f"{'Config':<20} {'Pairs/s':>10} {'Elapsed':>10} {'AvgSeq':>8} {'Tok%':>6} {'Full ETA':>10}")
    print("-" * 70)

    results = []
    for batch_size, max_length in configs:
        label = f"bs={batch_size} ml={max_length}"
        try:
            r = benchmark_config(session, tokenizer, pairs, batch_size, max_length)
            eta_h = estimate_full_run(r["pairs_per_sec"])
            print(f"{label:<20} {r['pairs_per_sec']:>10.1f} {r['elapsed_sec']:>9.1f}s {r['avg_seq_len']:>7.0f} "
                  f"{r['tokenize_pct']:>5.1f}% {eta_h:>9.1f}h")
            results.append(r)
        except Exception as e:
            print(f"{label:<20} {'FAILED':>10}  {str(e)[:50]}")

    # Summary
    print(f"\n{'=' * 70}")
    print("TOP 3 CONFIGURATIONS")
    print("=" * 70)
    results.sort(key=lambda x: x["pairs_per_sec"], reverse=True)
    for i, r in enumerate(results[:3]):
        eta = estimate_full_run(r["pairs_per_sec"])
        print(f"\n#{i+1}: batch_size={r['batch_size']}, max_length={r['max_length']}")
        print(f"    {r['pairs_per_sec']:.1f} pairs/sec")
        print(f"    Avg sequence length: {r['avg_seq_len']:.0f}")
        print(f"    Tokenization: {r['tokenize_pct']:.1f}% of time")
        print(f"    Full backfill ETA: {eta:.1f}h ({eta*60:.0f}m)")

    # Compare vs baseline
    baseline = next((r for r in results if r["batch_size"] == 128 and r["max_length"] == 512), None)
    best = results[0]
    if baseline:
        speedup = best["pairs_per_sec"] / baseline["pairs_per_sec"]
        print(f"\nBest vs baseline (bs=128 ml=512): {speedup:.1f}x speedup")
        print(f"  Baseline: {baseline['pairs_per_sec']:.1f} pairs/sec → {estimate_full_run(baseline['pairs_per_sec']):.1f}h")
        print(f"  Best:     {best['pairs_per_sec']:.1f} pairs/sec → {estimate_full_run(best['pairs_per_sec']):.1f}h")


if __name__ == "__main__":
    main()
