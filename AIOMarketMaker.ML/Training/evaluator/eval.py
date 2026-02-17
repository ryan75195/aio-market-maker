"""
Evaluate fine-tuned evaluator model against held-out test set.

Loads the merged model and runs inference on test pairs,
comparing predictions against GPT ground truth.

Usage:
    py -3.12 eval.py
    py -3.12 eval.py --model-dir E:/Dev/ml-training/evaluator/v2/merged
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
# RTX 5070 Ti (SM120/Blackwell) is too new for xformers — use PyTorch SDPA instead
os.environ["XFORMERS_DISABLED"] = "1"
# Windows path length limit breaks Triton compilation — disable torch.compile
os.environ["TORCHDYNAMO_DISABLE"] = "1"

import torch
from sklearn.metrics import (
    accuracy_score, classification_report, confusion_matrix,
    precision_score, recall_score, f1_score,
)

DATA_DIR = Path(__file__).parent.parent / "data"
TEST_CSV = DATA_DIR / "evaluator_test.csv"
DEFAULT_MODEL_DIR = "E:/Dev/ml-training/evaluator/v1/merged"
OUTPUT_CSV = DATA_DIR / "benchmarks" / "evaluator_v1_results.csv"


def parse_args():
    parser = argparse.ArgumentParser(description="Evaluate evaluator model")
    parser.add_argument("--model-dir", type=str, default=DEFAULT_MODEL_DIR)
    parser.add_argument("--limit", type=int, default=None)
    return parser.parse_args()


def load_model(model_dir):
    """Load fine-tuned model for inference."""
    try:
        from unsloth import FastLanguageModel
        model, tokenizer = FastLanguageModel.from_pretrained(
            model_dir,
            max_seq_length=512,
            load_in_4bit=True,
            dtype=torch.bfloat16,
        )
        FastLanguageModel.for_inference(model)
    except ImportError:
        from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig
        bnb_config = BitsAndBytesConfig(
            load_in_4bit=True,
            bnb_4bit_compute_dtype=torch.bfloat16,
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
        messages, tokenize=False, add_generation_prompt=True
    )
    inputs = tokenizer(text, return_tensors="pt").to(model.device)

    with torch.no_grad():
        outputs = model.generate(
            **inputs, max_new_tokens=256, temperature=0.1,
            do_sample=False, pad_token_id=tokenizer.pad_token_id,
        )

    # Decode only the generated tokens (not the prompt)
    generated = outputs[0][inputs["input_ids"].shape[1]:]
    response_text = tokenizer.decode(generated, skip_special_tokens=True).strip()

    # Parse JSON response
    try:
        parsed = json.loads(response_text)
        return parsed.get("verdict", "unknown"), parsed.get("error_type", ""), response_text
    except json.JSONDecodeError:
        # Try to extract verdict from text
        if "misclassification" in response_text.lower():
            return "misclassification", "", response_text
        elif "correct" in response_text.lower():
            return "correct", "", response_text
        return "parse_error", "", response_text


def main():
    args = parse_args()

    # Load test data
    with open(TEST_CSV, newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    if args.limit:
        rows = rows[:args.limit]

    print(f"Evaluating {len(rows)} test pairs")
    print(f"Model: {args.model_dir}")

    # Load model
    model, tokenizer = load_model(args.model_dir)

    # Run inference
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

        if (i + 1) % 50 == 0:
            elapsed = time.time() - start
            print(f"  {i+1}/{len(rows)} ({elapsed:.0f}s, "
                  f"{elapsed/(i+1)*1000:.0f}ms/pair)")

    elapsed = time.time() - start
    print(f"\nInference complete: {elapsed:.1f}s ({elapsed/len(rows)*1000:.0f}ms/pair)")

    # Metrics
    # Binary: correct=1, misclassification=0
    y_true = [1 if v == "correct" else 0 for v in ground_truth]
    y_pred = [1 if v == "correct" else 0 for v in predictions]
    valid_mask = [p in ("correct", "misclassification") for p in predictions]
    y_true_valid = [y for y, m in zip(y_true, valid_mask) if m]
    y_pred_valid = [y for y, m in zip(y_pred, valid_mask) if m]
    parse_errors = sum(1 for p in predictions if p not in ("correct", "misclassification"))

    print(f"\n{'='*60}")
    print("RESULTS")
    print(f"{'='*60}")
    print(f"Total pairs:    {len(rows)}")
    print(f"Valid predictions: {len(y_true_valid)}")
    print(f"Parse errors:   {parse_errors}")
    print(f"Speed:          {elapsed/len(rows)*1000:.0f}ms/pair")
    print()
    print(f"Overall accuracy: {accuracy_score(y_true_valid, y_pred_valid):.1%}")
    print()
    print("Classification report (0=misclassification, 1=correct):")
    print(classification_report(y_true_valid, y_pred_valid,
                                target_names=["misclassification", "correct"]))
    print("Confusion matrix:")
    print(confusion_matrix(y_true_valid, y_pred_valid))

    # Misclassification-specific metrics (the important ones)
    misclass_precision = precision_score(y_true_valid, y_pred_valid, pos_label=0)
    misclass_recall = recall_score(y_true_valid, y_pred_valid, pos_label=0)
    misclass_f1 = f1_score(y_true_valid, y_pred_valid, pos_label=0)
    print(f"\nMisclassification detection:")
    print(f"  Precision: {misclass_precision:.3f} (of flagged pairs, how many were truly wrong)")
    print(f"  Recall:    {misclass_recall:.3f} (of truly wrong pairs, how many were caught)")
    print(f"  F1:        {misclass_f1:.3f}")

    # Save results
    OUTPUT_CSV.parent.mkdir(parents=True, exist_ok=True)
    with open(OUTPUT_CSV, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=results[0].keys())
        writer.writeheader()
        writer.writerows(results)
    print(f"\nFull results saved to {OUTPUT_CSV}")


if __name__ == "__main__":
    main()
