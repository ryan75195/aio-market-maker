"""
Fine-tune Qwen3-8B as an evaluator for ONNX cross-encoder decisions.

Uses Unsloth QLoRA for efficient fine-tuning on RTX 5070 Ti (16GB VRAM).
Falls back to vanilla HuggingFace PEFT if Unsloth doesn't support the model.

Usage:
    py -3.12 train.py                     # full training
    py -3.12 train.py --dry-run            # load model, show data stats, exit
    py -3.12 train.py --epochs 1           # quick test run
    py -3.12 train.py --output-dir E:/Dev/ml-training/evaluator/v2/lora_adapter
"""

import argparse
import csv
import json
import os
import random
import sys
from pathlib import Path

# Redirect caches to E: drive
os.environ.setdefault("HF_HOME", "E:/DevCaches/huggingface")
os.environ.setdefault("TORCH_HOME", "E:/DevCaches/torch")
# RTX 5070 Ti (SM120/Blackwell) is too new for xformers — use PyTorch SDPA instead
os.environ["XFORMERS_DISABLED"] = "1"

import torch
from sklearn.model_selection import train_test_split

# Try Unsloth first, fall back to vanilla PEFT
try:
    from unsloth import FastLanguageModel
    USE_UNSLOTH = True
    print("Using Unsloth for fine-tuning")
except ImportError:
    from transformers import AutoModelForCausalLM, AutoTokenizer
    from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training
    USE_UNSLOTH = False
    print("Unsloth not available, using vanilla PEFT")

from transformers import TrainingArguments, Trainer
from torch.utils.data import Dataset

DATA_DIR = Path(__file__).parent.parent / "data"
AUDIT_CSV = DATA_DIR / "evaluator_audit_gpt.csv"
DEFAULT_OUTPUT = "E:/Dev/ml-training/evaluator/v1/lora_adapter"
DEFAULT_MERGED = "E:/Dev/ml-training/evaluator/v1/merged"

BASE_MODEL = "Qwen/Qwen3-8B"
MAX_SEQ_LENGTH = 512
LORA_RANK = 16
LORA_ALPHA = 32


def parse_args():
    parser = argparse.ArgumentParser(description="Train evaluator model")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--epochs", type=int, default=3)
    parser.add_argument("--batch-size", type=int, default=4)
    parser.add_argument("--lr", type=float, default=2e-4)
    parser.add_argument("--output-dir", type=str, default=DEFAULT_OUTPUT)
    parser.add_argument("--merged-dir", type=str, default=DEFAULT_MERGED)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--test-size", type=float, default=0.1)
    return parser.parse_args()


def load_audit_data(test_size=0.1, seed=42):
    """Load GPT audit CSV and split into train/test."""
    with open(AUDIT_CSV, newline="", encoding="utf-8") as f:
        rows = [r for r in csv.DictReader(f) if r["verdict"] != "error"]

    print(f"Loaded {len(rows)} audited pairs (excluding errors)")

    train_rows, test_rows = train_test_split(
        rows, test_size=test_size, random_state=seed,
        stratify=[r["verdict"] for r in rows],
    )

    # Save test set for eval.py
    test_path = DATA_DIR / "evaluator_test.csv"
    with open(test_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=rows[0].keys())
        writer.writeheader()
        writer.writerows(test_rows)
    print(f"Test set ({len(test_rows)} pairs) saved to {test_path}")

    train_path = DATA_DIR / "evaluator_train.csv"
    with open(train_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=rows[0].keys())
        writer.writeheader()
        writer.writerows(train_rows)
    print(f"Train set ({len(train_rows)} pairs) saved to {train_path}")

    return train_rows, test_rows


def format_chat(row):
    """Format a row into the chat template for training."""
    onnx_label = int(row["onnx_label"])
    label_text = "COMPARABLE" if onnx_label == 1 else "NOT COMPARABLE"
    confidence = float(row["similarity_score"])

    user_msg = (
        f"The classifier labeled this pair as {label_text} "
        f"(confidence: {confidence:.2f}).\n\n"
        f"LISTING A:\nTitle: {row['title_a']}\n"
        f"Description: {row['desc_a'][:500]}\n\n"
        f"LISTING B:\nTitle: {row['title_b']}\n"
        f"Description: {row['desc_b'][:500]}\n\n"
        f"Is the classifier's decision correct?"
    )

    assistant_msg = json.dumps({
        "verdict": row["verdict"],
        "correct_label": int(row["correct_label"]),
        "error_type": row["error_type"] if row["error_type"] else None,
        "reasoning": row["reasoning"],
    }, ensure_ascii=False)

    return user_msg, assistant_msg


class EvaluatorDataset(Dataset):
    def __init__(self, rows, tokenizer, max_length=MAX_SEQ_LENGTH):
        self.examples = []
        for row in rows:
            user_msg, assistant_msg = format_chat(row)
            # Use chat template
            messages = [
                {"role": "user", "content": user_msg},
                {"role": "assistant", "content": assistant_msg},
            ]
            text = tokenizer.apply_chat_template(
                messages, tokenize=False, add_generation_prompt=False
            )
            encoded = tokenizer(
                text, truncation=True, max_length=max_length,
                padding="max_length", return_tensors="pt",
            )
            input_ids = encoded["input_ids"].squeeze()
            attention_mask = encoded["attention_mask"].squeeze()
            # Labels = input_ids (causal LM), mask padding with -100
            labels = input_ids.clone()
            labels[attention_mask == 0] = -100
            self.examples.append({
                "input_ids": input_ids,
                "attention_mask": attention_mask,
                "labels": labels,
            })

    def __len__(self):
        return len(self.examples)

    def __getitem__(self, idx):
        return self.examples[idx]


def main():
    args = parse_args()
    random.seed(args.seed)
    torch.manual_seed(args.seed)

    # Load and split data
    train_rows, test_rows = load_audit_data(args.test_size, args.seed)

    from collections import Counter
    train_verdicts = Counter(r["verdict"] for r in train_rows)
    print(f"\nTrain distribution: {dict(train_verdicts)}")
    test_verdicts = Counter(r["verdict"] for r in test_rows)
    print(f"Test distribution:  {dict(test_verdicts)}")

    if args.dry_run:
        print("\n[DRY RUN] Would load model and train. Exiting.")
        return

    # Load model
    print(f"\nLoading {BASE_MODEL}...")
    if USE_UNSLOTH:
        model, tokenizer = FastLanguageModel.from_pretrained(
            BASE_MODEL,
            max_seq_length=MAX_SEQ_LENGTH,
            load_in_4bit=True,
            dtype=torch.bfloat16,
        )
        model = FastLanguageModel.get_peft_model(
            model,
            r=LORA_RANK,
            lora_alpha=LORA_ALPHA,
            target_modules=["q_proj", "k_proj", "v_proj", "o_proj",
                            "gate_proj", "up_proj", "down_proj"],
            lora_dropout=0.05,
            bias="none",
            use_gradient_checkpointing="unsloth",
        )
    else:
        from transformers import BitsAndBytesConfig
        bnb_config = BitsAndBytesConfig(
            load_in_4bit=True,
            bnb_4bit_quant_type="nf4",
            bnb_4bit_compute_dtype=torch.bfloat16,
        )
        model = AutoModelForCausalLM.from_pretrained(
            BASE_MODEL, quantization_config=bnb_config,
            device_map="auto", torch_dtype=torch.bfloat16,
        )
        tokenizer = AutoTokenizer.from_pretrained(BASE_MODEL)
        model = prepare_model_for_kbit_training(model)
        lora_config = LoraConfig(
            r=LORA_RANK, lora_alpha=LORA_ALPHA,
            target_modules=["q_proj", "k_proj", "v_proj", "o_proj",
                            "gate_proj", "up_proj", "down_proj"],
            lora_dropout=0.05, bias="none", task_type="CAUSAL_LM",
        )
        model = get_peft_model(model, lora_config)

    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    model.print_trainable_parameters()

    # Build datasets
    print("Tokenizing train set...")
    train_dataset = EvaluatorDataset(train_rows, tokenizer)
    print(f"Train examples: {len(train_dataset)}")

    # Training
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    training_args = TrainingArguments(
        output_dir=str(output_dir),
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch_size,
        gradient_accumulation_steps=4,
        learning_rate=args.lr,
        bf16=True,
        logging_steps=10,
        save_strategy="epoch",
        warmup_ratio=0.1,
        weight_decay=0.01,
        seed=args.seed,
        report_to="none",
    )

    trainer = Trainer(
        model=model,
        args=training_args,
        train_dataset=train_dataset,
    )

    print(f"\nStarting training: {args.epochs} epochs, batch={args.batch_size}, lr={args.lr}")
    trainer.train()

    # Save LoRA adapter
    model.save_pretrained(str(output_dir))
    tokenizer.save_pretrained(str(output_dir))
    print(f"\nLoRA adapter saved to {output_dir}")

    # Merge and save full model for inference
    merged_dir = Path(args.merged_dir)
    merged_dir.mkdir(parents=True, exist_ok=True)
    print(f"Merging LoRA weights into base model...")
    if USE_UNSLOTH:
        model.save_pretrained_merged(str(merged_dir), tokenizer)
    else:
        merged_model = model.merge_and_unload()
        merged_model.save_pretrained(str(merged_dir))
        tokenizer.save_pretrained(str(merged_dir))
    print(f"Merged model saved to {merged_dir}")


if __name__ == "__main__":
    main()
