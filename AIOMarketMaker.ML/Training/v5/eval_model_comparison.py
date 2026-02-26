"""
Model comparison: gpt-5-mini vs gpt-5-nano on variant classification labeling.

Picks 500 hard pairs (high cosine similarity, same category) and labels
each with both models. Measures agreement rate to determine if nano is
a viable cheaper alternative for training data generation.
"""

import csv
import json
import os
import random
import subprocess
import sys
import io
import time
import requests
from datetime import datetime
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor, as_completed
from threading import Lock

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

# ── Config ────────────────────────────────────────────────────────────────
DATA_DIR = Path(__file__).parent
OUTPUT_FILE = DATA_DIR / "model_comparison_results.json"
LISTINGS_CACHE = DATA_DIR / "listings_v5_cache.csv"

PINECONE_HOST = "https://arbitrage-d207f30.svc.aped-4627-b74a.pinecone.io"

TARGET_PAIRS = 500
PINECONE_TOP_K = 20
MIN_COSINE_SIM = 0.75  # only hard pairs (high similarity)

MODELS = ["gpt-5-mini", "gpt-5-nano"]
GPT_MAX_TOKENS = 2000
GPT_WORKERS = 10

# ── Load API keys ─────────────────────────────────────────────────────────
def load_api_keys():
    settings_path = DATA_DIR / ".." / ".." / "AIOMarketMaker.Api" / "bin" / "Debug" / "net8.0" / "local.settings.json"
    try:
        with open(settings_path) as f:
            settings = json.load(f)
            openai_key = settings["Values"].get("OpenAi:ApiKey", "")
            pinecone_key = settings["Values"].get("Pinecone:ApiKey", "")
            return openai_key, pinecone_key
    except (FileNotFoundError, KeyError):
        # Try Console settings
        etl_path = DATA_DIR / ".." / ".." / "AIOMarketMaker.Console" / "local.settings.json"
        try:
            with open(etl_path) as f:
                settings = json.load(f)
                openai_key = settings["Values"].get("OpenAi:ApiKey", "")
                pinecone_key = settings["Values"].get("Pinecone:ApiKey", "")
                return openai_key, pinecone_key
        except (FileNotFoundError, KeyError):
            return os.environ.get("OPENAI_API_KEY", ""), os.environ.get("PINECONE_API_KEY", "")


OPENAI_API_KEY, PINECONE_API_KEY = load_api_keys()

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

SYSTEM_PROMPT = """You are classifying whether two eBay listings are the same product variant.

Same variant: identical functional specifications (model, size, storage, capacity, generation) and same level of completeness. Both sold as single items. Color, cosmetic condition, and packaging differences are acceptable.

Different variant: any difference in functional specifications, quantity (single unit vs bundle/lot), or mismatched completeness (e.g. complete product vs accessory-only, parts-only, box-only, or non-functional/for-parts)."""

# ── Thread-safe logging ───────────────────────────────────────────────────
_log_lock = Lock()

def log(msg):
    ts = datetime.now().strftime("%H:%M:%S")
    line = f"[{ts}] {msg}"
    with _log_lock:
        print(line, flush=True)


# ── Load products from DB ─────────────────────────────────────────────────
def load_products_from_db():
    result = subprocess.run(
        ["sqlcmd", "-S", r"(localdb)\MSSQLLocalDB", "-d", "AIOMarketMaker",
         "-W", "-s", "|", "-h", "-1",
         "-Q", "SET NOCOUNT ON; SELECT Id, SearchTerm FROM ScrapeJobs WHERE IsEnabled = 1"],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    products = {}
    for line in result.stdout.strip().split("\n"):
        line = line.strip()
        if not line or "|" not in line:
            continue
        parts = line.split("|")
        try:
            products[int(parts[0].strip())] = parts[1].strip()
        except (ValueError, IndexError):
            continue
    return products


# ── Load listings ─────────────────────────────────────────────────────────
def export_listings_csv():
    log("Exporting listings from database...")
    result = subprocess.run(
        ["sqlcmd", "-S", r"(localdb)\MSSQLLocalDB", "-d", "AIOMarketMaker",
         "-W", "-s", "|", "-h", "-1",
         "-Q", ("SET NOCOUNT ON; "
                "SELECT l.ListingId, l.ScrapeJobId, l.Title, "
                "REPLACE(REPLACE(LEFT(ISNULL(l.Description,''), 300), CHAR(10), ' '), CHAR(13), ' '), "
                "l.ListingStatus, CAST(l.Price AS VARCHAR(20)) "
                "FROM Listings l WHERE l.Title IS NOT NULL")],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    lines = [l for l in result.stdout.strip().split("\n") if l.strip() and "|" in l]
    with open(LISTINGS_CACHE, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    log(f"Exported {len(lines)} listings to {LISTINGS_CACHE.name}")


def load_listings():
    if not LISTINGS_CACHE.exists():
        export_listings_csv()

    listings = {}
    with open(LISTINGS_CACHE, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            parts = line.split("|")
            if len(parts) < 6:
                continue
            lid = parts[0].strip()
            try:
                job_id = int(parts[1].strip())
                price = float(parts[5].strip()) if parts[5].strip() else 0
            except (ValueError, IndexError):
                continue
            desc = parts[3].strip() if len(parts) > 3 else ""
            if desc and (desc.startswith(".header") or desc.startswith("@media") or
                         desc.startswith("* {") or desc.startswith("Pages View")):
                desc = ""
            listings[lid] = {
                "listingId": lid,
                "scrapeJobId": job_id,
                "title": parts[2].strip(),
                "description": desc[:300],
                "price": price,
            }
    return listings


# ── Pinecone helpers ──────────────────────────────────────────────────────
def pinecone_fetch(ids):
    all_vectors = {}
    for i in range(0, len(ids), 100):
        batch = ids[i:i + 100]
        params = "&".join(f"ids={id_}" for id_ in batch)
        resp = requests.get(
            f"{PINECONE_HOST}/vectors/fetch?{params}",
            headers={"Api-Key": PINECONE_API_KEY},
        )
        resp.raise_for_status()
        all_vectors.update(resp.json().get("vectors", {}))
        if i + 100 < len(ids):
            time.sleep(0.2)
    return all_vectors


def pinecone_query(vector, top_k=PINECONE_TOP_K):
    resp = requests.post(
        f"{PINECONE_HOST}/query",
        headers={"Api-Key": PINECONE_API_KEY, "Content-Type": "application/json"},
        json={
            "vector": vector,
            "topK": top_k,
            "includeMetadata": True,
            "includeValues": False,
        },
    )
    resp.raise_for_status()
    return resp.json()["matches"]


# ── GPT labeling ──────────────────────────────────────────────────────────
def build_user_prompt(product_name, title_a, desc_a, title_b, desc_b):
    parts = [f"Product category: {product_name}\n"]
    parts.append(f"Listing A: {title_a}")
    if desc_a:
        parts.append(f"{desc_a}")
    parts.append(f"\nListing B: {title_b}")
    if desc_b:
        parts.append(f"{desc_b}")
    return "\n".join(parts)


def label_pair_with_model(title_a, desc_a, title_b, desc_b, product_name, model, session):
    """Label a single pair with a specific model. Returns dict or None."""
    user_msg = build_user_prompt(product_name, title_a, desc_a, title_b, desc_b)

    for attempt in range(3):
        try:
            resp = session.post(
                "https://api.openai.com/v1/chat/completions",
                json={
                    "model": model,
                    "messages": [
                        {"role": "system", "content": SYSTEM_PROMPT},
                        {"role": "user", "content": user_msg},
                    ],
                    "response_format": RESPONSE_SCHEMA,
                    "max_completion_tokens": GPT_MAX_TOKENS,
                },
            )

            if resp.status_code == 429:
                wait = 2 ** (attempt + 1)
                log(f"    Rate limited ({model}), waiting {wait}s")
                time.sleep(wait)
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
                "confidence": result["confidence"],
                "reasoning": result["reasoning"],
                "input_tokens": usage.get("prompt_tokens", 0),
                "output_tokens": usage.get("completion_tokens", 0),
            }

        except Exception as e:
            if attempt == 2:
                log(f"    {model} failed after 3 attempts: {str(e)[:80]}")
                return None
            time.sleep(1)

    return None


# ── Pair generation ───────────────────────────────────────────────────────
def generate_hard_pairs(listings, products, target_count):
    """Generate hard pairs by picking anchors and finding high-similarity neighbors."""
    log("Generating hard pairs from Pinecone...")

    by_job = {}
    for l in listings.values():
        jid = l["scrapeJobId"]
        if jid in products:
            by_job.setdefault(jid, []).append(l)

    valid_jobs = {jid: ls for jid, ls in by_job.items() if len(ls) >= 10}
    log(f"  {len(valid_jobs)} valid categories")

    pairs_per_job = max(5, target_count // len(valid_jobs))
    pairs = []
    job_ids = list(valid_jobs.keys())
    random.shuffle(job_ids)

    for job_id in job_ids:
        if len(pairs) >= target_count:
            break

        product_name = products[job_id]
        job_listings = valid_jobs[job_id]
        random.shuffle(job_listings)

        anchors_tried = 0
        job_pairs = 0

        for anchor in job_listings:
            if job_pairs >= pairs_per_job or anchors_tried >= 10:
                break
            anchors_tried += 1

            # Fetch anchor vector from Pinecone
            vectors = pinecone_fetch([anchor["listingId"]])
            if anchor["listingId"] not in vectors:
                continue

            vector = vectors[anchor["listingId"]]["values"]
            matches = pinecone_query(vector, top_k=PINECONE_TOP_K)

            # Filter to high-similarity matches that are in our listings
            for m in matches:
                if m["id"] == anchor["listingId"]:
                    continue
                if m["score"] < MIN_COSINE_SIM:
                    continue
                if m["id"] not in listings:
                    continue
                if job_pairs >= pairs_per_job:
                    break

                neighbor = listings[m["id"]]
                pairs.append({
                    "anchor_id": anchor["listingId"],
                    "neighbor_id": neighbor["listingId"],
                    "anchor_title": anchor["title"],
                    "anchor_desc": anchor["description"],
                    "neighbor_title": neighbor["title"],
                    "neighbor_desc": neighbor["description"],
                    "product_name": product_name,
                    "cosine_sim": m["score"],
                    "job_id": job_id,
                })
                job_pairs += 1

            time.sleep(0.1)

        if job_pairs > 0:
            log(f"  {product_name}: {job_pairs} pairs (sim >= {MIN_COSINE_SIM})")

    random.shuffle(pairs)
    pairs = pairs[:target_count]
    log(f"Generated {len(pairs)} hard pairs across {len(set(p['product_name'] for p in pairs))} categories")
    return pairs


# ── Main comparison ───────────────────────────────────────────────────────
def run_comparison(pairs, model_name):
    """Label all pairs with a single model. Returns list of results."""
    session = requests.Session()
    session.headers.update({
        "Authorization": f"Bearer {OPENAI_API_KEY}",
        "Content-Type": "application/json",
    })

    results = [None] * len(pairs)
    completed = [0]
    total = len(pairs)
    total_input_tokens = [0]
    total_output_tokens = [0]

    def label_one(idx):
        p = pairs[idx]
        result = label_pair_with_model(
            p["anchor_title"], p["anchor_desc"],
            p["neighbor_title"], p["neighbor_desc"],
            p["product_name"], model_name, session,
        )
        results[idx] = result
        if result:
            total_input_tokens[0] += result.get("input_tokens", 0)
            total_output_tokens[0] += result.get("output_tokens", 0)
        completed[0] += 1
        if completed[0] % 50 == 0 or completed[0] == total:
            log(f"  [{model_name}] {completed[0]}/{total}")

    with ThreadPoolExecutor(max_workers=GPT_WORKERS) as executor:
        futures = [executor.submit(label_one, i) for i in range(total)]
        for f in as_completed(futures):
            try:
                f.result()
            except Exception as e:
                log(f"  Thread error: {e}")

    return results, total_input_tokens[0], total_output_tokens[0]


def main():
    log("=" * 70)
    log("Model Comparison: gpt-5-mini vs gpt-5-nano")
    log(f"Target: {TARGET_PAIRS} hard pairs (cosine >= {MIN_COSINE_SIM})")
    log("=" * 70)

    if not OPENAI_API_KEY or not PINECONE_API_KEY:
        log("ERROR: Missing API keys")
        sys.exit(1)

    # Load data
    products = load_products_from_db()
    log(f"Found {len(products)} enabled scrape jobs")

    listings = load_listings()
    log(f"Loaded {len(listings)} listings")

    # Generate hard pairs
    pairs = generate_hard_pairs(listings, products, TARGET_PAIRS)
    if len(pairs) < 100:
        log(f"ERROR: Only generated {len(pairs)} pairs, need at least 100")
        sys.exit(1)

    # Label with both models
    all_model_results = {}
    all_token_usage = {}

    for model_name in MODELS:
        log(f"\nLabeling {len(pairs)} pairs with {model_name}...")
        start = time.time()
        results, input_tokens, output_tokens = run_comparison(pairs, model_name)
        elapsed = time.time() - start

        success_count = sum(1 for r in results if r is not None)
        log(f"  {model_name}: {success_count}/{len(pairs)} successful in {elapsed:.0f}s")
        log(f"  Tokens: {input_tokens:,} input, {output_tokens:,} output")

        all_model_results[model_name] = results
        all_token_usage[model_name] = {
            "input_tokens": input_tokens,
            "output_tokens": output_tokens,
            "elapsed_seconds": elapsed,
        }

    # ── Analysis ──────────────────────────────────────────────────────────
    log(f"\n{'=' * 70}")
    log("ANALYSIS")
    log(f"{'=' * 70}")

    mini_results = all_model_results["gpt-5-mini"]
    nano_results = all_model_results["gpt-5-nano"]

    # Filter to pairs where both models succeeded
    both_valid = []
    for i in range(len(pairs)):
        if mini_results[i] is not None and nano_results[i] is not None:
            both_valid.append(i)

    log(f"\nBoth models succeeded: {len(both_valid)}/{len(pairs)} pairs")

    # Overall agreement
    agree = sum(1 for i in both_valid if mini_results[i]["label"] == nano_results[i]["label"])
    disagree_indices = [i for i in both_valid if mini_results[i]["label"] != nano_results[i]["label"]]
    agreement_rate = agree / len(both_valid) if both_valid else 0

    log(f"\nOverall agreement: {agree}/{len(both_valid)} ({agreement_rate:.1%})")
    log(f"Disagreements: {len(disagree_indices)}")

    # Agreement by confidence
    both_high = [i for i in both_valid
                 if mini_results[i]["confidence"] == "high" and nano_results[i]["confidence"] == "high"]
    both_high_agree = sum(1 for i in both_high if mini_results[i]["label"] == nano_results[i]["label"])
    log(f"\nBoth high confidence: {len(both_high)} pairs, agree: {both_high_agree}/{len(both_high)} ({both_high_agree/len(both_high):.1%})" if both_high else "")

    any_low = [i for i in both_valid
               if mini_results[i]["confidence"] == "low" or nano_results[i]["confidence"] == "low"]
    any_low_agree = sum(1 for i in any_low if mini_results[i]["label"] == nano_results[i]["label"])
    log(f"Any low confidence: {len(any_low)} pairs, agree: {any_low_agree}/{len(any_low)} ({any_low_agree/len(any_low):.1%})" if any_low else "")

    # Label distribution
    for model_name in MODELS:
        results = all_model_results[model_name]
        valid = [r for r in results if r is not None]
        same_count = sum(1 for r in valid if r["label"] == 1)
        diff_count = sum(1 for r in valid if r["label"] == 0)
        high_conf = sum(1 for r in valid if r["confidence"] == "high")
        log(f"\n{model_name}:")
        log(f"  Same: {same_count}, Different: {diff_count} ({same_count/len(valid):.0%} same)")
        log(f"  High confidence: {high_conf}/{len(valid)} ({high_conf/len(valid):.0%})")

    # Agreement by cosine similarity band
    log(f"\nAgreement by cosine similarity band:")
    bands = [(0.75, 0.80), (0.80, 0.85), (0.85, 0.90), (0.90, 0.95), (0.95, 1.01)]
    for low, high in bands:
        band_indices = [i for i in both_valid if low <= pairs[i]["cosine_sim"] < high]
        if not band_indices:
            continue
        band_agree = sum(1 for i in band_indices if mini_results[i]["label"] == nano_results[i]["label"])
        log(f"  sim {low:.2f}-{high:.2f}: {len(band_indices)} pairs, agree: {band_agree}/{len(band_indices)} ({band_agree/len(band_indices):.1%})")

    # Agreement by category
    log(f"\nAgreement by category (worst first):")
    cat_stats = {}
    for i in both_valid:
        cat = pairs[i]["product_name"]
        if cat not in cat_stats:
            cat_stats[cat] = {"total": 0, "agree": 0}
        cat_stats[cat]["total"] += 1
        if mini_results[i]["label"] == nano_results[i]["label"]:
            cat_stats[cat]["agree"] += 1

    sorted_cats = sorted(cat_stats.items(), key=lambda x: x[1]["agree"] / x[1]["total"] if x[1]["total"] > 0 else 1)
    for cat, stats in sorted_cats:
        rate = stats["agree"] / stats["total"] if stats["total"] > 0 else 0
        if rate < 1.0 or stats["total"] >= 5:
            log(f"  {cat:<35} {stats['agree']}/{stats['total']} ({rate:.0%})")

    # Disagreement examples
    if disagree_indices:
        log(f"\n{'=' * 70}")
        log(f"DISAGREEMENT EXAMPLES (showing up to 20)")
        log(f"{'=' * 70}")
        for i in disagree_indices[:20]:
            p = pairs[i]
            m = mini_results[i]
            n = nano_results[i]
            mini_label = "SAME" if m["label"] == 1 else "DIFF"
            nano_label = "SAME" if n["label"] == 1 else "DIFF"
            log(f"\n  [{p['product_name']}] sim={p['cosine_sim']:.3f}")
            log(f"    A: {p['anchor_title'][:80]}")
            log(f"    B: {p['neighbor_title'][:80]}")
            log(f"    mini: {mini_label} ({m['confidence']}) — {m['reasoning'][:100]}")
            log(f"    nano: {nano_label} ({n['confidence']}) — {n['reasoning'][:100]}")

    # Cost projection
    log(f"\n{'=' * 70}")
    log("COST PROJECTION (200K pairs)")
    log(f"{'=' * 70}")

    for model_name in MODELS:
        usage = all_token_usage[model_name]
        valid_count = sum(1 for r in all_model_results[model_name] if r is not None)
        if valid_count == 0:
            continue
        avg_input = usage["input_tokens"] / valid_count
        avg_output = usage["output_tokens"] / valid_count

        # Pricing per 1M tokens
        pricing = {
            "gpt-5-mini": (0.25, 2.00),
            "gpt-5-nano": (0.05, 0.40),
        }
        input_rate, output_rate = pricing.get(model_name, (0, 0))

        projected_input = avg_input * 200_000
        projected_output = avg_output * 200_000
        input_cost = (projected_input / 1_000_000) * input_rate
        output_cost = (projected_output / 1_000_000) * output_rate
        total_cost = input_cost + output_cost
        batch_cost = total_cost * 0.5

        log(f"\n  {model_name}:")
        log(f"    Avg tokens/pair: {avg_input:.0f} input, {avg_output:.0f} output")
        log(f"    200K pairs: ${input_cost:.2f} input + ${output_cost:.2f} output = ${total_cost:.2f}")
        log(f"    With Batch API (50% off): ${batch_cost:.2f}")

    # Save full results
    output = {
        "metadata": {
            "date": datetime.now().isoformat(),
            "total_pairs": len(pairs),
            "both_valid": len(both_valid),
            "agreement_rate": agreement_rate,
            "min_cosine_sim": MIN_COSINE_SIM,
            "models": MODELS,
        },
        "token_usage": all_token_usage,
        "pairs": [],
    }

    for i in range(len(pairs)):
        pair_data = {
            "anchor_title": pairs[i]["anchor_title"],
            "neighbor_title": pairs[i]["neighbor_title"],
            "product_name": pairs[i]["product_name"],
            "cosine_sim": pairs[i]["cosine_sim"],
        }
        for model_name in MODELS:
            r = all_model_results[model_name][i]
            if r:
                pair_data[f"{model_name}_label"] = r["label"]
                pair_data[f"{model_name}_confidence"] = r["confidence"]
                pair_data[f"{model_name}_reasoning"] = r["reasoning"]
            else:
                pair_data[f"{model_name}_label"] = None
        output["pairs"].append(pair_data)

    with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
        json.dump(output, f, indent=2, ensure_ascii=False)
    log(f"\nFull results saved to: {OUTPUT_FILE}")

    # Final verdict
    log(f"\n{'=' * 70}")
    log("VERDICT")
    log(f"{'=' * 70}")
    if agreement_rate >= 0.95:
        log(f"Agreement rate {agreement_rate:.1%} >= 95%: nano is VIABLE for training data generation")
    elif agreement_rate >= 0.90:
        log(f"Agreement rate {agreement_rate:.1%} >= 90%: nano is BORDERLINE — review disagreements carefully")
    else:
        log(f"Agreement rate {agreement_rate:.1%} < 90%: nano is NOT recommended — stick with mini")


if __name__ == "__main__":
    main()
