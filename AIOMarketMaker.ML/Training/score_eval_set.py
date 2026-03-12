"""
Score the local LLM extraction against GPT-5-nano ground truth.

Loads the eval set, runs the local model on each title, and compares
axis-by-axis against the expected labels.

Usage:
    python score_eval_set.py
    python score_eval_set.py --model unsloth/qwen3-4b-unsloth-bnb-4bit
    python score_eval_set.py --finetuned           # evaluate fine-tuned model
    python score_eval_set.py --n 50  # score first 50 only
"""

import argparse
import json
import re
import time
from collections import defaultdict
from pathlib import Path

from experiment_topdown_taxonomy import (
    load_local_model, extract_axes_local, OUTPUT_DIR,
)

EVAL_DIR = OUTPUT_DIR / "eval"
MODEL_DIR = OUTPUT_DIR / "models" / "qwen3-4b-extraction"


def load_eval_set():
    eval_path = EVAL_DIR / "eval_set.json"
    with open(eval_path) as f:
        return json.load(f)


def load_skeleton(job_id):
    skeleton_path = EVAL_DIR / f"skeleton_{job_id}.json"
    with open(skeleton_path) as f:
        return json.load(f)


def has_title_evidence(value, title):
    """Check if there's textual evidence for an extracted value in the title.

    Handles common patterns:
      - "size9" matches "Size 9", "size 9", "size9"
      - "gen4" matches "Gen 4", "gen 4", "gen4"
      - "2-pack" matches "2Pack", "2-Pack", "Pack of 2", "2 Pack"
      - "console_only" matches "Console Only"
      - "usb_2_0" matches "USB"
      - "genuine" matches "Genuine", "Original"
      - "xl" matches " XL ", "XL "
    """
    title_lower = title.lower()
    val_lower = value.lower().strip()

    # Direct substring match
    if val_lower in title_lower:
        return True

    # Replace underscores with spaces: "console_only" -> "console only"
    spaced = val_lower.replace("_", " ")
    if spaced in title_lower:
        return True

    # Split on underscores and check if all parts appear
    parts = val_lower.replace("-", "_").split("_")
    parts = [p for p in parts if len(p) > 1]  # skip single chars
    if parts and all(p in title_lower for p in parts):
        return True

    # Handle number-letter compounds: "size9" -> "size 9", "gen4" -> "gen 4"
    import re
    expanded = re.sub(r'([a-z])(\d)', r'\1 \2', val_lower)
    if expanded != val_lower and expanded in title_lower:
        return True

    # Handle "2-pack" style: check for digit + "pack"
    pack_match = re.match(r'(\d+)[_-]?pack', val_lower)
    if pack_match:
        n = pack_match.group(1)
        if (f"{n}pack" in title_lower or f"{n} pack" in title_lower
                or f"pack of {n}" in title_lower):
            return True

    # Special: "genuine" also matches "original"
    if val_lower == "genuine" and "original" in title_lower:
        return True

    # Special: short values like "xl", "4k" need word boundary matching
    if len(val_lower) <= 3:
        pattern = r'(?<![a-z])' + re.escape(val_lower) + r'(?![a-z])'
        if re.search(pattern, title_lower):
            return True

    return False


def score_extraction(expected, actual, skeleton_axes):
    """Compare expected vs actual extraction, return per-axis scores.

    Uses skeleton_axes as the complete set of axes to evaluate,
    so axes that are null in BOTH expected and actual count as
    true negatives (previously missed).

    Returns dict with:
        - true_positive: both agree on a non-null value (and values match)
        - true_negative: both agree it's null/absent
        - false_positive: actual has value, expected is null (hallucination)
        - false_negative: expected has value, actual is null (missed)
        - wrong_value: both non-null but different values
    """
    # Use skeleton axes as the complete key set (not just union of dicts)
    all_keys = set(skeleton_axes)
    # Also include any extra keys the model invented outside the skeleton
    all_keys |= set(actual.keys())

    scores = {
        "true_positive": 0,
        "true_negative": 0,
        "false_positive": 0,
        "false_negative": 0,
        "wrong_value": 0,
    }
    details = []

    for key in sorted(all_keys):
        exp_val = expected.get(key)
        act_val = actual.get(key)

        if exp_val and act_val:
            if exp_val.lower().strip() == act_val.lower().strip():
                scores["true_positive"] += 1
                details.append((key, "TP", exp_val, act_val))
            else:
                scores["wrong_value"] += 1
                details.append((key, "WRONG", exp_val, act_val))
        elif not exp_val and not act_val:
            scores["true_negative"] += 1
            details.append((key, "TN", None, None))
        elif not exp_val and act_val:
            scores["false_positive"] += 1
            details.append((key, "FP", None, act_val))
        elif exp_val and not act_val:
            scores["false_negative"] += 1
            details.append((key, "FN", exp_val, None))

    return scores, details


# ── Fine-tuned model support ────────────────────────────────────────────────

def load_finetuned_model():
    """Load the fine-tuned model with LoRA adapter."""
    from unsloth import FastLanguageModel

    adapter_path = MODEL_DIR / "lora_adapter"
    if not adapter_path.exists():
        raise FileNotFoundError(
            f"No fine-tuned model at {adapter_path}. "
            f"Run finetune_extraction.py first."
        )

    print(f"Loading fine-tuned model from {adapter_path}...")
    model, tokenizer = FastLanguageModel.from_pretrained(
        model_name=str(adapter_path),
        max_seq_length=1024,
        load_in_4bit=True,
        dtype=None,
    )
    FastLanguageModel.for_inference(model)
    return model, tokenizer


def extract_axes_finetuned(model, tokenizer, title, skeleton):
    """Extract axes using the fine-tuned model with the training prompt format.

    Uses the same prompt format as the training data, so the model
    sees the exact input it was trained on.
    """
    import torch
    from generate_training_data import (
        EXTRACTION_SYSTEM, EXTRACTION_USER_TEMPLATE,
        format_axes_description,
    )

    axes_desc = format_axes_description(skeleton)
    user_msg = EXTRACTION_USER_TEMPLATE.format(
        axes_desc=axes_desc, title=title
    )

    messages = [
        {"role": "system", "content": EXTRACTION_SYSTEM},
        {"role": "user", "content": user_msg},
    ]

    text = tokenizer.apply_chat_template(
        messages,
        tokenize=False,
        add_generation_prompt=True,
    )

    inputs = tokenizer(text, return_tensors="pt").to(model.device)
    input_len = inputs["input_ids"].shape[1]

    with torch.no_grad():
        outputs = model.generate(
            **inputs,
            max_new_tokens=300,
            do_sample=False,
        )

    response = tokenizer.decode(
        outputs[0][input_len:], skip_special_tokens=True
    )

    try:
        start = response.index("{")
        end = response.rindex("}") + 1
        raw = json.loads(response[start:end])

        # Clean: normalize values, handle nulls
        cleaned = {}
        for k, v in raw.items():
            if v is None or str(v).strip().lower() in (
                "null", "none", "n/a", ""
            ):
                continue
            cleaned[k] = str(v).strip().lower()

        return cleaned if cleaned else None
    except (ValueError, json.JSONDecodeError):
        return None


def main():
    parser = argparse.ArgumentParser(
        description="Score local LLM against eval set"
    )
    parser.add_argument(
        "--model", default="unsloth/qwen3-4b-unsloth-bnb-4bit"
    )
    parser.add_argument(
        "--finetuned", action="store_true",
        help="Use the fine-tuned model instead of base"
    )
    parser.add_argument(
        "--n", type=int, default=0, help="Limit samples (0 = all)"
    )
    parser.add_argument(
        "--verbose", action="store_true", help="Show per-title details"
    )
    parser.add_argument(
        "--validate", action="store_true",
        help="Filter hallucinations using title evidence checking"
    )
    parser.add_argument(
        "--rescore", action="store_true",
        help="Re-score saved results with evidence checking (no model needed)"
    )
    args = parser.parse_args()

    # ── Rescore mode: analyze saved results without loading model ────────
    if args.rescore:
        rescore_results(args)
        return

    eval_data = load_eval_set()
    samples = eval_data["samples"]
    if args.n > 0:
        samples = samples[:args.n]

    # Load model
    if args.finetuned:
        model_label = "fine-tuned qwen3-4b"
        model, tokenizer = load_finetuned_model()
        extract_fn = extract_axes_finetuned
    else:
        model_label = args.model
        model, tokenizer = load_local_model(args.model)
        extract_fn = extract_axes_local

    print(f"Scoring {len(samples)} samples from {eval_data['jobs']} jobs")
    print(f"Model: {model_label}\n")

    # Group samples by job for skeleton loading
    by_job = defaultdict(list)
    for s in samples:
        by_job[s["job_id"]].append(s)

    # Score
    total_scores = defaultdict(int)
    per_axis_scores = defaultdict(lambda: defaultdict(int))
    per_job_scores = defaultdict(lambda: defaultdict(int))
    errors = []
    times = []

    for job_id, job_samples in by_job.items():
        skeleton = load_skeleton(job_id)
        skeleton_axes = [ax["name"] for ax in skeleton["axes"]]
        search_term = job_samples[0]["search_term"]
        print(
            f"\n--- {search_term} "
            f"(job {job_id}, {len(job_samples)} samples) ---"
        )

        for i, sample in enumerate(job_samples):
            t0 = time.time()
            actual = extract_fn(
                model, tokenizer, sample["title"], skeleton
            )
            elapsed = time.time() - t0
            times.append(elapsed)

            if actual is None:
                actual = {}

            # Apply title evidence validation if requested
            if args.validate:
                actual = {
                    k: v for k, v in actual.items()
                    if has_title_evidence(v, sample["title"])
                }

            expected = sample["expected"]
            scores, details = score_extraction(
                expected, actual, skeleton_axes
            )

            for k, v in scores.items():
                total_scores[k] += v
                per_job_scores[search_term][k] += v

            for key, result, exp, act in details:
                per_axis_scores[key][result] += 1

            # Track notable errors
            for key, result, exp, act in details:
                if result in ("FP", "WRONG"):
                    errors.append({
                        "title": sample["title"][:80],
                        "axis": key,
                        "type": result,
                        "expected": exp,
                        "actual": act,
                        "job": search_term,
                    })

            if args.verbose and any(
                r in ("FP", "WRONG", "FN") for _, r, _, _ in details
            ):
                safe_title = (
                    sample['title'][:70]
                    .encode('ascii', 'replace').decode()
                )
                print(f"  [{i+1}] {safe_title}")
                for key, result, exp, act in details:
                    if result not in ("TP", "TN"):
                        print(
                            f"       {result}: {key} "
                            f"— expected={exp}, got={act}"
                        )

            if (i + 1) % 10 == 0:
                avg_t = sum(times[-10:]) / min(10, len(times))
                print(
                    f"  {i+1}/{len(job_samples)} — {avg_t:.1f}s/title"
                )

    # ── Summary ──────────────────────────────────────────────────────────
    print_summary(
        total_scores, per_job_scores, errors,
        model_label, len(samples), len(by_job),
        avg_time=sum(times) / len(times),
    )

    # Save results
    suffix = "_finetuned" if args.finetuned else ""
    if args.validate:
        suffix += "_validated"
    results_path = EVAL_DIR / f"score_results{suffix}.json"
    with open(results_path, "w") as f:
        json.dump({
            "model": model_label,
            "finetuned": args.finetuned,
            "validated": args.validate,
            "samples": len(samples),
            "jobs": len(by_job),
            "avg_time_per_title": round(sum(times) / len(times), 2),
            "accuracy": round(
                (total_scores["true_positive"]
                 + total_scores["true_negative"])
                / sum(total_scores.values()) * 100, 2
            ),
            "precision": round(
                total_scores["true_positive"]
                / max(1, total_scores["true_positive"]
                      + total_scores["false_positive"]
                      + total_scores["wrong_value"]) * 100, 2
            ),
            "recall": round(
                total_scores["true_positive"]
                / max(1, total_scores["true_positive"]
                      + total_scores["false_negative"]
                      + total_scores["wrong_value"]) * 100, 2
            ),
            "hallucination_rate": round(
                total_scores["false_positive"]
                / max(1, sum(total_scores.values())) * 100, 2
            ),
            "total_scores": dict(total_scores),
            "per_job": {k: dict(v) for k, v in per_job_scores.items()},
            "errors": errors,
        }, f, indent=2)
    print(f"\nDetailed results saved to {results_path}")


def rescore_results(args):
    """Re-analyze saved results with evidence checking. No model needed."""
    suffix = "_finetuned" if args.finetuned else ""
    results_path = EVAL_DIR / f"score_results{suffix}.json"
    if not results_path.exists():
        print(f"No saved results at {results_path}")
        print("Run scoring first, then use --rescore to analyze.")
        return

    with open(results_path) as f:
        saved = json.load(f)

    errors = saved["errors"]
    original_scores = saved["total_scores"]

    print(f"Re-scoring {len(errors)} errors from {saved['model']}")
    print(f"Original: {saved['accuracy']:.1f}% accuracy, "
          f"{saved['hallucination_rate']:.1f}% hallucination rate\n")

    # Classify each FP as evidence-backed or true hallucination
    evidence_backed = []
    true_hallucinations = []

    for e in errors:
        if e["type"] != "FP":
            continue

        title = e["title"]
        value = e["actual"]
        has_evidence = has_title_evidence(value, title)

        entry = {**e, "has_evidence": has_evidence}
        if has_evidence:
            evidence_backed.append(entry)
        else:
            true_hallucinations.append(entry)

    wrong_errors = [e for e in errors if e["type"] == "WRONG"]
    total_fp = len(evidence_backed) + len(true_hallucinations)

    print(f"{'='*60}")
    print("HALLUCINATION ANALYSIS")
    print(f"{'='*60}")
    print(f"Total FP errors:       {total_fp}")
    print(f"Evidence-backed (GT wrong): {len(evidence_backed)} "
          f"({len(evidence_backed)/max(1,total_fp)*100:.0f}%)")
    print(f"True hallucinations:   {len(true_hallucinations)} "
          f"({len(true_hallucinations)/max(1,total_fp)*100:.0f}%)")

    # Recalculate metrics treating evidence-backed FPs as TPs
    tp = original_scores["true_positive"] + len(evidence_backed)
    tn = original_scores["true_negative"]
    fp = len(true_hallucinations)
    fn = original_scores["false_negative"]
    wrong = original_scores["wrong_value"]
    total = tp + tn + fp + fn + wrong

    accuracy = (tp + tn) / total * 100 if total > 0 else 0
    precision = tp / max(1, tp + fp + wrong) * 100
    recall = tp / max(1, tp + fn + wrong) * 100
    hallucination_rate = fp / total * 100 if total > 0 else 0

    print(f"\n{'='*60}")
    print("ADJUSTED METRICS (evidence-backed FPs -> TPs)")
    print(f"{'='*60}")
    print(f"  Accuracy:          {accuracy:.1f}% (was {saved['accuracy']:.1f}%)")
    print(f"  Precision:         {precision:.1f}% (was {saved['precision']:.1f}%)")
    print(f"  Recall:            {recall:.1f}% (was {saved['recall']:.1f}%)")
    print(f"  Hallucination rate:{hallucination_rate:.1f}% "
          f"(was {saved['hallucination_rate']:.1f}%)")

    print(f"\n  True Positive:  {tp:>5} (was {original_scores['true_positive']})")
    print(f"  True Negative:  {tn:>5}")
    print(f"  False Positive: {fp:>5} (was {original_scores['false_positive']})")
    print(f"  False Negative: {fn:>5}")
    print(f"  Wrong Value:    {wrong:>5}")

    # Show evidence-backed extractions (model was right)
    if evidence_backed:
        print(f"\n{'='*60}")
        print(f"EVIDENCE-BACKED (model correct, ground truth wrong)")
        print(f"{'='*60}")
        by_job = defaultdict(list)
        for e in evidence_backed:
            by_job[e["job"]].append(e)
        for job, items in sorted(by_job.items()):
            print(f"\n  {job} ({len(items)}):")
            for e in items[:8]:
                safe = e['title'].encode('ascii', 'replace').decode()
                print(f"    {e['axis']}={e['actual']} — {safe}")

    # Show true hallucinations
    if true_hallucinations:
        print(f"\n{'='*60}")
        print(f"TRUE HALLUCINATIONS (no title evidence)")
        print(f"{'='*60}")
        by_job = defaultdict(list)
        for e in true_hallucinations:
            by_job[e["job"]].append(e)
        for job, items in sorted(by_job.items()):
            print(f"\n  {job} ({len(items)}):")
            for e in items[:8]:
                safe = e['title'].encode('ascii', 'replace').decode()
                print(f"    {e['axis']}={e['actual']} — {safe}")

    # Per-job adjusted accuracy
    print(f"\n{'='*60}")
    print("PER-JOB ADJUSTED ACCURACY")
    print(f"{'='*60}")
    eb_by_job = defaultdict(int)
    th_by_job = defaultdict(int)
    for e in evidence_backed:
        eb_by_job[e["job"]] += 1
    for e in true_hallucinations:
        th_by_job[e["job"]] += 1

    for job, scores in sorted(saved["per_job"].items()):
        job_eb = eb_by_job.get(job, 0)
        job_th = th_by_job.get(job, 0)
        job_total = sum(scores.values())
        old_correct = scores["true_positive"] + scores["true_negative"]
        new_correct = old_correct + job_eb
        old_acc = old_correct / job_total * 100 if job_total > 0 else 0
        new_acc = new_correct / job_total * 100 if job_total > 0 else 0
        delta = new_acc - old_acc
        print(
            f"  {job:<40} {new_acc:5.1f}% "
            f"(+{delta:.1f}), {job_th} true hallucinations"
        )


def print_summary(total_scores, per_job_scores, errors,
                   model_label, n_samples, n_jobs, avg_time=None):
    """Print the scoring summary."""
    total = sum(total_scores.values())
    tp = total_scores["true_positive"]
    tn = total_scores["true_negative"]
    fp = total_scores["false_positive"]
    fn = total_scores["false_negative"]
    wrong = total_scores["wrong_value"]

    correct = tp + tn
    accuracy = correct / total * 100 if total > 0 else 0
    precision = tp / (tp + fp + wrong) * 100 if (tp + fp + wrong) > 0 else 0
    recall = tp / (tp + fn + wrong) * 100 if (tp + fn + wrong) > 0 else 0
    hallucination_rate = fp / total * 100 if total > 0 else 0

    print(f"\n{'='*60}")
    print(f"OVERALL RESULTS ({model_label})")
    print(f"{'='*60}")
    print(f"Samples: {n_samples} across {n_jobs} jobs")
    if avg_time:
        print(f"Avg extraction time: {avg_time:.1f}s/title")
    print(f"\nAxis-level metrics ({total} total axis evaluations):")
    print(f"  Accuracy:          {accuracy:.1f}% ({correct}/{total})")
    print(f"  Precision:         {precision:.1f}%")
    print(f"  Recall:            {recall:.1f}%")
    print(f"  Hallucination rate:{hallucination_rate:.1f}% "
          f"({fp} false positives)")
    if total > 0:
        print(f"  Wrong values:      {wrong} "
              f"({wrong/total*100:.1f}%)")
    print(f"\nBreakdown:")
    print(f"  True Positive:  {tp:>5} (correctly extracted)")
    print(f"  True Negative:  {tn:>5} (correctly null)")
    print(f"  False Positive: {fp:>5} (hallucinated)")
    print(f"  False Negative: {fn:>5} (missed)")
    print(f"  Wrong Value:    {wrong:>5} (extracted but wrong)")

    # Per-job breakdown
    print(f"\n{'='*60}")
    print(f"PER-JOB ACCURACY")
    print(f"{'='*60}")
    for job, scores in sorted(per_job_scores.items()):
        job_total = sum(scores.values())
        job_correct = scores["true_positive"] + scores["true_negative"]
        job_acc = job_correct / job_total * 100 if job_total > 0 else 0
        job_fp = scores["false_positive"]
        print(
            f"  {job:<40} {job_acc:5.1f}% acc, "
            f"{job_fp} hallucinations"
        )

    # Top errors
    if errors:
        print(f"\n{'='*60}")
        print(f"TOP ERRORS (hallucinations + wrong values)")
        print(f"{'='*60}")
        fp_errors = [e for e in errors if e["type"] == "FP"]
        wrong_errors = [e for e in errors if e["type"] == "WRONG"]

        if fp_errors:
            print(f"\nHallucinations ({len(fp_errors)}):")
            for e in fp_errors[:15]:
                safe = e['title'].encode('ascii', 'replace').decode()
                print(
                    f"  [{e['job'][:20]}] "
                    f"{e['axis']}={e['actual']} — {safe}"
                )

        if wrong_errors:
            print(f"\nWrong values ({len(wrong_errors)}):")
            for e in wrong_errors[:15]:
                safe = e['title'].encode('ascii', 'replace').decode()
                print(
                    f"  [{e['job'][:20]}] {e['axis']}: "
                    f"expected={e['expected']}, "
                    f"got={e['actual']} — {safe}"
                )


if __name__ == "__main__":
    main()
