"""
Evaluate the fine-tuned Qwen3 LoRA model on the audit benchmark pairs.
Compares against the zero-shot baseline (55% accuracy).

Usage:
    python eval_finetuned.py
    python eval_finetuned.py --adapter output/qwen3-variant-classifier
    python eval_finetuned.py --n 50
"""

import argparse
import csv
import json
import os
import random
import time
from pathlib import Path

os.environ["TORCHINDUCTOR_CACHE_DIR"] = "C:/tmp/ti"
os.environ["TRITON_CACHE_DIR"] = "C:/tmp/triton"
os.environ["UNSLOTH_COMPILE_DISABLE"] = "1"
os.environ["TORCH_COMPILE_DISABLE"] = "1"

csv.field_size_limit(10_000_000)

DATA_DIR = Path(__file__).parent / "data"
AUDIT_FILE = DATA_DIR / "evaluator_audit_gpt.csv"

USER_TEMPLATE = """Listing A:
Title: {title_a}
Description: {desc_a}

Listing B:
Title: {title_b}
Description: {desc_b}"""


def load_test_pairs(n: int):
    """Load balanced test pairs from audit file."""
    all_pairs = []
    with open(AUDIT_FILE, encoding="utf-8", errors="replace") as f:
        reader = csv.DictReader(f)
        for row in reader:
            all_pairs.append(row)

    random.seed(42)
    misclass = [p for p in all_pairs if p.get("verdict") == "misclassification"]
    correct_preds = [p for p in all_pairs if p.get("verdict") != "misclassification"]
    half = n // 2
    sample = random.sample(misclass, min(half, len(misclass))) + random.sample(correct_preds, min(half, len(correct_preds)))
    random.shuffle(sample)
    return sample[:n]


def classify(model, tokenizer, title_a, desc_a, title_b, desc_b, system_prompt=None):
    import torch
    user_msg = USER_TEMPLATE.format(
        title_a=title_a, desc_a=desc_a[:300],
        title_b=title_b, desc_b=desc_b[:300],
    )
    messages = []
    if system_prompt:
        messages.append({"role": "system", "content": system_prompt})
    messages.append({"role": "user", "content": user_msg})
    text = tokenizer.apply_chat_template(
        messages, tokenize=False, add_generation_prompt=True,
    )
    inputs = tokenizer(text, return_tensors="pt").to(model.device)
    input_len = inputs["input_ids"].shape[1]

    with torch.no_grad():
        outputs = model.generate(
            **inputs, max_new_tokens=150, do_sample=False, temperature=1.0,
        )
    response = tokenizer.decode(outputs[0][input_len:], skip_special_tokens=True)

    try:
        start = response.index("{")
        end = response.rindex("}") + 1
        parsed = json.loads(response[start:end])
        return parsed.get("verdict", "unknown"), parsed.get("reason", ""), response
    except (ValueError, json.JSONDecodeError):
        return "parse_error", response, response


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--adapter", default="output/qwen3-variant-classifier", help="LoRA adapter path")
    parser.add_argument("--n", type=int, default=20, help="Number of test pairs")
    parser.add_argument("--system-prompt", type=str, default=None,
                        help="Optional system prompt to inject at inference time")
    args = parser.parse_args()

    adapter_path = Path(__file__).parent / args.adapter

    print(f"Loading base model + LoRA adapter from {adapter_path}")

    from unsloth import FastLanguageModel
    import torch

    model, tokenizer = FastLanguageModel.from_pretrained(
        model_name=str(adapter_path),
        max_seq_length=1024,
        load_in_4bit=True,
        dtype=None,
    )
    FastLanguageModel.for_inference(model)

    pairs = load_test_pairs(args.n)
    print(f"\nEvaluating {len(pairs)} pairs...")
    print("=" * 80)

    correct = 0
    total = 0
    parse_errors = 0
    times = []

    for i, pair in enumerate(pairs):
        title_a = pair.get("title_a", "")
        title_b = pair.get("title_b", "")
        desc_a = pair.get("desc_a", "")
        desc_b = pair.get("desc_b", "")
        human_label = str(pair.get("correct_label", ""))
        product = pair.get("search_term", "")

        t1 = time.time()
        verdict, reason, raw = classify(model, tokenizer, title_a, desc_a, title_b, desc_b,
                                        system_prompt=args.system_prompt)
        elapsed = time.time() - t1
        times.append(elapsed)

        if verdict == "parse_error":
            parse_errors += 1
            status = "PARSE_ERR"
        else:
            model_label = 1 if verdict == "same" else 0
            is_correct = str(model_label) == str(human_label)
            if is_correct:
                correct += 1
            total += 1
            status = "OK" if is_correct else "WRONG"

        print(f"[{i+1}/{len(pairs)}] {product} | {status} | {elapsed:.1f}s")
        print(f"  Human: {'comparable' if str(human_label) == '1' else 'not comparable'}")
        print(f"  Model: {verdict} — {reason[:100]}")

    print("\n" + "=" * 80)
    accuracy = correct / max(total, 1) * 100
    avg_time = sum(times) / max(len(times), 1)
    print(f"FINE-TUNED ACCURACY: {correct}/{total} ({accuracy:.1f}%)")
    print(f"Zero-shot baseline: 11/20 (55.0%)")
    print(f"Parse errors: {parse_errors}")
    print(f"Avg time/pair: {avg_time:.1f}s")
    print(f"Throughput: {3600/avg_time:.0f} pairs/hour")


if __name__ == "__main__":
    main()
