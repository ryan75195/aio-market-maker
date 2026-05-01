"""
Fine-tune Qwen3-8B on the variant classification task using Unsloth QLoRA.

Dataset: labeled_pairs_v10.csv (143K GPT-5-mini labeled pairs with reasoning)
Output: QLoRA adapter saved to ./output/qwen3-8b-variant-classifier/

Usage:
    python finetune_qwen3.py                        # full training (~7-8 hours)
    python finetune_qwen3.py --max-samples 10000    # quick test run (~15 min)
    python finetune_qwen3.py --resume                # resume from last checkpoint
"""

import argparse
import csv
import json
import logging
import os
import sys
import random
import time
from datetime import datetime
from pathlib import Path

csv.field_size_limit(10_000_000)

# Windows workarounds
os.environ["TORCHINDUCTOR_CACHE_DIR"] = "C:/tmp/ti"
os.environ["TRITON_CACHE_DIR"] = "C:/tmp/triton"
os.environ["UNSLOTH_COMPILE_DISABLE"] = "1"
os.environ["TORCH_COMPILE_DISABLE"] = "1"  # fully disable torch.compile

DATA_DIR = Path(__file__).parent / "data"
V10_FILE = DATA_DIR / "labeled_pairs_v10.csv"
OUTPUT_DIR = Path(__file__).parent / "output" / "qwen3-variant-classifier"
LOG_DIR = Path(__file__).parent / "logs"

# ── Prompt templates ─────────────────────────────────────────────────────

USER_TEMPLATE = """Listing A:
Title: {title_a}
Description: {desc_a}

Listing B:
Title: {title_b}
Description: {desc_b}"""

DESC_LIMIT = 300  # chars — truncate descriptions to reduce noise


def setup_logging():
    """Set up dual logging: console + timestamped log file."""
    LOG_DIR.mkdir(parents=True, exist_ok=True)
    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    log_file = LOG_DIR / f"finetune_{timestamp}.log"

    logger = logging.getLogger("finetune")
    logger.setLevel(logging.INFO)

    fmt = logging.Formatter("%(asctime)s | %(message)s", datefmt="%Y-%m-%d %H:%M:%S")

    fh = logging.FileHandler(log_file, encoding="utf-8")
    fh.setFormatter(fmt)
    logger.addHandler(fh)

    ch = logging.StreamHandler(sys.stdout)
    ch.setFormatter(fmt)
    logger.addHandler(ch)

    logger.info(f"Log file: {log_file}")
    return logger, log_file


log = None  # set in main()


def log_print(msg: str):
    """Print via logger if available, else plain print."""
    if log:
        log.info(msg)
    else:
        print(msg)


def build_chat_example(row: dict) -> dict:
    """Convert a CSV row into a chat-format training example."""
    title_a = row.get("anchor_title", "").strip()
    title_b = row.get("neighbor_title", "").strip()
    desc_a = row.get("anchor_desc", "").strip()[:DESC_LIMIT]
    desc_b = row.get("neighbor_desc", "").strip()[:DESC_LIMIT]
    label = int(row["label"])
    reasoning = row.get("reasoning", "").strip()

    verdict = "same" if label == 1 else "different"
    # Reason BEFORE verdict — unlocks chain-of-thought: model reasons then decides
    response = json.dumps({"reason": reasoning, "verdict": verdict}, ensure_ascii=False)

    user_msg = USER_TEMPLATE.format(
        title_a=title_a, desc_a=desc_a,
        title_b=title_b, desc_b=desc_b,
    )

    return {
        "messages": [
            {"role": "user", "content": user_msg},
            {"role": "assistant", "content": response},
        ]
    }


def load_dataset(max_samples: int = 0, eval_frac: float = 0.02):
    """Load v10 CSV and split into train/eval datasets."""
    log_print(f"Loading dataset from {V10_FILE}...")
    examples = []

    with open(V10_FILE, encoding="utf-8", errors="replace") as f:
        reader = csv.DictReader(f)
        for row in reader:
            # Skip rows with empty titles
            if not row.get("anchor_title") or not row.get("neighbor_title"):
                continue
            # Skip low-confidence labels
            if row.get("confidence") == "low":
                continue
            examples.append(build_chat_example(row))

    random.seed(42)
    random.shuffle(examples)

    if max_samples > 0:
        examples = examples[:max_samples]

    # Split train/eval
    eval_size = max(int(len(examples) * eval_frac), 100)
    eval_set = examples[:eval_size]
    train_set = examples[eval_size:]

    log_print(f"Total: {len(examples)} | Train: {len(train_set)} | Eval: {len(eval_set)}")
    return train_set, eval_set


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default="Qwen/Qwen3-4B", help="Base model")
    parser.add_argument("--max-samples", type=int, default=0, help="Limit dataset size (0=all)")
    parser.add_argument("--epochs", type=int, default=1, help="Training epochs")
    parser.add_argument("--batch-size", type=int, default=2, help="Per-device batch size")
    parser.add_argument("--grad-accum", type=int, default=8, help="Gradient accumulation steps")
    parser.add_argument("--lr", type=float, default=1e-4, help="Learning rate")
    parser.add_argument("--lora-r", type=int, default=16, help="LoRA rank")
    parser.add_argument("--lora-alpha", type=int, default=32, help="LoRA alpha")
    parser.add_argument("--max-seq-len", type=int, default=512, help="Max sequence length")
    parser.add_argument("--resume", action="store_true", help="Resume from checkpoint")
    parser.add_argument("--save-merged", action="store_true", help="Also save merged 4-bit model")
    args = parser.parse_args()

    # ── Logging ─────────────────────────────────────────────────────────
    global log
    log, log_file = setup_logging()
    log_print(f"Args: {vars(args)}")

    # ── Load data ─────────────────────────────────────────────────────────
    train_data, eval_data = load_dataset(args.max_samples)

    # ── Load model with Unsloth ───────────────────────────────────────────
    from unsloth import FastLanguageModel
    from unsloth.chat_templates import get_chat_template
    import torch

    log_print(f"Loading {args.model} with Unsloth...")
    model, tokenizer = FastLanguageModel.from_pretrained(
        model_name=args.model,
        max_seq_length=1024,  # model capacity — handles eval sequences > max_seq_len
        load_in_4bit=True,
        dtype=None,  # auto-detect
    )

    # Apply LoRA adapters
    model = FastLanguageModel.get_peft_model(
        model,
        r=args.lora_r,
        lora_alpha=args.lora_alpha,
        lora_dropout=0,
        target_modules=[
            "q_proj", "k_proj", "v_proj", "o_proj",
            "gate_proj", "up_proj", "down_proj",
        ],
        bias="none",
        use_gradient_checkpointing="unsloth",
        random_state=42,
    )

    # Set up chat template
    tokenizer = get_chat_template(tokenizer, chat_template="qwen-2.5")

    # ── Tokenize dataset ──────────────────────────────────────────────────
    from datasets import Dataset

    def format_example(example):
        text = tokenizer.apply_chat_template(
            example["messages"],
            tokenize=False,
            add_generation_prompt=False,
        )
        return {"text": text}

    train_ds = Dataset.from_list(train_data).map(format_example)
    eval_ds = Dataset.from_list(eval_data).map(format_example)

    # ── Training ──────────────────────────────────────────────────────────
    from trl import SFTTrainer
    from transformers import TrainingArguments, TrainerCallback

    class FileLogCallback(TrainerCallback):
        """Log training metrics to the log file at each logging step."""
        def __init__(self, start_time):
            self.start_time = start_time

        def on_log(self, args, state, control, logs=None, **kwargs):
            if logs is None:
                return
            elapsed = time.time() - self.start_time
            step = state.global_step
            total = state.max_steps
            pct = 100 * step / total if total else 0
            eta_s = (elapsed / max(step, 1)) * (total - step)
            eta_h = eta_s / 3600

            parts = [f"step {step}/{total} ({pct:.1f}%)"]
            if "loss" in logs:
                parts.append(f"loss={logs['loss']:.4f}")
            if "eval_loss" in logs:
                parts.append(f"eval_loss={logs['eval_loss']:.4f}")
            if "learning_rate" in logs:
                parts.append(f"lr={logs['learning_rate']:.2e}")
            parts.append(f"elapsed={elapsed/3600:.1f}h")
            parts.append(f"ETA={eta_h:.1f}h")
            log_print(" | ".join(parts))

    effective_batch = args.batch_size * args.grad_accum
    total_steps = len(train_ds) // effective_batch * args.epochs
    eval_steps = max(total_steps // 20, 100)  # eval ~20 times during training
    save_steps = max(total_steps // 10, 200)  # save ~10 checkpoints

    log_print(f"Training config:")
    log_print(f"  Effective batch size: {effective_batch}")
    log_print(f"  Total steps: {total_steps}")
    log_print(f"  Eval every: {eval_steps} steps")
    log_print(f"  Save every: {save_steps} steps")
    log_print(f"  Learning rate: {args.lr}")
    log_print(f"  LoRA rank: {args.lora_r}, alpha: {args.lora_alpha}")

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    training_start = time.time()

    trainer = SFTTrainer(
        model=model,
        tokenizer=tokenizer,
        train_dataset=train_ds,
        eval_dataset=eval_ds,
        args=TrainingArguments(
            output_dir=str(OUTPUT_DIR),
            num_train_epochs=args.epochs,
            per_device_train_batch_size=args.batch_size,
            gradient_accumulation_steps=args.grad_accum,
            learning_rate=args.lr,
            lr_scheduler_type="cosine",
            warmup_ratio=0.05,
            weight_decay=0.01,
            fp16=not torch.cuda.is_bf16_supported(),
            bf16=torch.cuda.is_bf16_supported(),
            logging_steps=10,
            eval_strategy="steps",
            eval_steps=eval_steps,
            save_strategy="steps",
            save_steps=save_steps,
            save_total_limit=3,
            load_best_model_at_end=True,
            metric_for_best_model="eval_loss",
            greater_is_better=False,
            report_to="none",
            seed=42,
        ),
        max_seq_length=args.max_seq_len,
        dataset_text_field="text",
        callbacks=[FileLogCallback(training_start)],
    )

    if args.resume:
        trainer.train(resume_from_checkpoint=True)
    else:
        trainer.train()

    # ── Save ──────────────────────────────────────────────────────────────
    log_print(f"Saving LoRA adapter to {OUTPUT_DIR}...")
    model.save_pretrained(str(OUTPUT_DIR))
    tokenizer.save_pretrained(str(OUTPUT_DIR))

    if args.save_merged:
        merged_dir = OUTPUT_DIR.parent / "qwen3-8b-variant-classifier-merged"
        log_print(f"Saving merged 4-bit model to {merged_dir}...")
        model.save_pretrained_merged(
            str(merged_dir),
            tokenizer,
            save_method="merged_4bit_forced",
        )

    # ── Final eval summary ────────────────────────────────────────────────
    metrics = trainer.evaluate()
    log_print(f"Final eval loss: {metrics['eval_loss']:.4f}")
    log_print(f"Model saved to: {OUTPUT_DIR}")
    log_print(f"To test the fine-tuned model:")
    log_print(f"  python eval_finetuned.py --adapter {OUTPUT_DIR} --n 50")


if __name__ == "__main__":
    main()
