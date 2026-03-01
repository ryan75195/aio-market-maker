"""
Quick experiment: Test Qwen3-8B on the variant classification task.

1. Zero-shot (no fine-tuning) with the v9 eval prompt
2. Evaluate against human-labeled benchmark pairs
3. Report accuracy, speed, and failure modes

Usage:
    python experiment_local_llm.py
    python experiment_local_llm.py --model Qwen/Qwen3-8B   # default
    python experiment_local_llm.py --model Qwen/Qwen3-4B    # smaller/faster
    python experiment_local_llm.py --n 50                     # more test pairs
"""

import argparse
import csv
import json
import time
from pathlib import Path

import torch
from transformers import AutoModelForCausalLM, AutoTokenizer

DATA_DIR = Path(__file__).parent / "data"
BENCHMARK_FILE = DATA_DIR / "benchmarks" / "benchmark_gpt5mini_results.csv"
AUDIT_FILE = DATA_DIR / "evaluator_audit_gpt.csv"

SYSTEM_PROMPT = """You are classifying whether two eBay listings are the same product variant for pricing comparison.

COMPARABLE: identical functional specs (model, size, storage, generation), similar condition, similar completeness. Color and trivial accessories (cables, charger) are OK to differ.

NOT COMPARABLE if ANY of:
- Different specs (storage, CPU, screen size, model number)
- Accessory vs full product (disc drive vs console, pencil vs iPad)
- Working vs broken/for-parts
- New vs Used condition category
- Bundle with expensive accessories vs bare product
- Multi-item lot vs single item

For product identity, trust the TITLE over the description — sellers often use template descriptions.

Respond with JSON: {"verdict": "same" or "different", "reason": "<one sentence>"}"""

USER_TEMPLATE = """Listing A:
Title: {title_a}
Description: {desc_a}

Listing B:
Title: {title_b}
Description: {desc_b}"""


def load_benchmark_pairs(n: int):
    """Load n diverse pairs from the audit file (has titles, descs, and human-corrected labels)."""
    # Load all, then sample evenly across error types for diversity
    all_pairs = []
    with open(AUDIT_FILE, encoding="utf-8") as f:
        reader = csv.DictReader(f)
        for row in reader:
            all_pairs.append(row)

    # Mix of misclassifications and correct ones for a balanced test
    import random
    random.seed(42)
    misclass = [p for p in all_pairs if p.get("verdict") == "misclassification"]
    correct = [p for p in all_pairs if p.get("verdict") != "misclassification"]
    # Take half from each
    half = n // 2
    sample = random.sample(misclass, min(half, len(misclass))) + random.sample(correct, min(half, len(correct)))
    random.shuffle(sample)
    return sample[:n]


def load_v10_sample(n: int):
    """Fallback: load from v10 labeled pairs if benchmark is small."""
    pairs = []
    v10 = DATA_DIR / "labeled_pairs_v10.csv"
    with open(v10, encoding="utf-8") as f:
        reader = csv.DictReader(f)
        for i, row in enumerate(reader):
            if i % 100 == 0:  # sample every 100th pair for diversity
                pairs.append({
                    "anchor_title": row.get("anchor_title", ""),
                    "neighbor_title": row.get("neighbor_title", ""),
                    "anchor_desc": row.get("anchor_desc", "")[:300],
                    "neighbor_desc": row.get("neighbor_desc", "")[:300],
                    "human_label": row.get("label", ""),
                    "product_name": row.get("product_name", ""),
                })
                if len(pairs) >= n:
                    break
    return pairs


def classify_pair(model, tokenizer, title_a, desc_a, title_b, desc_b):
    """Run a single classification through the model."""
    user_msg = USER_TEMPLATE.format(
        title_a=title_a,
        desc_a=desc_a[:300],
        title_b=title_b,
        desc_b=desc_b[:300],
    )

    messages = [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user", "content": user_msg},
    ]

    text = tokenizer.apply_chat_template(
        messages,
        tokenize=False,
        add_generation_prompt=True,
        enable_thinking=False,  # disable thinking mode for speed
    )

    inputs = tokenizer(text, return_tensors="pt").to(model.device)
    input_len = inputs["input_ids"].shape[1]

    with torch.no_grad():
        outputs = model.generate(
            **inputs,
            max_new_tokens=150,
            do_sample=False,
            temperature=1.0,
        )

    response_ids = outputs[0][input_len:]
    response = tokenizer.decode(response_ids, skip_special_tokens=True)

    # Parse JSON from response
    try:
        # Find JSON in response
        start = response.index("{")
        end = response.rindex("}") + 1
        parsed = json.loads(response[start:end])
        return parsed.get("verdict", "unknown"), parsed.get("reason", ""), response
    except (ValueError, json.JSONDecodeError):
        return "parse_error", response, response


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default="Qwen/Qwen3-8B", help="Model name")
    parser.add_argument("--n", type=int, default=20, help="Number of test pairs")
    parser.add_argument("--source", default="benchmark", choices=["benchmark", "v10"])
    args = parser.parse_args()

    print(f"Loading model: {args.model}")
    print(f"GPU: {torch.cuda.get_device_name(0)}, VRAM: {torch.cuda.get_device_properties(0).total_memory / 1e9:.1f}GB")

    t0 = time.time()
    tokenizer = AutoTokenizer.from_pretrained(args.model)
    # Use 4-bit quantization to fit in 16GB VRAM
    from transformers import BitsAndBytesConfig
    quant_config = BitsAndBytesConfig(
        load_in_4bit=True,
        bnb_4bit_compute_dtype=torch.bfloat16,
        bnb_4bit_quant_type="nf4",
    )
    model = AutoModelForCausalLM.from_pretrained(
        args.model,
        torch_dtype=torch.bfloat16,
        device_map="auto",
        quantization_config=quant_config,
    )
    load_time = time.time() - t0
    print(f"Model loaded in {load_time:.1f}s")

    # Load test pairs
    if args.source == "benchmark":
        pairs = load_benchmark_pairs(args.n)
        label_key = "human_label"
        title_a_key = "anchor_title" if "anchor_title" in pairs[0] else "product_name"
        # The benchmark file uses specific column names - check what we have
        print(f"Columns: {list(pairs[0].keys())}")
    else:
        pairs = load_v10_sample(args.n)
        label_key = "human_label"

    print(f"\nRunning {len(pairs)} pairs through {args.model}...")
    print("=" * 80)

    correct = 0
    total = 0
    parse_errors = 0
    times = []
    results = []

    for i, pair in enumerate(pairs):
        # Handle different CSV column names
        title_a = pair.get("title_a", pair.get("anchor_title", ""))
        title_b = pair.get("title_b", pair.get("neighbor_title", ""))
        desc_a = pair.get("desc_a", pair.get("anchor_desc", ""))
        desc_b = pair.get("desc_b", pair.get("neighbor_desc", ""))
        # Audit file uses correct_label, v10 uses label
        human_label = str(pair.get("correct_label", pair.get(label_key, pair.get("human_label", ""))))
        product = pair.get("search_term", pair.get("product_name", ""))

        t1 = time.time()
        verdict, reason, raw = classify_pair(model, tokenizer, title_a, desc_a, title_b, desc_b)
        elapsed = time.time() - t1
        times.append(elapsed)

        if verdict == "parse_error":
            parse_errors += 1
            status = "PARSE_ERR"
        else:
            # Map verdict to label: same=1, different=0
            model_label = 1 if verdict == "same" else 0
            is_correct = str(model_label) == str(human_label)
            if is_correct:
                correct += 1
            total += 1
            status = "OK" if is_correct else "WRONG"

        print(f"\n[{i+1}/{len(pairs)}] {product} | {status} | {elapsed:.1f}s")
        print(f"  Human: {'comparable' if str(human_label) == '1' else 'not comparable'}")
        print(f"  Model: {verdict} — {reason[:100]}")

        results.append({
            "product": product,
            "human_label": human_label,
            "model_verdict": verdict,
            "reason": reason,
            "correct": status == "OK",
            "time_s": elapsed,
        })

    # Summary
    print("\n" + "=" * 80)
    print("RESULTS SUMMARY")
    print("=" * 80)
    accuracy = correct / max(total, 1) * 100
    avg_time = sum(times) / max(len(times), 1)
    print(f"Accuracy: {correct}/{total} ({accuracy:.1f}%)")
    print(f"Parse errors: {parse_errors}/{len(pairs)}")
    print(f"Avg time per pair: {avg_time:.1f}s")
    print(f"Throughput: {3600/avg_time:.0f} pairs/hour")
    print(f"Daily capacity: {86400/avg_time:.0f} pairs/day")

    # Breakdown
    wrong = [r for r in results if not r["correct"] and r["model_verdict"] != "parse_error"]
    if wrong:
        print(f"\nWRONG predictions ({len(wrong)}):")
        for r in wrong:
            print(f"  {r['product']}: human={'comparable' if r['human_label'] == '1' else 'not comparable'}, model={r['model_verdict']}")
            print(f"    Reason: {r['reason'][:120]}")

    # Save results
    out_path = DATA_DIR / "experiment_local_llm_results.json"
    with open(out_path, "w") as f:
        json.dump({
            "model": args.model,
            "n_pairs": len(pairs),
            "accuracy": accuracy,
            "correct": correct,
            "total": total,
            "parse_errors": parse_errors,
            "avg_time_s": avg_time,
            "pairs_per_hour": 3600 / avg_time,
            "results": results,
        }, f, indent=2)
    print(f"\nResults saved to {out_path}")


if __name__ == "__main__":
    main()
