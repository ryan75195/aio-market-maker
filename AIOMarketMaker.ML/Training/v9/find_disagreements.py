"""Find training set disagreements: pairs where the model's prediction disagrees with the label.

Runs the v8 model on the v9 training data and outputs disagreements sorted by
model confidence. High-confidence disagreements are likely mislabeled.

Usage:
    py -3.12 -u find_disagreements.py                    # GPU (fast, ~15 min)
    py -3.12 -u find_disagreements.py --device cpu        # CPU (no GPU conflict, ~45 min)
    py -3.12 -u find_disagreements.py --category "Birkenstock Arizona Sandals"  # Single category
"""

import argparse
import csv
import os
from pathlib import Path

os.environ.setdefault("HF_HOME", "E:/DevCaches/huggingface")
os.environ.setdefault("TORCH_HOME", "E:/DevCaches/torch")

import numpy as np
import pandas as pd
import torch
from torch.utils.data import DataLoader, Dataset
from transformers import AutoModelForSequenceClassification, AutoTokenizer

csv.field_size_limit(10_000_000)

DATA_DIR = Path(__file__).parent.parent / "data"
V8_MODEL_DIR = "E:/Dev/ml-training/variant-classifier/v8/pytorch"
OUTPUT_DIR = Path(__file__).parent


class PairDataset(Dataset):
    def __init__(self, df, tokenizer, max_length):
        self.df = df.reset_index(drop=True)
        self.tokenizer = tokenizer
        self.max_length = max_length

    def __len__(self):
        return len(self.df)

    def __getitem__(self, idx):
        row = self.df.iloc[idx]
        text_a = f"{row['anchor_title']} | {row['anchor_desc']}"
        text_b = f"{row['neighbor_title']} | {row['neighbor_desc']}"

        encoding = self.tokenizer(
            text_a, text_b,
            max_length=self.max_length,
            padding="max_length",
            truncation=True,
            return_tensors="pt",
        )

        return {
            "input_ids": encoding["input_ids"].squeeze(0),
            "attention_mask": encoding["attention_mask"].squeeze(0),
            "idx": idx,
        }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", type=str, default=str(DATA_DIR / "labeled_pairs_v9_merged.csv"))
    parser.add_argument("--model-dir", type=str, default=V8_MODEL_DIR)
    parser.add_argument("--max-length", type=int, default=256, help="v8 was trained with 256")
    parser.add_argument("--batch-size", type=int, default=64)
    parser.add_argument("--device", type=str, default="cuda" if torch.cuda.is_available() else "cpu")
    parser.add_argument("--category", type=str, default=None, help="Filter to single category")
    args = parser.parse_args()

    device = torch.device(args.device)
    print(f"Device: {device}")

    # Load data
    print(f"Loading data from {args.data}...")
    df = pd.read_csv(args.data)
    df["anchor_desc"] = df["anchor_desc"].fillna("")
    df["neighbor_desc"] = df["neighbor_desc"].fillna("")
    print(f"  Total pairs: {len(df)}")

    if args.category:
        df = df[df["product_name"] == args.category]
        print(f"  Filtered to '{args.category}': {len(df)} pairs")

    # Load model
    print(f"Loading model from {args.model_dir}...")
    tokenizer = AutoTokenizer.from_pretrained(args.model_dir)
    model = AutoModelForSequenceClassification.from_pretrained(args.model_dir, num_labels=2)
    model.to(device)
    model.eval()

    # Run inference
    dataset = PairDataset(df, tokenizer, args.max_length)
    loader = DataLoader(dataset, batch_size=args.batch_size, shuffle=False, num_workers=0)

    all_probs = []
    all_preds = []

    print(f"Running inference on {len(df)} pairs...")
    with torch.no_grad():
        for batch_idx, batch in enumerate(loader):
            input_ids = batch["input_ids"].to(device)
            attention_mask = batch["attention_mask"].to(device)

            outputs = model(input_ids=input_ids, attention_mask=attention_mask)
            probs = torch.softmax(outputs.logits.float(), dim=-1).cpu().numpy()
            preds = np.argmax(probs, axis=-1)

            all_probs.append(probs)
            all_preds.append(preds)

            if (batch_idx + 1) % 100 == 0:
                done = min((batch_idx + 1) * args.batch_size, len(df))
                print(f"  {done}/{len(df)} ({done/len(df)*100:.0f}%)")

    all_probs = np.concatenate(all_probs, axis=0)
    all_preds = np.concatenate(all_preds, axis=0)

    # Add predictions to dataframe
    df = df.reset_index(drop=True)
    df["predicted"] = all_preds
    df["confidence"] = np.max(all_probs, axis=1)
    df["prob_different"] = all_probs[:, 0]
    df["prob_same"] = all_probs[:, 1]
    df["disagrees"] = df["label"] != df["predicted"]

    # Summary
    n_disagree = df["disagrees"].sum()
    n_total = len(df)
    print(f"\n=== Results ===")
    print(f"  Total pairs:    {n_total}")
    print(f"  Agreements:     {n_total - n_disagree} ({(n_total - n_disagree)/n_total*100:.1f}%)")
    print(f"  Disagreements:  {n_disagree} ({n_disagree/n_total*100:.1f}%)")

    # Disagreements by category
    if not args.category:
        print(f"\n--- Disagreement rate by category (worst first) ---")
        cat_stats = df.groupby("product_name").agg(
            total=("label", "count"),
            disagreements=("disagrees", "sum"),
        )
        cat_stats["rate"] = cat_stats["disagreements"] / cat_stats["total"]
        cat_stats = cat_stats.sort_values("rate", ascending=False)

        for cat, row in cat_stats.head(30).iterrows():
            print(f"  {cat:<45} {row['disagreements']:>4}/{row['total']:<4} ({row['rate']*100:.1f}%)")

    # Save disagreements
    disagreements = df[df["disagrees"]].sort_values("confidence", ascending=False)

    # Select columns for output
    output_cols = [
        "product_name", "anchor_title", "neighbor_title",
        "anchor_desc", "neighbor_desc",
        "label", "predicted", "confidence", "prob_different", "prob_same",
    ]
    # Only include columns that exist
    output_cols = [c for c in output_cols if c in disagreements.columns]

    suffix = f"_{args.category.replace(' ', '_')}" if args.category else ""
    output_path = OUTPUT_DIR / f"disagreements{suffix}.csv"
    disagreements[output_cols].to_csv(output_path, index=False)
    print(f"\nDisagreements saved to {output_path}")
    print(f"  Total disagreements: {len(disagreements)}")
    if len(disagreements) > 0:
        print(f"  Highest confidence disagreement: {disagreements['confidence'].iloc[0]:.4f}")
        print(f"  Top 5 most confident disagreements:")
        for _, row in disagreements.head(5).iterrows():
            label_str = "same" if row["label"] == 1 else "diff"
            pred_str = "same" if row["predicted"] == 1 else "diff"
            print(f"    [{row['product_name']}] label={label_str} pred={pred_str} conf={row['confidence']:.3f}")
            print(f"      A: {row['anchor_title'][:80]}")
            print(f"      B: {row['neighbor_title'][:80]}")


if __name__ == "__main__":
    main()
