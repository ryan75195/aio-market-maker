"""
Evaluate the v10 ONNX model on the full training dataset.

Runs all 143K pairs through the exported ONNX model and computes
accuracy, F1, precision, recall — overall and per-category.

Usage:
    py -3.12 eval-v10-onnx.py
"""

import csv
import sys
import io
import time
import numpy as np
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace", line_buffering=True)
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace", line_buffering=True)

csv.field_size_limit(10_000_000)

import onnxruntime as ort
from transformers import AutoTokenizer
from sklearn.metrics import (
    accuracy_score, f1_score, precision_score, recall_score,
    classification_report, confusion_matrix
)

MODEL_PATH = Path("E:/Dev/ml-training/variant-classifier/v10/onnx/model.onnx")
DATA_PATH = Path("data/labeled_pairs_v10.csv")
MAX_LENGTH = 512
BATCH_SIZE = 32


def main():
    print(f"Loading ONNX model from {MODEL_PATH}...")
    session = ort.InferenceSession(
        str(MODEL_PATH),
        providers=["CUDAExecutionProvider", "CPUExecutionProvider"]
    )
    provider = session.get_providers()[0]
    print(f"Using provider: {provider}")

    print("Loading tokenizer...")
    tokenizer = AutoTokenizer.from_pretrained("FacebookAI/roberta-large")

    print(f"Loading dataset from {DATA_PATH}...")
    rows = []
    with open(DATA_PATH, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        for row in reader:
            rows.append(row)

    print(f"Loaded {len(rows)} pairs")

    all_preds = []
    all_labels = []
    all_confs = []
    categories = []

    start = time.time()

    for batch_start in range(0, len(rows), BATCH_SIZE):
        batch = rows[batch_start:batch_start + BATCH_SIZE]

        texts_a = []
        texts_b = []
        for row in batch:
            a = f"{row['anchor_title']} | {row.get('anchor_desc', '')}"
            b = f"{row['neighbor_title']} | {row.get('neighbor_desc', '')}"
            texts_a.append(a)
            texts_b.append(b)
            all_labels.append(int(row["label"]))
            categories.append(row["product_name"])

        encoded = tokenizer(
            texts_a, texts_b,
            return_tensors="np",
            max_length=MAX_LENGTH,
            padding=True,  # Dynamic padding to max in batch
            truncation=True,
        )

        logits = session.run(
            ["logits"],
            {
                "input_ids": encoded["input_ids"].astype(np.int64),
                "attention_mask": encoded["attention_mask"].astype(np.int64),
            }
        )[0]

        # Softmax
        exp_logits = np.exp(logits - logits.max(axis=1, keepdims=True))
        probs = exp_logits / exp_logits.sum(axis=1, keepdims=True)

        preds = np.argmax(probs, axis=1)
        confs = probs.max(axis=1)

        all_preds.extend(preds.tolist())
        all_confs.extend(confs.tolist())

        done = min(batch_start + BATCH_SIZE, len(rows))
        if done % 1280 == 0 or done == len(rows) or done <= BATCH_SIZE:
            elapsed = time.time() - start
            rate = done / elapsed if elapsed > 0 else 0
            eta = (len(rows) - done) / rate if rate > 0 else 0
            print(f"  {done}/{len(rows)} ({100*done/len(rows):.1f}%) - {rate:.0f}/s - ETA {eta:.0f}s")

    elapsed = time.time() - start
    print(f"\nInference complete: {len(rows)} pairs in {elapsed:.1f}s ({len(rows)/elapsed:.0f}/s)")

    all_labels = np.array(all_labels)
    all_preds = np.array(all_preds)
    all_confs = np.array(all_confs)

    # Overall metrics
    print("\n" + "=" * 60)
    print("OVERALL METRICS")
    print("=" * 60)
    print(f"Accuracy:        {accuracy_score(all_labels, all_preds):.4f}")
    print(f"F1 (macro):      {f1_score(all_labels, all_preds, average='macro'):.4f}")
    print(f"F1 (class 0):    {f1_score(all_labels, all_preds, pos_label=0):.4f}")
    print(f"F1 (class 1):    {f1_score(all_labels, all_preds, pos_label=1):.4f}")
    print(f"Precision:       {precision_score(all_labels, all_preds, average='macro'):.4f}")
    print(f"Recall:          {recall_score(all_labels, all_preds, average='macro'):.4f}")
    print(f"Avg confidence:  {all_confs.mean():.4f}")

    print("\nConfusion Matrix:")
    cm = confusion_matrix(all_labels, all_preds)
    print(f"  TN={cm[0,0]}  FP={cm[0,1]}")
    print(f"  FN={cm[1,0]}  TP={cm[1,1]}")

    print("\nClassification Report:")
    print(classification_report(all_labels, all_preds, target_names=["Different", "Same"]))

    # Per-category metrics
    print("=" * 60)
    print("PER-CATEGORY METRICS")
    print("=" * 60)

    categories = np.array(categories)
    unique_cats = sorted(set(categories))

    cat_results = []
    for cat in unique_cats:
        mask = categories == cat
        cat_labels = all_labels[mask]
        cat_preds = all_preds[mask]
        n = mask.sum()
        n_pos = (cat_labels == 1).sum()
        n_neg = (cat_labels == 0).sum()
        acc = accuracy_score(cat_labels, cat_preds)
        f1 = f1_score(cat_labels, cat_preds, average="macro", zero_division=0)
        f1_same = f1_score(cat_labels, cat_preds, pos_label=1, zero_division=0)
        cat_results.append((cat, n, n_pos, n_neg, acc, f1, f1_same))

    # Sort by F1 macro
    cat_results.sort(key=lambda x: x[5])

    print(f"\n{'Category':<45} {'N':>6} {'Pos':>5} {'Neg':>5} {'Acc':>6} {'F1Mac':>6} {'F1Sam':>6}")
    print("-" * 90)
    for cat, n, n_pos, n_neg, acc, f1, f1_same in cat_results:
        print(f"{cat:<45} {n:>6} {n_pos:>5} {n_neg:>5} {acc:>6.3f} {f1:>6.3f} {f1_same:>6.3f}")

    # Confidence analysis
    print("\n" + "=" * 60)
    print("CONFIDENCE ANALYSIS")
    print("=" * 60)

    correct = all_preds == all_labels
    print(f"\nCorrect predictions:   avg conf = {all_confs[correct].mean():.4f}")
    print(f"Incorrect predictions: avg conf = {all_confs[~correct].mean():.4f}")

    for threshold in [0.50, 0.60, 0.70, 0.80, 0.90, 0.95]:
        above = all_confs >= threshold
        if above.sum() > 0:
            acc_above = accuracy_score(all_labels[above], all_preds[above])
            f1_above = f1_score(all_labels[above], all_preds[above], average="macro", zero_division=0)
            pct = 100 * above.sum() / len(all_confs)
            print(f"  conf >= {threshold:.2f}: {above.sum():>7} pairs ({pct:>5.1f}%), acc={acc_above:.4f}, f1={f1_above:.4f}")

    # Disagreements with ground truth
    print("\n" + "=" * 60)
    print("ERROR ANALYSIS")
    print("=" * 60)

    fp_mask = (all_preds == 1) & (all_labels == 0)
    fn_mask = (all_preds == 0) & (all_labels == 1)
    print(f"False positives (predicted same, labeled different): {fp_mask.sum()}")
    print(f"False negatives (predicted different, labeled same): {fn_mask.sum()}")

    # High-confidence errors
    high_conf_errors = (~correct) & (all_confs >= 0.90)
    print(f"High-confidence errors (conf >= 0.90): {high_conf_errors.sum()}")

    print("\nDone.")


if __name__ == "__main__":
    main()
