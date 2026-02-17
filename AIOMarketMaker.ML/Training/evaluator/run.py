"""
Run the fine-tuned evaluator on production comparable pairs.

Queries ListingRelationships for comparable pairs (optionally filtered
by confidence threshold), runs evaluator inference, and outputs
flagged misclassifications for cross-encoder retraining.

Usage:
    py -3.12 run.py                           # all comparable pairs
    py -3.12 run.py --max-confidence 0.80      # only low-confidence pairs
    py -3.12 run.py --job-id 42                # specific scrape job only
    py -3.12 run.py --limit 100 --dry-run      # preview
"""

import argparse
import csv
import json
import os
import sys
import time
from pathlib import Path

os.environ.setdefault("HF_HOME", "E:/DevCaches/huggingface")
os.environ.setdefault("TORCH_HOME", "E:/DevCaches/torch")

import pyodbc
import torch

DB_CONN = (
    "DRIVER={ODBC Driver 17 for SQL Server};"
    "SERVER=(localdb)\\MSSQLLocalDB;"
    "DATABASE=AIOMarketMaker;"
    "Trusted_Connection=yes;"
)

DATA_DIR = Path(__file__).parent.parent / "data"
DEFAULT_MODEL_DIR = "E:/Dev/ml-training/evaluator/v1/merged"
OUTPUT_CSV = DATA_DIR / "evaluator_corrections.csv"
DESC_LIMIT = 500


def parse_args():
    parser = argparse.ArgumentParser(description="Run evaluator on production data")
    parser.add_argument("--model-dir", type=str, default=DEFAULT_MODEL_DIR)
    parser.add_argument("--max-confidence", type=float, default=None,
                        help="Only evaluate pairs below this confidence")
    parser.add_argument("--job-id", type=int, default=None,
                        help="Only evaluate pairs from this scrape job")
    parser.add_argument("--limit", type=int, default=None)
    parser.add_argument("--dry-run", action="store_true")
    return parser.parse_args()


def fetch_pairs(args):
    """Query comparable pairs from production database."""
    conn = pyodbc.connect(DB_CONN)

    where_clauses = ["lr.IsComparable = 1"]
    if args.max_confidence:
        where_clauses.append(f"lr.SimilarityScore <= {args.max_confidence}")
    if args.job_id:
        where_clauses.append(f"a.ScrapeJobId = {args.job_id}")

    where = " AND ".join(where_clauses)
    limit = f"TOP {args.limit}" if args.limit else ""

    sql = f"""
    SELECT {limit}
        lr.ListingIdA, lr.ListingIdB,
        lr.IsComparable, lr.SimilarityScore,
        a.Title AS TitleA,
        REPLACE(REPLACE(LEFT(ISNULL(a.Description,''), {DESC_LIMIT}), CHAR(10), ' '), CHAR(13), ' ') AS DescA,
        b.Title AS TitleB,
        REPLACE(REPLACE(LEFT(ISNULL(b.Description,''), {DESC_LIMIT}), CHAR(10), ' '), CHAR(13), ' ') AS DescB,
        sj.SearchTerm
    FROM ListingRelationships lr
    INNER JOIN Listings a ON a.Id = lr.ListingIdA
    INNER JOIN Listings b ON b.Id = lr.ListingIdB
    INNER JOIN ScrapeJobs sj ON sj.Id = a.ScrapeJobId
    WHERE {where}
    ORDER BY lr.SimilarityScore ASC
    """

    cursor = conn.cursor()
    cursor.execute(sql)
    rows = cursor.fetchall()
    columns = [col[0] for col in cursor.description]
    conn.close()

    return [dict(zip(columns, row)) for row in rows]


def load_model(model_dir):
    """Load fine-tuned evaluator model."""
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


def predict(model, tokenizer, pair):
    """Run evaluator inference on a single pair."""
    onnx_label = 1 if pair["IsComparable"] else 0
    label_text = "COMPARABLE" if onnx_label == 1 else "NOT COMPARABLE"
    confidence = pair["SimilarityScore"]

    user_msg = (
        f"The classifier labeled this pair as {label_text} "
        f"(confidence: {confidence:.2f}).\n\n"
        f"LISTING A:\nTitle: {pair['TitleA']}\n"
        f"Description: {pair['DescA'][:500]}\n\n"
        f"LISTING B:\nTitle: {pair['TitleB']}\n"
        f"Description: {pair['DescB'][:500]}\n\n"
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

    generated = outputs[0][inputs["input_ids"].shape[1]:]
    response_text = tokenizer.decode(generated, skip_special_tokens=True).strip()

    try:
        parsed = json.loads(response_text)
        return parsed.get("verdict", "unknown"), parsed.get("error_type", ""), \
               parsed.get("reasoning", ""), int(parsed.get("correct_label", onnx_label))
    except json.JSONDecodeError:
        if "misclassification" in response_text.lower():
            return "misclassification", "", response_text[:200], 1 - onnx_label
        return "parse_error", "", response_text[:200], onnx_label


def main():
    args = parse_args()

    print("Fetching production pairs...")
    pairs = fetch_pairs(args)
    print(f"Found {len(pairs)} pairs to evaluate")

    if not pairs:
        print("No pairs found. Exiting.")
        return

    if args.dry_run:
        print(f"\n[DRY RUN] Would evaluate {len(pairs)} pairs. Sample:")
        for p in pairs[:5]:
            print(f"  ({p['ListingIdA']}, {p['ListingIdB']}) "
                  f"score={p['SimilarityScore']:.2f} — {p['SearchTerm']}")
        return

    print(f"\nLoading model from {args.model_dir}...")
    model, tokenizer = load_model(args.model_dir)

    # Run inference
    corrections = []
    total_correct = 0
    total_misclass = 0

    start = time.time()
    for i, pair in enumerate(pairs):
        verdict, error_type, reasoning, correct_label = predict(model, tokenizer, pair)

        if verdict == "misclassification":
            total_misclass += 1
            corrections.append({
                "listing_id_a": pair["ListingIdA"],
                "listing_id_b": pair["ListingIdB"],
                "onnx_label": 1 if pair["IsComparable"] else 0,
                "corrected_label": correct_label,
                "similarity_score": pair["SimilarityScore"],
                "error_type": error_type,
                "reasoning": reasoning,
                "search_term": pair["SearchTerm"],
                "title_a": pair["TitleA"],
                "title_b": pair["TitleB"],
            })
        else:
            total_correct += 1

        if (i + 1) % 100 == 0:
            elapsed = time.time() - start
            print(f"  {i+1}/{len(pairs)} — {total_misclass} flagged "
                  f"({elapsed/(i+1)*1000:.0f}ms/pair)")

    elapsed = time.time() - start
    print(f"\nComplete: {elapsed:.1f}s ({elapsed/len(pairs)*1000:.0f}ms/pair)")
    print(f"  Correct:          {total_correct}")
    print(f"  Misclassification: {total_misclass} ({total_misclass/len(pairs):.1%})")

    if corrections:
        OUTPUT_CSV.parent.mkdir(parents=True, exist_ok=True)
        with open(OUTPUT_CSV, "w", newline="", encoding="utf-8") as f:
            writer = csv.DictWriter(f, fieldnames=corrections[0].keys())
            writer.writeheader()
            writer.writerows(corrections)
        print(f"\n{len(corrections)} corrections saved to {OUTPUT_CSV}")
        print("These can be merged into the cross-encoder training dataset.")

        # Error type breakdown
        from collections import Counter
        et_counts = Counter(c["error_type"] for c in corrections)
        print("\nError type breakdown:")
        for et, count in et_counts.most_common():
            print(f"  {et or 'unknown'}: {count}")
    else:
        print("\nNo misclassifications found.")


if __name__ == "__main__":
    main()
