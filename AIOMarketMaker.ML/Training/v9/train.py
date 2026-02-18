"""
Train v9 cross-encoder for eBay variant classification.

Changes from v8:
  - max_length: 256 -> 512 (full RoBERTa capacity)
  - Descriptions: full text (median 756 chars vs v8's 256)
  - batch_size: 16 -> 8 (2x sequence length, halved batch)
  - gradient_accumulation_steps: 2 (effective batch = 16)
  - Data: labeled_pairs_v9_merged.csv (143K pairs, 2K corrections)
  - Tokenizer truncation='longest_first' balances both sides naturally

Usage:
    py -3.12 -u train.py
    py -3.12 -u train.py --batch-size 4 --grad-accum 4
    py -3.12 -u train.py --max-length 256  # compare against v8 config
"""

import argparse
import csv
import json
import os
import time
from collections import Counter
from pathlib import Path

# Redirect HuggingFace and torch caches to E: drive to save C: space
os.environ.setdefault("HF_HOME", "E:/DevCaches/huggingface")
os.environ.setdefault("TORCH_HOME", "E:/DevCaches/torch")

import numpy as np
import pandas as pd
import torch
from sklearn.metrics import (
    accuracy_score,
    classification_report,
    confusion_matrix,
    f1_score,
    precision_score,
    recall_score,
)
from sklearn.model_selection import train_test_split
from torch.utils.data import Dataset
from transformers import (
    AutoModelForSequenceClassification,
    AutoTokenizer,
    EarlyStoppingCallback,
    Trainer,
    TrainingArguments,
)

csv.field_size_limit(10_000_000)

DATA_DIR = Path(__file__).parent.parent / "data"


def parse_args():
    parser = argparse.ArgumentParser(description="Train v9 variant classifier")
    parser.add_argument(
        "--data",
        type=str,
        default=str(DATA_DIR / "labeled_pairs_v9_merged.csv"),
        help="Path to labeled pairs CSV",
    )
    parser.add_argument(
        "--model-name",
        type=str,
        default="FacebookAI/roberta-large",
        help="HuggingFace model name",
    )
    parser.add_argument(
        "--output-dir",
        type=str,
        default="E:/Dev/ml-training/variant-classifier/v9/pytorch",
        help="Directory to save model and tokenizer",
    )
    parser.add_argument("--epochs", type=int, default=3, help="Number of training epochs")
    parser.add_argument("--batch-size", type=int, default=8, help="Batch size per device")
    parser.add_argument("--grad-accum", type=int, default=2,
                        help="Gradient accumulation steps (effective batch = batch-size * grad-accum)")
    parser.add_argument("--lr", type=float, default=1e-5, help="Learning rate")
    parser.add_argument("--max-length", type=int, default=512, help="Max token length")
    parser.add_argument("--seed", type=int, default=42, help="Random seed")
    parser.add_argument("--warmup-steps", type=int, default=500, help="LR warmup steps")
    parser.add_argument("--patience", type=int, default=5, help="Early stopping patience (eval intervals)")
    parser.add_argument(
        "--high-confidence-only",
        action="store_true",
        help="Filter to high-confidence labels only",
    )
    parser.add_argument(
        "--resume",
        nargs="?",
        const=True,
        default=False,
        help="Resume from checkpoint. Use --resume for latest, or --resume <path> for specific checkpoint",
    )
    return parser.parse_args()


class VariantPairDataset(Dataset):
    """Dataset for variant pair classification.

    Each sample is a sentence pair: anchor (title | desc) and neighbor (title | desc).
    Tokenizer uses longest_first truncation to balance both sides.
    """

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
            text_a,
            text_b,
            max_length=self.max_length,
            padding="max_length",
            truncation=True,
            return_tensors="pt",
        )

        return {
            "input_ids": encoding["input_ids"].squeeze(0),
            "attention_mask": encoding["attention_mask"].squeeze(0),
            "labels": torch.tensor(row["label"], dtype=torch.long),
        }


class WeightedTrainer(Trainer):
    """Trainer with weighted cross-entropy loss for class imbalance."""

    def __init__(self, class_weights, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.class_weights = class_weights

    def compute_loss(self, model, inputs, return_outputs=False, **kwargs):
        labels = inputs.pop("labels")
        outputs = model(**inputs)
        logits = outputs.logits.float()
        weight = self.class_weights.to(logits.device)
        loss_fn = torch.nn.CrossEntropyLoss(weight=weight)
        loss = loss_fn(logits, labels)
        return (loss, outputs) if return_outputs else loss


def compute_metrics(eval_pred):
    logits, labels = eval_pred
    preds = np.argmax(logits, axis=-1)
    return {
        "accuracy": accuracy_score(labels, preds),
        "f1_macro": f1_score(labels, preds, average="macro"),
        "f1_same": f1_score(labels, preds, pos_label=1, average="binary"),
        "f1_different": f1_score(labels, preds, pos_label=0, average="binary"),
        "precision_macro": precision_score(labels, preds, average="macro"),
        "recall_macro": recall_score(labels, preds, average="macro"),
    }


def load_and_split(data_path, seed, high_confidence_only):
    print(f"Loading data from {data_path}...")
    df = pd.read_csv(data_path)
    print(f"  Total pairs: {len(df)}")

    # Fill NaN descriptions
    df["anchor_desc"] = df["anchor_desc"].fillna("")
    df["neighbor_desc"] = df["neighbor_desc"].fillna("")

    if high_confidence_only:
        df = df[df["confidence"] == "high"]
        print(f"  After high-confidence filter: {len(df)}")

    # Label distribution
    label_counts = df["label"].value_counts()
    print(f"  Label distribution: {dict(label_counts)}")
    print(f"  Same ratio: {label_counts.get(1, 0) / len(df):.1%}")

    # Stratified split by compound key: product_name + label
    df["_stratify_key"] = df["product_name"].astype(str) + "_" + df["label"].astype(str)

    # Need at least 10 samples per stratify key to survive 80/10/10 cascading splits
    key_counts = df["_stratify_key"].value_counts()
    rare_keys = key_counts[key_counts < 10].index
    if len(rare_keys) > 0:
        df.loc[df["_stratify_key"].isin(rare_keys), "_stratify_key"] = (
            "rare_" + df.loc[df["_stratify_key"].isin(rare_keys), "label"].astype(str)
        )

    # 80/20 split first, then split the 20 into 10/10
    train_df, temp_df = train_test_split(
        df, test_size=0.2, random_state=seed, stratify=df["_stratify_key"]
    )

    val_df, test_df = train_test_split(
        temp_df, test_size=0.5, random_state=seed, stratify=temp_df["label"]
    )

    for split_df in [train_df, val_df, test_df]:
        split_df.drop(columns=["_stratify_key"], inplace=True)

    # Augment training set: swap anchor/neighbor (teaches order invariance, 2x data)
    swapped = train_df.copy()
    swapped = swapped.rename(columns={
        "anchor_id": "neighbor_id", "neighbor_id": "anchor_id",
        "anchor_title": "neighbor_title", "neighbor_title": "anchor_title",
        "anchor_desc": "neighbor_desc", "neighbor_desc": "anchor_desc",
    })
    train_df = pd.concat([train_df, swapped], ignore_index=True)
    print(f"  Train: {len(train_df)} (with swap augmentation), Val: {len(val_df)}, Test: {len(test_df)}")
    return train_df, val_df, test_df


def compute_class_weights(train_df):
    counts = Counter(train_df["label"].values)
    total = sum(counts.values())
    n_classes = len(counts)
    weights = {cls: total / (n_classes * count) for cls, count in counts.items()}
    weight_tensor = torch.tensor([weights[0], weights[1]], dtype=torch.float32)
    print(f"  Class weights: different={weight_tensor[0]:.3f}, same={weight_tensor[1]:.3f}")
    return weight_tensor


def per_category_f1(test_df, model, tokenizer, max_length, device):
    """Compute F1 per product_name category on test set."""
    categories = test_df["product_name"].unique()
    results = {}

    model.eval()
    for cat in sorted(categories):
        cat_df = test_df[test_df["product_name"] == cat]
        if len(cat_df) < 2:
            continue

        dataset = VariantPairDataset(cat_df, tokenizer, max_length)
        all_preds = []
        all_labels = []

        with torch.no_grad():
            for i in range(len(dataset)):
                item = dataset[i]
                input_ids = item["input_ids"].unsqueeze(0).to(device)
                attention_mask = item["attention_mask"].unsqueeze(0).to(device)
                label = item["labels"].item()

                outputs = model(input_ids=input_ids, attention_mask=attention_mask)
                pred = torch.argmax(outputs.logits, dim=-1).item()

                all_preds.append(pred)
                all_labels.append(label)

        if len(set(all_labels)) < 2:
            acc = accuracy_score(all_labels, all_preds)
            results[cat] = {
                "n_samples": len(cat_df),
                "accuracy": acc,
                "f1_macro": None,
                "note": "single class in test split",
            }
        else:
            f1 = f1_score(all_labels, all_preds, average="macro")
            acc = accuracy_score(all_labels, all_preds)
            results[cat] = {
                "n_samples": len(cat_df),
                "accuracy": acc,
                "f1_macro": f1,
            }

    return results


def main():
    args = parse_args()
    start_time = time.time()

    torch.manual_seed(args.seed)
    np.random.seed(args.seed)

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Device: {device}")
    if torch.cuda.is_available():
        print(f"  GPU: {torch.cuda.get_device_name(0)}")
        print(f"  VRAM: {torch.cuda.get_device_properties(0).total_memory / 1024**3:.1f} GB")

    print(f"\n=== v9 Training Config ===")
    print(f"  max_length:    {args.max_length} tokens (v8: 256)")
    print(f"  batch_size:    {args.batch_size} (v8: 16)")
    print(f"  grad_accum:    {args.grad_accum} (effective batch: {args.batch_size * args.grad_accum})")
    print(f"  lr:            {args.lr}")
    print(f"  epochs:        {args.epochs}")
    print()

    # Load and split data
    train_df, val_df, test_df = load_and_split(args.data, args.seed, args.high_confidence_only)

    # Compute class weights from training set
    class_weights = compute_class_weights(train_df)

    # Load tokenizer and model
    print(f"\nLoading model: {args.model_name}")
    tokenizer = AutoTokenizer.from_pretrained(args.model_name)
    model = AutoModelForSequenceClassification.from_pretrained(
        args.model_name, num_labels=2
    )

    # Create datasets
    train_dataset = VariantPairDataset(train_df, tokenizer, args.max_length)
    val_dataset = VariantPairDataset(val_df, tokenizer, args.max_length)
    test_dataset = VariantPairDataset(test_df, tokenizer, args.max_length)

    # Training arguments
    training_args = TrainingArguments(
        output_dir=f"{args.output_dir}_checkpoints",
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch_size,
        per_device_eval_batch_size=args.batch_size * 2,
        gradient_accumulation_steps=args.grad_accum,
        learning_rate=args.lr,
        lr_scheduler_type="cosine",
        warmup_steps=args.warmup_steps,
        weight_decay=0.01,
        fp16=False,
        bf16=torch.cuda.is_available(),
        eval_strategy="steps",
        eval_steps=500,
        save_strategy="steps",
        save_steps=500,
        save_total_limit=3,
        load_best_model_at_end=True,
        metric_for_best_model="eval_f1_macro",
        greater_is_better=True,
        logging_steps=50,
        report_to="none",
        seed=args.seed,
        dataloader_num_workers=0,  # Windows compatibility
    )

    # Create trainer
    trainer = WeightedTrainer(
        class_weights=class_weights,
        model=model,
        args=training_args,
        train_dataset=train_dataset,
        eval_dataset=val_dataset,
        compute_metrics=compute_metrics,
        callbacks=[EarlyStoppingCallback(early_stopping_patience=args.patience)],
    )

    # Train
    effective_batch = args.batch_size * args.grad_accum
    steps_per_epoch = len(train_dataset) // effective_batch
    print(f"\nStarting training: {args.epochs} epochs")
    print(f"  Training samples: {len(train_dataset)}")
    print(f"  Steps per epoch:  {steps_per_epoch}")
    print(f"  Total steps:      {steps_per_epoch * args.epochs}")

    resume_checkpoint = None
    if args.resume:
        if args.resume is True:
            resume_checkpoint = True  # auto-detect latest
            print(f"  Resuming from latest checkpoint in {training_args.output_dir}")
        else:
            resume_checkpoint = args.resume
            print(f"  Resuming from checkpoint: {resume_checkpoint}")

    train_result = trainer.train(resume_from_checkpoint=resume_checkpoint)
    train_time = time.time() - start_time

    # Evaluate on validation set
    print("\n--- Validation Results ---")
    val_metrics = trainer.evaluate(val_dataset)
    for k, v in sorted(val_metrics.items()):
        if isinstance(v, float):
            print(f"  {k}: {v:.4f}")

    # Evaluate on test set
    print("\n--- Test Results ---")
    test_metrics = trainer.evaluate(test_dataset, metric_key_prefix="test")
    for k, v in sorted(test_metrics.items()):
        if isinstance(v, float):
            print(f"  {k}: {v:.4f}")

    # Detailed test predictions
    test_predictions = trainer.predict(test_dataset)
    test_preds = np.argmax(test_predictions.predictions, axis=-1)
    test_labels = test_predictions.label_ids

    print("\nClassification Report:")
    print(
        classification_report(
            test_labels, test_preds, target_names=["different", "same"]
        )
    )

    cm = confusion_matrix(test_labels, test_preds)
    print("Confusion Matrix:")
    print(f"  TN={cm[0][0]}  FP={cm[0][1]}")
    print(f"  FN={cm[1][0]}  TP={cm[1][1]}")

    # Per-category F1 analysis
    print("\n--- Per-Category F1 (Test Set) ---")
    category_results = per_category_f1(
        test_df, trainer.model, tokenizer, args.max_length, device
    )
    f1_values = [
        v["f1_macro"] for v in category_results.values() if v["f1_macro"] is not None
    ]
    if f1_values:
        worst_cat = min(category_results.items(), key=lambda x: x[1].get("f1_macro") or 1.0)
        best_cat = max(category_results.items(), key=lambda x: x[1].get("f1_macro") or 0.0)
        print(f"\n  Best:  {best_cat[0]} (F1={best_cat[1]['f1_macro']:.3f}, n={best_cat[1]['n_samples']})")
        print(f"  Worst: {worst_cat[0]} (F1={worst_cat[1]['f1_macro']:.3f}, n={worst_cat[1]['n_samples']})")
        print(f"  Mean:  {np.mean(f1_values):.3f}")
        print(f"  Floor: {min(f1_values):.3f}")

    print("\n  Category breakdown:")
    for cat, metrics in sorted(
        category_results.items(),
        key=lambda x: x[1].get("f1_macro") or 0.0,
    ):
        f1_str = f"{metrics['f1_macro']:.3f}" if metrics["f1_macro"] is not None else "N/A"
        print(f"    {cat:<45} F1={f1_str}  n={metrics['n_samples']}")

    # Save model and tokenizer
    print(f"\nSaving model to {args.output_dir}/")
    os.makedirs(args.output_dir, exist_ok=True)
    trainer.save_model(args.output_dir)
    tokenizer.save_pretrained(args.output_dir)

    # Compile results
    results = {
        "version": "v9",
        "model_name": args.model_name,
        "data_file": args.data,
        "high_confidence_only": args.high_confidence_only,
        "total_pairs": len(train_df) + len(val_df) + len(test_df),
        "train_size": len(train_df),
        "val_size": len(val_df),
        "test_size": len(test_df),
        "epochs": args.epochs,
        "batch_size": args.batch_size,
        "gradient_accumulation_steps": args.grad_accum,
        "effective_batch_size": args.batch_size * args.grad_accum,
        "lr": args.lr,
        "max_length": args.max_length,
        "seed": args.seed,
        "training_time_seconds": round(train_time, 1),
        "training_steps": train_result.global_step,
        "changes_from_v8": {
            "max_length": "256 -> 512",
            "descriptions": "200 char truncated -> full (median 756 chars)",
            "corrections_added": 2052,
            "empty_desc_pairs_removed": True,
        },
        "validation": {
            k.replace("eval_", ""): round(v, 4)
            for k, v in val_metrics.items()
            if isinstance(v, float)
        },
        "test": {
            k.replace("test_", ""): round(v, 4)
            for k, v in test_metrics.items()
            if isinstance(v, float)
        },
        "confusion_matrix": {
            "TN": int(cm[0][0]),
            "FP": int(cm[0][1]),
            "FN": int(cm[1][0]),
            "TP": int(cm[1][1]),
        },
        "per_category_f1": {
            cat: {k: round(v, 4) if isinstance(v, float) else v for k, v in metrics.items()}
            for cat, metrics in category_results.items()
        },
        "baseline_comparison": {
            "mlp_v4_f1": 0.698,
            "bert_base_v5_f1": 0.871,
            "roberta_v6_f1": 0.903,
            "roberta_v7_f1": 0.920,
            "roberta_v8_f1": 0.913,
            "this_model_f1": round(test_metrics.get("test_f1_macro", 0), 4),
            "improvement_over_v8": round(
                test_metrics.get("test_f1_macro", 0) - 0.913, 4
            ),
        },
    }

    results_path = os.path.join(args.output_dir, "results_v9.json")
    with open(results_path, "w") as f:
        json.dump(results, f, indent=2)
    print(f"\nResults saved to {results_path}")

    # Summary
    print("\n" + "=" * 60)
    print("SUMMARY")
    print("=" * 60)
    print(f"  Model:         {args.model_name}")
    print(f"  Max tokens:    {args.max_length} (v8: 256)")
    print(f"  Test F1 macro: {test_metrics.get('test_f1_macro', 0):.4f}")
    print(f"  Test accuracy: {test_metrics.get('test_accuracy', 0):.4f}")
    print(f"  V8 baseline:   0.913")
    print(f"  vs V8:         {test_metrics.get('test_f1_macro', 0) - 0.913:+.4f}")
    target_met = test_metrics.get("test_f1_macro", 0) >= 0.93
    print(f"  Target (0.93): {'MET' if target_met else 'NOT MET'}")
    print(f"  Training time: {train_time:.0f}s ({train_time/60:.1f}min)")
    print("=" * 60)


if __name__ == "__main__":
    main()
