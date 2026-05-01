"""
Fine-tune Qwen3-4B for product attribute extraction using QLoRA.

Teacher: GPT-5-nano labels (from generate_training_data.py).
Student: Qwen3-4B with LoRA adapters.

The fine-tuned model learns to:
  1. Only extract values from the provided value list
  2. Return null for axes not clearly present in the title
  3. Return null for accessory/part listings
  4. Include ALL axes in the response with explicit nulls

Usage:
    python finetune_extraction.py
    python finetune_extraction.py --epochs 5 --lr 2e-4
    python finetune_extraction.py --resume  # resume from checkpoint
"""

import argparse
import json
from pathlib import Path

from experiment_topdown_taxonomy import OUTPUT_DIR

TRAIN_DIR = OUTPUT_DIR / "training"
MODEL_DIR = OUTPUT_DIR / "models" / "qwen3-4b-extraction"


def load_training_data():
    """Load training and validation JSONL files."""
    train_path = TRAIN_DIR / "train.jsonl"
    val_path = TRAIN_DIR / "val.jsonl"

    if not train_path.exists():
        raise FileNotFoundError(
            f"Training data not found at {train_path}. "
            f"Run generate_training_data.py first."
        )

    train_data = []
    with open(train_path, encoding="utf-8") as f:
        for line in f:
            train_data.append(json.loads(line))

    val_data = []
    if val_path.exists():
        with open(val_path, encoding="utf-8") as f:
            for line in f:
                val_data.append(json.loads(line))

    return train_data, val_data


def main():
    parser = argparse.ArgumentParser(
        description="Fine-tune Qwen3-4B for extraction"
    )
    parser.add_argument(
        "--model", default="unsloth/Qwen3-4B",
        help="Base model to fine-tune"
    )
    parser.add_argument("--epochs", type=int, default=3)
    parser.add_argument("--lr", type=float, default=2e-4)
    parser.add_argument("--batch-size", type=int, default=4)
    parser.add_argument("--grad-accum", type=int, default=4)
    parser.add_argument("--lora-r", type=int, default=16)
    parser.add_argument("--max-seq-len", type=int, default=1024)
    parser.add_argument(
        "--resume", action="store_true",
        help="Resume from last checkpoint"
    )
    args = parser.parse_args()

    MODEL_DIR.mkdir(parents=True, exist_ok=True)

    # ── Load training data ──────────────────────────────────────────────────
    print("Loading training data...")
    train_data, val_data = load_training_data()
    print(f"Train: {len(train_data)}, Val: {len(val_data)}")

    # ── Load model with unsloth ─────────────────────────────────────────────
    from unsloth import FastLanguageModel

    print(f"\nLoading {args.model}...")
    model, tokenizer = FastLanguageModel.from_pretrained(
        model_name=args.model,
        max_seq_length=args.max_seq_len,
        load_in_4bit=True,
        dtype=None,  # auto-detect
    )

    # ── Add LoRA adapters ───────────────────────────────────────────────────
    model = FastLanguageModel.get_peft_model(
        model,
        r=args.lora_r,
        lora_alpha=args.lora_r * 2,
        lora_dropout=0,
        target_modules=[
            "q_proj", "k_proj", "v_proj", "o_proj",
            "gate_proj", "up_proj", "down_proj",
        ],
        bias="none",
        use_gradient_checkpointing="unsloth",
    )

    # Print trainable parameters
    trainable = sum(p.numel() for p in model.parameters() if p.requires_grad)
    total = sum(p.numel() for p in model.parameters())
    print(f"Trainable: {trainable:,} / {total:,} "
          f"({trainable/total*100:.2f}%)")

    # ── Format datasets ─────────────────────────────────────────────────────
    from datasets import Dataset

    def format_example(example):
        """Convert chat messages to the tokenizer's chat format."""
        text = tokenizer.apply_chat_template(
            example["messages"],
            tokenize=False,
            add_generation_prompt=False,
        )
        return {"text": text}

    train_dataset = Dataset.from_list(train_data).map(format_example)
    val_dataset = (
        Dataset.from_list(val_data).map(format_example)
        if val_data else None
    )

    # ── Training ────────────────────────────────────────────────────────────
    from trl import SFTTrainer, SFTConfig

    effective_batch = args.batch_size * args.grad_accum
    steps_per_epoch = len(train_data) // effective_batch
    total_steps = steps_per_epoch * args.epochs

    # completion_only_loss: only compute loss on assistant responses,
    # not the prompt. More sample-efficient for our small dataset.
    training_args = SFTConfig(
        output_dir=str(MODEL_DIR / "checkpoints"),
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch_size,
        gradient_accumulation_steps=args.grad_accum,
        learning_rate=args.lr,
        warmup_ratio=0.1,
        weight_decay=0.01,
        logging_steps=max(1, steps_per_epoch // 5),
        eval_strategy="epoch" if val_dataset else "no",
        save_strategy="epoch",
        save_total_limit=2,
        bf16=True,
        optim="adamw_8bit",
        seed=42,
        report_to="none",
        dataloader_num_workers=0,  # Windows compatibility
        max_seq_length=args.max_seq_len,
        dataset_text_field="text",
        packing=False,
        completion_only_loss=True,
    )

    trainer = SFTTrainer(
        model=model,
        tokenizer=tokenizer,
        train_dataset=train_dataset,
        eval_dataset=val_dataset,
        args=training_args,
    )

    print(f"\nStarting fine-tuning...")
    print(f"  Epochs: {args.epochs}")
    print(f"  Effective batch size: {effective_batch}")
    print(f"  Learning rate: {args.lr}")
    print(f"  LoRA rank: {args.lora_r}")
    print(f"  Steps/epoch: ~{steps_per_epoch}")
    print(f"  Total steps: ~{total_steps}")

    checkpoint = True if args.resume else None
    trainer.train(resume_from_checkpoint=checkpoint)

    # ── Save LoRA adapter ───────────────────────────────────────────────────
    adapter_path = MODEL_DIR / "lora_adapter"
    model.save_pretrained(str(adapter_path))
    tokenizer.save_pretrained(str(adapter_path))

    # Save training config for reproducibility
    config = {
        "base_model": args.model,
        "lora_r": args.lora_r,
        "lora_alpha": args.lora_r * 2,
        "epochs": args.epochs,
        "lr": args.lr,
        "batch_size": args.batch_size,
        "grad_accum": args.grad_accum,
        "max_seq_len": args.max_seq_len,
        "train_examples": len(train_data),
        "val_examples": len(val_data),
    }
    with open(MODEL_DIR / "training_config.json", "w") as f:
        json.dump(config, f, indent=2)

    print(f"\n{'='*60}")
    print(f"Fine-tuning complete!")
    print(f"{'='*60}")
    print(f"LoRA adapter: {adapter_path}")
    print(f"Config: {MODEL_DIR / 'training_config.json'}")
    print(f"\nTo evaluate:")
    print(f"  python score_eval_set.py --finetuned")


if __name__ == "__main__":
    main()
