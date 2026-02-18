"""
Evaluate fine-tuned evaluator model against held-out test set.

Loads the merged model and runs inference on test pairs,
comparing predictions against GPT ground truth.

Usage:
    py -3.12 -u eval.py
    py -3.12 -u eval.py --model-dir E:/Dev/ml-training/evaluator/v2/merged --tag v2
"""

import argparse
import csv
import json
import sys
import time
import os
from pathlib import Path

os.environ.setdefault("HF_HOME", "E:/DevCaches/huggingface")
os.environ.setdefault("TORCH_HOME", "E:/DevCaches/torch")
os.environ["XFORMERS_DISABLED"] = "1"
os.environ["TORCHDYNAMO_DISABLE"] = "1"

import torch
from sklearn.metrics import (
    accuracy_score, classification_report, confusion_matrix,
    precision_score, recall_score, f1_score,
)

DATA_DIR = Path(__file__).parent.parent / "data"
TEST_CSV = DATA_DIR / "evaluator_test.csv"
DEFAULT_MODEL_DIR = "E:/Dev/ml-training/evaluator/v1/merged"


def parse_args():
    parser = argparse.ArgumentParser(description="Evaluate evaluator model")
    parser.add_argument("--model-dir", type=str, default=DEFAULT_MODEL_DIR)
    parser.add_argument("--tag", type=str, default="v1",
                        help="Version tag for output CSV naming")
    parser.add_argument("--limit", type=int, default=None)
    return parser.parse_args()


def load_model(model_dir):
    """Load fine-tuned model for inference."""
    try:
        from unsloth import FastLanguageModel
        model, tokenizer = FastLanguageModel.from_pretrained(
            model_dir, max_seq_length=512,
            load_in_4bit=True, dtype=torch.bfloat16,
        )
        FastLanguageModel.for_inference(model)
    except ImportError:
        from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig
        bnb_config = BitsAndBytesConfig(
            load_in_4bit=True, bnb_4bit_compute_dtype=torch.bfloat16,
        )
        model = AutoModelForCausalLM.from_pretrained(
            model_dir, quantization_config=bnb_config,
            device_map="auto", torch_dtype=torch.bfloat16,
        )
        tokenizer = AutoTokenizer.from_pretrained(model_dir)
        model.eval()

    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token
    return model, tokenizer


def predict(model, tokenizer, row):
    """Run inference on a single pair and parse the JSON response."""
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

    messages = [{"role": "user", "content": user_msg}]
    text = tokenizer.apply_chat_template(
        messages, tokenize=False, add_generation_prompt=True,
        enable_thinking=False,
    )
    inputs = tokenizer(text, return_tensors="pt").to(model.device)

    with torch.inference_mode():
        outputs = model.generate(
            **inputs, max_new_tokens=64, temperature=0.1,
            do_sample=False, pad_token_id=tokenizer.pad_token_id,
        )

    generated = outputs[0][inputs["input_ids"].shape[1]:]
    response_text = tokenizer.decode(generated, skip_special_tokens=True).strip()

    try:
        parsed = json.loads(response_text)
        return parsed.get("verdict", "unknown"), parsed.get("error_type", ""), response_text
    except json.JSONDecodeError:
        if "misclassification" in response_text.lower():
            return "misclassification", "", response_text
        elif "correct" in response_text.lower():
            return "correct", "", response_text
        return "parse_error", "", response_text


def save_results(results, output_csv):
    """Write results CSV (called periodically and at end)."""
    output_csv.parent.mkdir(parents=True, exist_ok=True)
    with open(output_csv, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=results[0].keys())
        writer.writeheader()
        writer.writerows(results)


def print_metrics(predictions, ground_truth, elapsed, total):
    """Compute and print all metrics."""
    y_true = [1 if v == "correct" else 0 for v in ground_truth]
    y_pred = [1 if v == "correct" else 0 for v in predictions]
    valid_mask = [p in ("correct", "misclassification") for p in predictions]
    y_true_valid = [y for y, m in zip(y_true, valid_mask) if m]
    y_pred_valid = [y for y, m in zip(y_pred, valid_mask) if m]
    parse_errors = sum(1 for p in predictions if p not in ("correct", "misclassification"))

    print(f"\n{'='*60}", flush=True)
    print("RESULTS", flush=True)
    print(f"{'='*60}", flush=True)
    print(f"Total pairs:      {total}", flush=True)
    print(f"Valid predictions: {len(y_true_valid)}", flush=True)
    print(f"Parse errors:     {parse_errors}", flush=True)
    print(f"Speed:            {elapsed/total*1000:.0f}ms/pair", flush=True)
    print(flush=True)

    if not y_true_valid:
        print("No valid predictions to evaluate!", flush=True)
        return

    print(f"Overall accuracy: {accuracy_score(y_true_valid, y_pred_valid):.1%}", flush=True)
    print(flush=True)
    print("Classification report (0=misclassification, 1=correct):", flush=True)
    print(classification_report(y_true_valid, y_pred_valid,
                                target_names=["misclassification", "correct"]), flush=True)
    print("Confusion matrix:", flush=True)
    print(confusion_matrix(y_true_valid, y_pred_valid), flush=True)

    misclass_precision = precision_score(y_true_valid, y_pred_valid, pos_label=0)
    misclass_recall = recall_score(y_true_valid, y_pred_valid, pos_label=0)
    misclass_f1 = f1_score(y_true_valid, y_pred_valid, pos_label=0)
    print(f"\nMisclassification detection:", flush=True)
    print(f"  Precision: {misclass_precision:.3f} (of flagged, how many truly wrong)", flush=True)
    print(f"  Recall:    {misclass_recall:.3f} (of truly wrong, how many caught)", flush=True)
    print(f"  F1:        {misclass_f1:.3f}", flush=True)


def main():
    args = parse_args()
    output_csv = DATA_DIR / "benchmarks" / f"evaluator_{args.tag}_results.csv"

    with open(TEST_CSV, newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    if args.limit:
        rows = rows[:args.limit]

    print(f"Evaluating {len(rows)} test pairs", flush=True)
    print(f"Model: {args.model_dir}", flush=True)
    print(f"Output: {output_csv}", flush=True)

    model, tokenizer = load_model(args.model_dir)

    predictions = []
    ground_truth = []
    results = []

    start = time.time()
    for i, row in enumerate(rows):
        pred_verdict, pred_error_type, raw_response = predict(model, tokenizer, row)
        true_verdict = row["verdict"]

        predictions.append(pred_verdict)
        ground_truth.append(true_verdict)
        results.append({
            **row,
            "pred_verdict": pred_verdict,
            "pred_error_type": pred_error_type,
            "raw_response": raw_response[:500],
        })

        if (i + 1) % 10 == 0:
            elapsed = time.time() - start
            print(f"  {i+1}/{len(rows)} ({elapsed:.0f}s, "
                  f"{elapsed/(i+1)*1000:.0f}ms/pair)", flush=True)

        # Periodic save every 50 pairs
        if (i + 1) % 50 == 0:
            save_results(results, output_csv)

    elapsed = time.time() - start
    print(f"\nInference complete: {elapsed:.1f}s ({elapsed/len(rows)*1000:.0f}ms/pair)",
          flush=True)

    print_metrics(predictions, ground_truth, elapsed, len(rows))

    save_results(results, output_csv)
    print(f"\nFull results saved to {output_csv}", flush=True)


if __name__ == "__main__":
    main()
