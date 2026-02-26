"""
Nano prompt tuning: Test prompt variations on the 67 disagreement pairs
to see if we can fix nano's systematic over-splitting bias.

Variations tested:
  A) Baseline (current prompt, for re-validation)
  B) Enhanced rubric (more explicit about acceptable differences)
  C) Few-shot examples (3 examples showing color/year = same)
  D) Enhanced + few-shot + shorter max_tokens
"""

import json
import sys
import io
import time
import requests
from datetime import datetime
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor, as_completed
from threading import Lock

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

DATA_DIR = Path(__file__).parent
RESULTS_FILE = DATA_DIR / "model_comparison_results.json"
OUTPUT_FILE = DATA_DIR / "nano_prompt_tuning_results.json"

GPT_WORKERS = 10

# ── Load API key ──────────────────────────────────────────────────────────
def load_openai_key():
    for path in [
        DATA_DIR / ".." / ".." / "AIOMarketMaker.Api" / "bin" / "Debug" / "net8.0" / "local.settings.json",
        DATA_DIR / ".." / ".." / "AIOMarketMaker.Console" / "local.settings.json",
    ]:
        try:
            with open(path) as f:
                return json.load(f)["Values"]["OpenAi:ApiKey"]
        except (FileNotFoundError, KeyError):
            continue
    import os
    return os.environ.get("OPENAI_API_KEY", "")


OPENAI_API_KEY = load_openai_key()

# ── Structured output schema ──────────────────────────────────────────────
RESPONSE_SCHEMA = {
    "type": "json_schema",
    "json_schema": {
        "name": "variant_classification",
        "strict": True,
        "schema": {
            "type": "object",
            "properties": {
                "reasoning": {
                    "type": "string",
                    "description": "Brief analysis of key similarities and differences between the two listings.",
                },
                "label": {
                    "type": "integer",
                    "enum": [0, 1],
                    "description": "1 if same variant, 0 if different variant.",
                },
                "confidence": {
                    "type": "string",
                    "enum": ["high", "low"],
                    "description": "high if the distinction is clear, low if borderline.",
                },
            },
            "required": ["reasoning", "label", "confidence"],
            "additionalProperties": False,
        },
    },
}

# Schema with label first (to prevent reasoning from biasing the decision)
RESPONSE_SCHEMA_LABEL_FIRST = {
    "type": "json_schema",
    "json_schema": {
        "name": "variant_classification",
        "strict": True,
        "schema": {
            "type": "object",
            "properties": {
                "label": {
                    "type": "integer",
                    "enum": [0, 1],
                    "description": "1 if same variant, 0 if different variant.",
                },
                "confidence": {
                    "type": "string",
                    "enum": ["high", "low"],
                    "description": "high if the distinction is clear, low if borderline.",
                },
                "reasoning": {
                    "type": "string",
                    "description": "One sentence explaining the key factor.",
                },
            },
            "required": ["label", "confidence", "reasoning"],
            "additionalProperties": False,
        },
    },
}

# ── Prompt variations ─────────────────────────────────────────────────────

PROMPT_A = """You are classifying whether two eBay listings are the same product variant.

Same variant: identical functional specifications (model, size, storage, capacity, generation) and same level of completeness. Both sold as single items. Color, cosmetic condition, and packaging differences are acceptable.

Different variant: any difference in functional specifications, quantity (single unit vs bundle/lot), or mismatched completeness (e.g. complete product vs accessory-only, parts-only, box-only, or non-functional/for-parts)."""

PROMPT_B = """You are classifying whether two eBay listings are the same product variant.

SAME variant (label=1): Both listings have identical functional specifications — same model number, same size, same storage/capacity, same generation. Both are sold as single items with similar completeness.

These differences DO NOT make them different variants — label as SAME:
- Different color or finish
- Different cosmetic condition (used vs new, scratches, wear)
- Different packaging (box vs no box)
- Different seller descriptions or listing quality
- Different year of manufacture for the same model/reference
- Different included accessories (cables, cases, mats)

DIFFERENT variant (label=0): Any difference in functional specifications, such as:
- Different model number or version (e.g. Gen 1 vs Gen 2, Mark II vs Mark III)
- Different size (e.g. Small vs Large, 11" vs 13")
- Different storage or memory (e.g. 128GB vs 256GB)
- Single item vs bundle/lot
- Working product vs parts-only/for-repair"""

PROMPT_C = PROMPT_A + """

Examples:

Listing A: Ray-Ban RB2140 Wayfarer Blueberry Ice Pops
Listing B: Ray-Ban RB2140 Wayfarer Black Gloss Frame
Answer: SAME (label=1) — same model RB2140, color difference is acceptable.

Listing A: Rolex Submariner 16610 2010
Listing B: Rolex Submariner 16610 2007 Box & Papers
Answer: SAME (label=1) — same reference 16610, year and packaging differences are acceptable.

Listing A: MacBook Pro 14" M3 Pro 18GB 512GB
Listing B: MacBook Pro 14" M3 Pro 36GB 1TB
Answer: DIFFERENT (label=0) — different RAM and storage specifications."""

PROMPT_D = """Classify whether two eBay listings are the same product variant.

SAME (1): identical model, size, storage, generation. Color, condition, year, packaging differences are acceptable.
DIFFERENT (0): any difference in model, size, storage, generation, or quantity.

Examples:
- RB2140 Wayfarer Blue vs RB2140 Wayfarer Black → SAME (color ok)
- Rolex 16610 2010 vs Rolex 16610 2007 → SAME (year ok)
- MacBook Pro M3 18GB vs M3 36GB → DIFFERENT (RAM differs)
- Peloton Bike + mat vs Peloton Bike → SAME (accessory ok)
- Dyson V15 working vs V15 for parts → DIFFERENT (completeness differs)"""


VARIATIONS = {
    "A_baseline": {
        "system_prompt": PROMPT_A,
        "schema": RESPONSE_SCHEMA,
        "max_tokens": 2000,
        "temperature": None,  # default
    },
    "B_enhanced_rubric": {
        "system_prompt": PROMPT_B,
        "schema": RESPONSE_SCHEMA,
        "max_tokens": 2000,
        "temperature": None,
    },
    "C_few_shot": {
        "system_prompt": PROMPT_C,
        "schema": RESPONSE_SCHEMA,
        "max_tokens": 2000,
        "temperature": None,
    },
    "D_compact_few_shot": {
        "system_prompt": PROMPT_D,
        "schema": RESPONSE_SCHEMA_LABEL_FIRST,
        "max_tokens": 300,
        "temperature": 0,
    },
}

# ── Logging ───────────────────────────────────────────────────────────────
_log_lock = Lock()

def log(msg):
    ts = datetime.now().strftime("%H:%M:%S")
    with _log_lock:
        print(f"[{ts}] {msg}", flush=True)


# ── GPT call ──────────────────────────────────────────────────────────────
def call_gpt(user_msg, variation, session):
    v = VARIATIONS[variation]
    body = {
        "model": "gpt-5-nano",
        "messages": [
            {"role": "system", "content": v["system_prompt"]},
            {"role": "user", "content": user_msg},
        ],
        "response_format": v["schema"],
        "max_completion_tokens": v["max_tokens"],
    }
    if v["temperature"] is not None:
        body["temperature"] = v["temperature"]

    for attempt in range(3):
        try:
            resp = session.post(
                "https://api.openai.com/v1/chat/completions",
                json=body,
            )
            if resp.status_code == 429:
                time.sleep(2 ** (attempt + 1))
                continue
            if resp.status_code >= 500:
                time.sleep(2 ** (attempt + 1))
                continue
            resp.raise_for_status()

            content = resp.json()["choices"][0]["message"]["content"]
            if not content:
                time.sleep(1)
                continue

            result = json.loads(content)
            usage = resp.json().get("usage", {})
            return {
                "label": result["label"],
                "confidence": result.get("confidence", "unknown"),
                "reasoning": result.get("reasoning", ""),
                "input_tokens": usage.get("prompt_tokens", 0),
                "output_tokens": usage.get("completion_tokens", 0),
            }
        except Exception as e:
            if attempt == 2:
                return None
            time.sleep(1)
    return None


def build_user_prompt(product_name, title_a, desc_a, title_b, desc_b):
    parts = [f"Product category: {product_name}\n"]
    parts.append(f"Listing A: {title_a}")
    if desc_a:
        parts.append(f"{desc_a}")
    parts.append(f"\nListing B: {title_b}")
    if desc_b:
        parts.append(f"{desc_b}")
    return "\n".join(parts)


def main():
    log("=" * 70)
    log("Nano Prompt Tuning: Testing variations on disagreement pairs")
    log("=" * 70)

    # Load the comparison results to find disagreement pairs
    with open(RESULTS_FILE, encoding="utf-8") as f:
        data = json.load(f)

    # Find disagreement pairs (where both models had results but different labels)
    disagreements = []
    all_pairs = []
    for p in data["pairs"]:
        mini_label = p.get("gpt-5-mini_label")
        nano_label = p.get("gpt-5-nano_label")
        if mini_label is not None and nano_label is not None:
            all_pairs.append(p)
            if mini_label != nano_label:
                disagreements.append(p)

    log(f"Total valid pairs: {len(all_pairs)}")
    log(f"Disagreement pairs: {len(disagreements)}")
    log(f"Agreement pairs (sample): using 100 random agreement pairs as control")

    # Also sample 100 agreement pairs as a control (to check we don't regress)
    agreements = [p for p in all_pairs if p["gpt-5-mini_label"] == p["gpt-5-nano_label"]]
    import random
    random.seed(42)
    control_sample = random.sample(agreements, min(100, len(agreements)))

    test_pairs = disagreements + control_sample
    log(f"Total test pairs: {len(test_pairs)} ({len(disagreements)} disagreements + {len(control_sample)} control)")

    # Run each variation
    all_variation_results = {}

    session = requests.Session()
    session.headers.update({
        "Authorization": f"Bearer {OPENAI_API_KEY}",
        "Content-Type": "application/json",
    })

    for var_name in VARIATIONS:
        log(f"\n--- Variation {var_name} ---")
        start = time.time()

        results = [None] * len(test_pairs)
        completed = [0]
        total_input = [0]
        total_output = [0]

        def label_one(idx, vn=var_name):
            p = test_pairs[idx]
            # Build user prompt from available data
            user_msg = build_user_prompt(
                p["product_name"],
                p["anchor_title"], "",  # descriptions not stored in comparison results
                p["neighbor_title"], "",
            )
            result = call_gpt(user_msg, vn, session)
            results[idx] = result
            if result:
                total_input[0] += result["input_tokens"]
                total_output[0] += result["output_tokens"]
            completed[0] += 1
            if completed[0] % 50 == 0 or completed[0] == len(test_pairs):
                log(f"  [{vn}] {completed[0]}/{len(test_pairs)}")

        with ThreadPoolExecutor(max_workers=GPT_WORKERS) as executor:
            futures = [executor.submit(label_one, i) for i in range(len(test_pairs))]
            for f in as_completed(futures):
                try:
                    f.result()
                except Exception as e:
                    log(f"  Error: {e}")

        elapsed = time.time() - start
        success = sum(1 for r in results if r is not None)
        log(f"  Completed: {success}/{len(test_pairs)} in {elapsed:.0f}s")
        log(f"  Tokens: {total_input[0]:,} in, {total_output[0]:,} out")

        all_variation_results[var_name] = results

    # ── Analysis ──────────────────────────────────────────────────────────
    log(f"\n{'=' * 70}")
    log("RESULTS")
    log(f"{'=' * 70}")

    n_disagree = len(disagreements)

    for var_name, results in all_variation_results.items():
        # Split results into disagreement and control portions
        disagree_results = results[:n_disagree]
        control_results = results[n_disagree:]

        # On disagreement pairs: how often does nano now agree with mini?
        fixed = 0
        still_wrong = 0
        flipped_wrong = 0  # was right (agreed w/ mini on control), now wrong
        valid_disagree = 0

        for i, r in enumerate(disagree_results):
            if r is None:
                continue
            valid_disagree += 1
            mini_label = disagreements[i]["gpt-5-mini_label"]
            if r["label"] == mini_label:
                fixed += 1
            else:
                still_wrong += 1

        # On control pairs: does nano still agree with mini?
        control_agree = 0
        control_regress = 0
        valid_control = 0

        for i, r in enumerate(control_results):
            if r is None:
                continue
            valid_control += 1
            mini_label = control_sample[i]["gpt-5-mini_label"]
            if r["label"] == mini_label:
                control_agree += 1
            else:
                control_regress += 1

        # Token stats
        valid_results = [r for r in results if r is not None]
        avg_output = sum(r["output_tokens"] for r in valid_results) / len(valid_results) if valid_results else 0
        avg_input = sum(r["input_tokens"] for r in valid_results) / len(valid_results) if valid_results else 0

        # Label distribution
        same_count = sum(1 for r in valid_results if r["label"] == 1)
        diff_count = sum(1 for r in valid_results if r["label"] == 0)

        fix_rate = fixed / valid_disagree if valid_disagree else 0
        control_rate = control_agree / valid_control if valid_control else 0

        # Projected overall agreement
        # Original: 402 agree out of 469
        # New: 402 + fixed, minus control_regress (scaled to full 402)
        projected_new_agree = 402 + fixed - int(control_regress * (402 / valid_control)) if valid_control else 402 + fixed
        projected_agreement = projected_new_agree / 469

        log(f"\n  {var_name}:")
        log(f"    Disagreements fixed: {fixed}/{valid_disagree} ({fix_rate:.0%})")
        log(f"    Control preserved:   {control_agree}/{valid_control} ({control_rate:.0%})")
        log(f"    Control regressed:   {control_regress}/{valid_control}")
        log(f"    Label split:         {same_count} same / {diff_count} diff ({same_count/len(valid_results):.0%} same)" if valid_results else "")
        log(f"    Avg tokens:          {avg_input:.0f} in, {avg_output:.0f} out")
        log(f"    Projected agreement: {projected_agreement:.1%} (was 85.7%)")

        # Cost projection
        pricing_input, pricing_output = 0.05, 0.40  # nano pricing per 1M
        cost_200k = ((avg_input * 200_000 / 1_000_000) * pricing_input +
                     (avg_output * 200_000 / 1_000_000) * pricing_output)
        batch_cost = cost_200k * 0.5
        log(f"    200K pairs cost:     ${cost_200k:.2f} (Batch: ${batch_cost:.2f})")

    # Save detailed results
    output = {
        "metadata": {
            "date": datetime.now().isoformat(),
            "disagreement_count": len(disagreements),
            "control_count": len(control_sample),
        },
        "variations": {},
    }

    for var_name, results in all_variation_results.items():
        variation_data = []
        for i, r in enumerate(results):
            p = test_pairs[i]
            entry = {
                "anchor_title": p["anchor_title"],
                "neighbor_title": p["neighbor_title"],
                "product_name": p["product_name"],
                "mini_label": p["gpt-5-mini_label"],
                "is_disagreement": i < n_disagree,
            }
            if r:
                entry["nano_label"] = r["label"]
                entry["confidence"] = r["confidence"]
                entry["reasoning"] = r["reasoning"]
                entry["output_tokens"] = r["output_tokens"]
            variation_data.append(entry)
        output["variations"][var_name] = variation_data

    with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
        json.dump(output, f, indent=2, ensure_ascii=False)
    log(f"\nDetailed results saved to: {OUTPUT_FILE}")


if __name__ == "__main__":
    main()
