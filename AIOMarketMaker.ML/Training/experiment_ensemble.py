"""
Experiment: Can a logistic regression ensemble of cross-encoder logits + OpenAI
similarity scores improve predictions and/or calibration?

Pipeline:
1. Load the USearch vector index and compute cosine similarity for each labeled pair
2. Run all labeled pairs through the ONNX cross-encoder → collect logits
3. Fit logistic regression on [cross_encoder_logit_diff, similarity_score] → label
4. Compare accuracy, F1, and calibration vs cross-encoder alone

Usage:
    py -3.12 experiment_ensemble.py
"""

import csv
import sys
import io
import time
import json
import numpy as np
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace", line_buffering=True)
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace", line_buffering=True)

csv.field_size_limit(10_000_000)

import onnxruntime as ort
from usearch.index import Index
from transformers import AutoTokenizer
from sklearn.linear_model import LogisticRegression
from sklearn.model_selection import cross_val_predict
from sklearn.metrics import accuracy_score, f1_score, log_loss
from sklearn.preprocessing import StandardScaler

MODEL_PATH = Path("E:/Dev/ml-training/variant-classifier/v10/onnx/model.onnx")
DATA_PATH = Path("data/labeled_pairs_v10.csv")
IDMAP_PATH = Path("<REPO_ROOT>/AIOMarketMaker/AIOMarketMaker.Api/data/vectors-idmap.json")
INDEX_PATH = Path("<REPO_ROOT>/AIOMarketMaker/AIOMarketMaker.Api/data/vectors.usearch")
MAX_LENGTH = 512
BATCH_SIZE = 32


def load_vector_index():
    """Load USearch index and ID map, return a function that computes cosine similarity."""
    print("Loading vector index...")
    idmap = json.loads(IDMAP_PATH.read_text())
    index = Index(ndim=3072, metric="cos", dtype="f32")
    index.load(str(INDEX_PATH))
    print(f"  {len(index)} vectors, {len(idmap)} ID mappings")
    return index, idmap


def cosine_similarity(index, idmap, id_a: str, id_b: str) -> float | None:
    """Compute cosine similarity between two listings using the vector index."""
    if id_a not in idmap or id_b not in idmap:
        return None
    idx_a = idmap[id_a]
    idx_b = idmap[id_b]
    v_a = np.array(index[idx_a], dtype=np.float32)
    v_b = np.array(index[idx_b], dtype=np.float32)
    dot = np.dot(v_a, v_b)
    norm = np.linalg.norm(v_a) * np.linalg.norm(v_b)
    if norm == 0:
        return None
    return float(dot / norm)


def main():
    # Step 1: Load vector index and compute similarities for all pairs
    index, idmap = load_vector_index()

    print(f"\nLoading dataset from {DATA_PATH}...")
    rows = []
    with open(DATA_PATH, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        for row in reader:
            rows.append(row)
    print(f"Loaded {len(rows)} pairs")

    print("Computing cosine similarities from vector index...")
    valid_rows = []
    missing = 0
    for row in rows:
        sim = cosine_similarity(index, idmap, row["anchor_id"], row["neighbor_id"])
        if sim is not None:
            valid_rows.append((row, sim))
        else:
            missing += 1

    print(f"  Pairs with vectors: {len(valid_rows)}, missing: {missing}")

    # Free memory
    del index, idmap

    # Step 2: Run cross-encoder inference
    print(f"\nLoading ONNX model from {MODEL_PATH}...")
    session = ort.InferenceSession(
        str(MODEL_PATH),
        providers=["CUDAExecutionProvider", "CPUExecutionProvider"]
    )
    provider = session.get_providers()[0]
    print(f"Using provider: {provider}")

    print("Loading tokenizer...")
    tokenizer = AutoTokenizer.from_pretrained("FacebookAI/roberta-large")

    all_logits = []
    all_labels = []
    all_similarities = []

    start = time.time()
    for batch_start in range(0, len(valid_rows), BATCH_SIZE):
        batch = valid_rows[batch_start:batch_start + BATCH_SIZE]

        texts_a = []
        texts_b = []
        for row, sim in batch:
            a = f"{row['anchor_title']} | {row.get('anchor_desc', '')}"
            b = f"{row['neighbor_title']} | {row.get('neighbor_desc', '')}"
            texts_a.append(a)
            texts_b.append(b)
            all_labels.append(int(row["label"]))
            all_similarities.append(sim)

        encoded = tokenizer(
            texts_a, texts_b,
            return_tensors="np",
            max_length=MAX_LENGTH,
            padding=True,
            truncation=True,
        )

        logits = session.run(
            ["logits"],
            {
                "input_ids": encoded["input_ids"].astype(np.int64),
                "attention_mask": encoded["attention_mask"].astype(np.int64),
            }
        )[0]

        all_logits.append(logits)

        done = min(batch_start + BATCH_SIZE, len(valid_rows))
        if done % 1280 == 0 or done == len(valid_rows) or done <= BATCH_SIZE:
            elapsed = time.time() - start
            rate = done / elapsed if elapsed > 0 else 0
            eta = (len(valid_rows) - done) / rate if rate > 0 else 0
            print(f"  {done}/{len(valid_rows)} ({100*done/len(valid_rows):.1f}%) - {rate:.0f}/s - ETA {eta:.0f}s")

    elapsed = time.time() - start
    print(f"\nInference complete: {len(valid_rows)} pairs in {elapsed:.1f}s")

    all_logits = np.vstack(all_logits)
    all_labels = np.array(all_labels)
    all_similarities = np.array(all_similarities)

    # Step 3: Baseline — cross-encoder only
    exp_logits = np.exp(all_logits - all_logits.max(axis=1, keepdims=True))
    ce_probs = exp_logits / exp_logits.sum(axis=1, keepdims=True)
    ce_preds = np.argmax(ce_probs, axis=1)
    ce_confs = ce_probs.max(axis=1)

    ce_acc = accuracy_score(all_labels, ce_preds)
    ce_f1 = f1_score(all_labels, ce_preds, average="macro")
    ce_nll = log_loss(all_labels, ce_probs)
    ce_correct = ce_preds == all_labels

    print(f"\n{'=' * 60}")
    print("BASELINE: Cross-Encoder Only")
    print(f"{'=' * 60}")
    print(f"Accuracy:         {ce_acc:.4f}")
    print(f"F1 (macro):       {ce_f1:.4f}")
    print(f"NLL (log loss):   {ce_nll:.6f}")
    print(f"Avg conf correct: {ce_confs[ce_correct].mean():.4f}")
    print(f"Avg conf wrong:   {ce_confs[~ce_correct].mean():.4f}")

    # Step 4: Ensemble — logistic regression on [logit_diff, similarity]
    logit_diff = all_logits[:, 1] - all_logits[:, 0]
    X = np.column_stack([logit_diff, all_similarities])

    scaler = StandardScaler()
    X_scaled = scaler.fit_transform(X)

    print(f"\n{'=' * 60}")
    print("ENSEMBLE: Cross-Encoder Logits + Similarity Score")
    print(f"{'=' * 60}")
    print("Fitting logistic regression with 5-fold cross-validation...")

    lr = LogisticRegression(max_iter=1000, random_state=42)
    ensemble_probs = cross_val_predict(lr, X_scaled, all_labels, cv=5, method="predict_proba")
    ensemble_preds = np.argmax(ensemble_probs, axis=1)
    ensemble_confs = ensemble_probs.max(axis=1)

    ens_acc = accuracy_score(all_labels, ensemble_preds)
    ens_f1 = f1_score(all_labels, ensemble_preds, average="macro")
    ens_nll = log_loss(all_labels, ensemble_probs)
    ens_correct = ensemble_preds == all_labels

    print(f"Accuracy:         {ens_acc:.4f}")
    print(f"F1 (macro):       {ens_f1:.4f}")
    print(f"NLL (log loss):   {ens_nll:.6f}")
    print(f"Avg conf correct: {ensemble_confs[ens_correct].mean():.4f}")
    print(f"Avg conf wrong:   {ensemble_confs[~ens_correct].mean():.4f}")

    # Step 5: Comparison
    print(f"\n{'=' * 60}")
    print("COMPARISON")
    print(f"{'=' * 60}")
    print(f"{'Metric':<25} {'Cross-Encoder':>15} {'Ensemble':>15} {'Delta':>10}")
    print("-" * 65)
    print(f"{'Accuracy':<25} {ce_acc:>15.4f} {ens_acc:>15.4f} {ens_acc - ce_acc:>+10.4f}")
    print(f"{'F1 (macro)':<25} {ce_f1:>15.4f} {ens_f1:>15.4f} {ens_f1 - ce_f1:>+10.4f}")
    print(f"{'NLL (log loss)':<25} {ce_nll:>15.6f} {ens_nll:>15.6f} {ens_nll - ce_nll:>+10.6f}")
    print(f"{'Conf (correct)':<25} {ce_confs[ce_correct].mean():>15.4f} {ensemble_confs[ens_correct].mean():>15.4f}")
    print(f"{'Conf (wrong)':<25} {ce_confs[~ce_correct].mean():>15.4f} {ensemble_confs[~ens_correct].mean():>15.4f}")

    # Prediction changes
    changed = ce_preds != ensemble_preds
    print(f"\nPredictions changed: {changed.sum()} / {len(all_labels)} ({100*changed.sum()/len(all_labels):.2f}%)")

    if changed.sum() > 0:
        ce_was_correct = ce_correct[changed]
        ens_now_correct = ens_correct[changed]
        flipped_to_correct = (~ce_was_correct & ens_now_correct).sum()
        flipped_to_wrong = (ce_was_correct & ~ens_now_correct).sum()
        print(f"  Flipped wrong→correct: {flipped_to_correct}")
        print(f"  Flipped correct→wrong: {flipped_to_wrong}")
        print(f"  Net improvement: {flipped_to_correct - flipped_to_wrong:+d}")

    # Fit final model and print coefficients
    lr_final = LogisticRegression(max_iter=1000, random_state=42)
    lr_final.fit(X_scaled, all_labels)
    print(f"\nLogistic regression coefficients:")
    print(f"  Logit diff weight:    {lr_final.coef_[0][0]:.4f}")
    print(f"  Similarity weight:    {lr_final.coef_[0][1]:.4f}")
    print(f"  Intercept:            {lr_final.intercept_[0]:.4f}")

    # Calibration buckets
    print(f"\n{'=' * 60}")
    print("CALIBRATION: Ensemble confidence vs actual accuracy")
    print(f"{'=' * 60}")
    print(f"{'Conf Bucket':<15} {'Count':>8} {'Actual Acc':>12} {'Avg Conf':>12}")
    print("-" * 50)
    for lo, hi in [(0.5, 0.6), (0.6, 0.7), (0.7, 0.8), (0.8, 0.9), (0.9, 0.95), (0.95, 1.0)]:
        mask = (ensemble_confs >= lo) & (ensemble_confs < hi)
        if mask.sum() > 0:
            actual_acc = accuracy_score(all_labels[mask], ensemble_preds[mask])
            avg_conf = ensemble_confs[mask].mean()
            print(f"[{lo:.2f}, {hi:.2f}){'':<5} {mask.sum():>8} {actual_acc:>12.4f} {avg_conf:>12.4f}")

    # Also show cross-encoder calibration for comparison
    print(f"\n{'=' * 60}")
    print("CALIBRATION: Cross-encoder confidence vs actual accuracy")
    print(f"{'=' * 60}")
    print(f"{'Conf Bucket':<15} {'Count':>8} {'Actual Acc':>12} {'Avg Conf':>12}")
    print("-" * 50)
    for lo, hi in [(0.5, 0.6), (0.6, 0.7), (0.7, 0.8), (0.8, 0.9), (0.9, 0.95), (0.95, 1.0)]:
        mask = (ce_confs >= lo) & (ce_confs < hi)
        if mask.sum() > 0:
            actual_acc = accuracy_score(all_labels[mask], ce_preds[mask])
            avg_conf = ce_confs[mask].mean()
            print(f"[{lo:.2f}, {hi:.2f}){'':<5} {mask.sum():>8} {actual_acc:>12.4f} {avg_conf:>12.4f}")

    print(f"\nDone.")


if __name__ == "__main__":
    main()
