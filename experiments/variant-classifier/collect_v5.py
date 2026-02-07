"""
V5 Data Collection: 25K labeled pairs with structured outputs.

Improvements over V4:
- Single GPT call with structured output (reasoning → label → confidence)
- Over-fetches from Pinecone (top_k=50), filters seen pairs for dedup
- 44+ categories with equal representation
- Checkpoints per-job for resume capability
- CSV + JSON output
"""

import csv
import json
import os
import random
import subprocess
import sys
import time
import numpy as np
import requests
from datetime import datetime
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor, as_completed
from threading import Lock

# ── Config ────────────────────────────────────────────────────────────────
DATA_DIR = Path(__file__).parent
CHECKPOINT_FILE = DATA_DIR / "collect_v5_checkpoint.json"
OUTPUT_JSON = DATA_DIR / "labeled_pairs_v5.json"
OUTPUT_CSV = DATA_DIR / "labeled_pairs_v5.csv"
LISTINGS_CACHE = DATA_DIR / "listings_v5_cache.csv"
LOG_FILE = DATA_DIR / f"collect_v5_{datetime.now().strftime('%Y%m%d_%H%M%S')}.log"

PINECONE_HOST = "https://arbitrage-d207f30.svc.aped-4627-b74a.pinecone.io"

TARGET_PAIRS = 25000
ANCHORS_PER_JOB = 115       # max anchors per category
ANCHOR_CAP_PCT = 0.50        # cap at 50% of category size
PINECONE_TOP_K = 50           # over-fetch for dedup headroom
NEIGHBORS_PER_ANCHOR = 5      # pairs generated per anchor
GPT_MODEL = "gpt-5-mini"
GPT_MAX_TOKENS = 2000
GPT_WORKERS = 15              # parallel GPT threads
PINECONE_SLEEP = 0.1          # sleep between Pinecone queries

# ── Load API keys from local.settings.json ────────────────────────────────
def load_api_keys():
    settings_path = DATA_DIR / ".." / ".." / "AIOMarketMaker.Api" / "bin" / "Debug" / "net8.0" / "local.settings.json"
    try:
        with open(settings_path) as f:
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

# ── Thread-safe logging (console + file) ─────────────────────────────────
_log_lock = Lock()
_log_file = None

def _init_log():
    global _log_file
    _log_file = open(LOG_FILE, "w", encoding="utf-8")

def log(msg):
    ts = datetime.now().strftime("%H:%M:%S")
    line = f"[{ts}] {msg}"
    with _log_lock:
        print(line, flush=True)
        if _log_file:
            _log_file.write(line + "\n")
            _log_file.flush()


def safe(s):
    return str(s).encode("ascii", "replace").decode()


# ── Load product map from DB ──────────────────────────────────────────────
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
    """Export fresh listings from DB to cache CSV."""
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
    """Load listings from cached CSV."""
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
            # Filter junk descriptions
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


# ── Anchor selection ──────────────────────────────────────────────────────
def pick_anchors(listings, job_id, n):
    """Pick n anchors spread across the price range, filtering outlier accessories."""
    candidates = [l for l in listings.values() if l["scrapeJobId"] == job_id]
    if len(candidates) < 10:
        return []

    # Filter below 10th percentile (likely accessories/parts)
    prices = sorted(c["price"] for c in candidates if c["price"] > 0)
    if prices:
        price_floor = prices[len(prices) // 10]
        filtered = [c for c in candidates if c["price"] >= price_floor]
        if len(filtered) >= n:
            candidates = filtered

    random.shuffle(candidates)
    return candidates[:n]


# ── Pinecone helpers ──────────────────────────────────────────────────────
def pinecone_fetch(ids):
    """Fetch vectors by ID from Pinecone."""
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


def pinecone_query(vector, top_k=PINECONE_TOP_K, job_id=None):
    """Query Pinecone for similar vectors, optionally filtered by job."""
    body = {
        "vector": vector,
        "topK": top_k,
        "includeMetadata": True,
        "includeValues": False,
    }
    if job_id is not None:
        body["filter"] = {"scrapeJobId": {"$eq": job_id}}
    resp = requests.post(
        f"{PINECONE_HOST}/query",
        headers={"Api-Key": PINECONE_API_KEY, "Content-Type": "application/json"},
        json=body,
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


# Track fatal API errors across threads
_fatal_error = None
_fatal_lock = Lock()

def _set_fatal(msg):
    global _fatal_error
    with _fatal_lock:
        if _fatal_error is None:
            _fatal_error = msg
            log(f"  FATAL API ERROR: {msg}")

def _is_fatal():
    with _fatal_lock:
        return _fatal_error is not None


def label_pair(title_a, desc_a, title_b, desc_b, product_name, session):
    """Label a single pair using structured output. Returns (label, confidence, reasoning) or None on failure."""
    if _is_fatal():
        return None

    user_msg = build_user_prompt(product_name, title_a, desc_a, title_b, desc_b)

    for attempt in range(3):
        if _is_fatal():
            return None
        try:
            resp = session.post(
                "https://api.openai.com/v1/chat/completions",
                json={
                    "model": GPT_MODEL,
                    "messages": [
                        {"role": "system", "content": SYSTEM_PROMPT},
                        {"role": "user", "content": user_msg},
                    ],
                    "response_format": RESPONSE_SCHEMA,
                    "max_completion_tokens": GPT_MAX_TOKENS,
                },
            )

            if resp.status_code == 429:
                error_body = resp.json().get("error", {})
                error_code = error_body.get("code", "")
                wait = 2 ** (attempt + 1)
                if error_code == "insufficient_quota":
                    if attempt == 2:
                        _set_fatal(f"Quota exceeded after 3 retries: {error_body.get('message', '')[:100]}")
                        return None
                    log(f"    Quota error, retrying in {wait}s (attempt {attempt+1}/3)")
                else:
                    log(f"    Rate limited, waiting {wait}s (attempt {attempt+1}/3)")
                time.sleep(wait)
                continue

            if resp.status_code >= 500:
                log(f"    API server error {resp.status_code} (attempt {attempt+1}/3)")
                time.sleep(2 ** (attempt + 1))
                continue

            if resp.status_code == 401:
                _set_fatal(f"Authentication failed (401): check API key")
                return None

            if resp.status_code == 403:
                _set_fatal(f"Access denied (403): {resp.text[:100]}")
                return None

            resp.raise_for_status()

            content = resp.json()["choices"][0]["message"]["content"]
            if not content:
                finish = resp.json()["choices"][0].get("finish_reason", "unknown")
                log(f"    Empty response (finish_reason={finish}), retrying...")
                time.sleep(1)
                continue

            result = json.loads(content)
            return result["label"], result["confidence"], result["reasoning"]

        except requests.exceptions.ConnectionError as e:
            log(f"    Connection error (attempt {attempt+1}/3): {str(e)[:80]}")
            if attempt == 2:
                return None
            time.sleep(2 ** (attempt + 1))

        except Exception as e:
            if attempt == 2:
                log(f"    Label failed after 3 attempts: {str(e)[:80]}")
                return None
            time.sleep(1)

    return None


# ── Parallel labeling ─────────────────────────────────────────────────────
def label_pairs_parallel(pairs_to_label, max_workers=GPT_WORKERS):
    """Label pairs in parallel. Each item: (title_a, desc_a, title_b, desc_b, product_name)"""
    session = requests.Session()
    session.headers.update({
        "Authorization": f"Bearer {OPENAI_API_KEY}",
        "Content-Type": "application/json",
    })

    results = [None] * len(pairs_to_label)
    completed = [0]
    total = len(pairs_to_label)

    def label_one(idx):
        a_title, a_desc, n_title, n_desc, product_name = pairs_to_label[idx]
        result = label_pair(a_title, a_desc, n_title, n_desc, product_name, session)
        results[idx] = result
        completed[0] += 1
        if completed[0] % 100 == 0 or completed[0] == total:
            log(f"    Labeled {completed[0]}/{total}")

    with ThreadPoolExecutor(max_workers=max_workers) as executor:
        futures = [executor.submit(label_one, i) for i in range(total)]
        for f in as_completed(futures):
            try:
                f.result()
            except Exception as e:
                log(f"    Thread error: {e}")

    return results


# ── Checkpoint management ─────────────────────────────────────────────────
def load_checkpoint():
    if CHECKPOINT_FILE.exists():
        with open(CHECKPOINT_FILE) as f:
            return json.load(f)
    return {"completed_jobs": [], "pairs": [], "seen_pairs": []}


def save_checkpoint(checkpoint):
    with open(CHECKPOINT_FILE, "w") as f:
        json.dump(checkpoint, f)


# ── CSV output ────────────────────────────────────────────────────────────
def save_csv(pairs):
    with open(OUTPUT_CSV, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=[
            "anchor_id", "neighbor_id", "job_id", "product_name",
            "anchor_title", "neighbor_title",
            "anchor_desc", "neighbor_desc",
            "label", "confidence", "reasoning",
        ])
        writer.writeheader()
        for p in pairs:
            writer.writerow(p)


# ── Main ──────────────────────────────────────────────────────────────────
def main():
    _init_log()
    log("=" * 60)
    log("V5 Data Collection — 25K Labeled Pairs")
    log(f"Log file: {LOG_FILE.name}")
    log("=" * 60)

    if not OPENAI_API_KEY or not PINECONE_API_KEY:
        log("ERROR: Missing API keys. Set in local.settings.json or env vars.")
        sys.exit(1)

    # Load products from DB
    products = load_products_from_db()
    log(f"Found {len(products)} enabled scrape jobs")

    # Load listings
    listings = load_listings()
    log(f"Loaded {len(listings)} listings")

    # Group listings by job
    by_job = {}
    for l in listings.values():
        jid = l["scrapeJobId"]
        if jid in products:
            by_job.setdefault(jid, []).append(l)

    # Filter jobs with enough listings
    valid_jobs = {jid: ls for jid, ls in by_job.items() if len(ls) >= 10}
    log(f"Valid categories: {len(valid_jobs)} (>= 10 listings each)")

    # Calculate anchors per job
    pairs_per_job = TARGET_PAIRS // len(valid_jobs)
    anchors_needed = pairs_per_job // NEIGHBORS_PER_ANCHOR

    log(f"Target: {TARGET_PAIRS} pairs across {len(valid_jobs)} categories")
    log(f"  ~{pairs_per_job} pairs/category, ~{anchors_needed} anchors/category")
    log(f"  Pinecone top_k={PINECONE_TOP_K}, sample {NEIGHBORS_PER_ANCHOR}/anchor")
    log(f"  GPT model: {GPT_MODEL}, workers: {GPT_WORKERS}")

    # Load checkpoint
    checkpoint = load_checkpoint()
    completed_jobs = set(checkpoint["completed_jobs"])
    all_pairs = checkpoint["pairs"]
    seen_pairs = set(tuple(sp) for sp in checkpoint["seen_pairs"])

    if completed_jobs:
        log(f"Resuming: {len(completed_jobs)} jobs done, {len(all_pairs)} pairs, {len(seen_pairs)} seen")

    remaining_jobs = [jid for jid in sorted(valid_jobs.keys()) if jid not in completed_jobs]
    log(f"Jobs remaining: {len(remaining_jobs)}")

    for job_idx, job_id in enumerate(remaining_jobs):
        product_name = products[job_id]
        job_listings = valid_jobs[job_id]
        n_anchors = min(anchors_needed, int(len(job_listings) * ANCHOR_CAP_PCT))

        log(f"\n[{len(completed_jobs)+1}/{len(valid_jobs)}] {product_name} (job {job_id}, {len(job_listings)} listings, {n_anchors} anchors)")

        # Pick anchors
        anchors = pick_anchors(listings, job_id, n_anchors)
        if not anchors:
            log(f"  Skipping — not enough listings")
            completed_jobs.add(job_id)
            checkpoint["completed_jobs"] = list(completed_jobs)
            save_checkpoint(checkpoint)
            continue

        # Fetch anchor vectors from Pinecone
        anchor_ids = [a["listingId"] for a in anchors]
        log(f"  Fetching {len(anchor_ids)} anchor vectors from Pinecone...")
        anchor_vectors = pinecone_fetch(anchor_ids)
        found = len(anchor_vectors)
        if found < len(anchor_ids):
            log(f"  Warning: only {found}/{len(anchor_ids)} anchors found in Pinecone")

        # Build pairs: query Pinecone, filter seen, sample
        pairs_to_label = []   # (title_a, desc_a, title_b, desc_b, product_name)
        pairs_meta = []       # (anchor_id, neighbor_id, job_id, product_name)
        skipped_seen = 0

        for anchor in anchors:
            aid = anchor["listingId"]
            if aid not in anchor_vectors:
                continue

            vec = anchor_vectors[aid]["values"]
            matches = pinecone_query(vec, top_k=PINECONE_TOP_K, job_id=job_id)

            # Filter: remove self, remove seen pairs
            fresh = []
            for m in matches:
                nid = m["id"]
                if nid == aid:
                    continue
                pair_key = tuple(sorted([aid, nid]))
                if pair_key in seen_pairs:
                    skipped_seen += 1
                    continue
                fresh.append(m)

            # Sample up to NEIGHBORS_PER_ANCHOR
            sampled = random.sample(fresh, min(NEIGHBORS_PER_ANCHOR, len(fresh)))

            for m in sampled:
                nid = m["id"]
                pair_key = tuple(sorted([aid, nid]))
                seen_pairs.add(pair_key)

                neighbor = listings.get(nid, {})
                n_title = neighbor.get("title", "")
                n_desc = neighbor.get("description", "")
                if not n_title:
                    n_title = m.get("metadata", {}).get("title", nid)

                pairs_to_label.append((
                    anchor["title"], anchor["description"],
                    n_title, n_desc,
                    product_name,
                ))
                pairs_meta.append((aid, nid, job_id, product_name))

            time.sleep(PINECONE_SLEEP)

        log(f"  {len(pairs_to_label)} pairs to label ({skipped_seen} skipped as seen)")

        if not pairs_to_label:
            completed_jobs.add(job_id)
            checkpoint["completed_jobs"] = list(completed_jobs)
            checkpoint["seen_pairs"] = [list(sp) for sp in seen_pairs]
            save_checkpoint(checkpoint)
            continue

        # Label in parallel
        log(f"  Labeling {len(pairs_to_label)} pairs ({GPT_WORKERS} workers)...")
        label_results = label_pairs_parallel(pairs_to_label)

        # Check for fatal API errors
        if _is_fatal():
            log(f"  ABORTING: {_fatal_error}")
            log("  Saving checkpoint before exit...")
            checkpoint["completed_jobs"] = list(completed_jobs)
            checkpoint["seen_pairs"] = [list(sp) for sp in seen_pairs]
            save_checkpoint(checkpoint)
            if all_pairs:
                save_csv(all_pairs)
                with open(OUTPUT_JSON, "w") as f:
                    json.dump(all_pairs, f, indent=2)
            log(f"  Saved {len(all_pairs)} pairs. Fix the issue and re-run to resume.")
            sys.exit(1)

        # Collect results
        job_pairs = []
        failures = 0
        for (a_title, a_desc, n_title, n_desc, _), (aid, nid, jid, pname), result in zip(
            pairs_to_label, pairs_meta, label_results
        ):
            if result is None:
                failures += 1
                continue
            label, confidence, reasoning = result
            job_pairs.append({
                "anchor_id": aid,
                "neighbor_id": nid,
                "job_id": jid,
                "product_name": pname,
                "anchor_title": a_title,
                "neighbor_title": n_title,
                "anchor_desc": a_desc or "",
                "neighbor_desc": n_desc or "",
                "label": label,
                "confidence": confidence,
                "reasoning": reasoning[:200],  # truncate for storage
            })

        all_pairs.extend(job_pairs)

        # Stats
        comps = sum(1 for p in job_pairs if p["label"] == 1)
        high = sum(1 for p in job_pairs if p["confidence"] == "high")
        fail_rate = failures / max(len(pairs_to_label), 1) * 100
        log(f"  Results: {len(job_pairs)} labeled ({failures} failed, {fail_rate:.0f}% failure rate)")
        log(f"    Same: {comps} ({comps/max(len(job_pairs),1)*100:.0f}%) | Different: {len(job_pairs)-comps}")
        log(f"    High conf: {high} ({high/max(len(job_pairs),1)*100:.0f}%)")
        log(f"  Running total: {len(all_pairs)} pairs")

        if fail_rate > 50 and len(pairs_to_label) > 10:
            log(f"  WARNING: High failure rate ({fail_rate:.0f}%). Check API status/billing.")

        # Checkpoint
        completed_jobs.add(job_id)
        checkpoint["completed_jobs"] = list(completed_jobs)
        checkpoint["pairs"] = all_pairs
        checkpoint["seen_pairs"] = [list(sp) for sp in seen_pairs]
        save_checkpoint(checkpoint)

        # Also save incremental CSV/JSON every job
        save_csv(all_pairs)
        with open(OUTPUT_JSON, "w") as f:
            json.dump(all_pairs, f, indent=2)

    # ── Final summary ─────────────────────────────────────────────────────
    log(f"\n{'='*60}")
    log("COLLECTION COMPLETE")
    log(f"{'='*60}")
    log(f"Total pairs: {len(all_pairs)}")

    if all_pairs:
        total_comps = sum(1 for p in all_pairs if p["label"] == 1)
        total_high = sum(1 for p in all_pairs if p["confidence"] == "high")
        log(f"  Same variant: {total_comps} ({total_comps/len(all_pairs)*100:.1f}%)")
        log(f"  Different: {len(all_pairs)-total_comps} ({(len(all_pairs)-total_comps)/len(all_pairs)*100:.1f}%)")
        log(f"  High confidence: {total_high} ({total_high/len(all_pairs)*100:.1f}%)")

        # Per-job breakdown
        from collections import Counter
        job_counts = Counter(p["job_id"] for p in all_pairs)
        log(f"\nPer-category breakdown:")
        for jid in sorted(job_counts.keys()):
            job_pairs = [p for p in all_pairs if p["job_id"] == jid]
            comps = sum(1 for p in job_pairs if p["label"] == 1)
            name = products.get(jid, str(jid))
            log(f"  {name}: {len(job_pairs)} pairs ({comps} same, {len(job_pairs)-comps} diff)")

    save_csv(all_pairs)
    with open(OUTPUT_JSON, "w") as f:
        json.dump(all_pairs, f, indent=2)
    log(f"\nSaved: {OUTPUT_CSV.name} and {OUTPUT_JSON.name}")
    log(f"Checkpoint: {CHECKPOINT_FILE.name} (delete to start fresh)")


if __name__ == "__main__":
    random.seed(42)
    np.random.seed(42)
    main()
