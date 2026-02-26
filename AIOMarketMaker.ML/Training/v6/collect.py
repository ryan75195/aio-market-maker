"""
V6 Full Collection: 200K labeled pairs using banded hard-negative strategy.

Builds on existing 24,260 v5 pairs. Generates ~175K new pairs using:
  1. Pinecone banded (same category) — 3 similarity bands for difficulty control
  2. Pinecone cross-category — teaches cross-product discrimination
  3. Same-category random — catches same-brand-different-model
  4. Description-overlap — template seller defense

Supports resume via checkpoints. Run after validating quality with v6_pilot.

Usage:
  py -3.12 collect_v6.py
  py -3.12 collect_v6.py --target 50000    # smaller run
  py -3.12 collect_v6.py --resume          # resume from checkpoint
"""

import argparse
import csv
import json
import os
import random
import subprocess
import sys
import io
import time
import requests
from collections import defaultdict
from datetime import datetime
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor, as_completed
from threading import Lock

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

# ── Config ────────────────────────────────────────────────────────────────
DATA_DIR = Path(__file__).parent
V5_CSV = DATA_DIR / "labeled_pairs_v5.csv"
LISTINGS_CACHE = DATA_DIR / "listings_v5_cache.csv"
CHECKPOINT_FILE = DATA_DIR / "collect_v6_checkpoint.json"
OUTPUT_CSV = DATA_DIR / "labeled_pairs_v6.csv"
OUTPUT_JSON = DATA_DIR / "labeled_pairs_v6.json"
MERGED_CSV = DATA_DIR / "labeled_pairs_v6_merged.csv"

PINECONE_HOST = "https://arbitrage-d207f30.svc.aped-4627-b74a.pinecone.io"

GPT_MODEL = "gpt-5-mini"
GPT_MAX_TOKENS = 2000
GPT_WORKERS = 75
PINECONE_WORKERS = 20

# GPT-5-mini pricing (per million tokens)
PRICE_INPUT_PER_M = 0.25
PRICE_OUTPUT_PER_M = 2.00

DEFAULT_TARGET = 175000  # new pairs to generate (+ 24K v5 = ~200K total)

# Pair source ratios
RATIO_BANDED = 0.60
RATIO_CROSS_CAT = 0.15
RATIO_RANDOM = 0.15
RATIO_DESC_OVERLAP = 0.10

# Weak categories get 2x anchors
WEAK_CATEGORIES = {
    "Fender Stratocaster Guitar",
    "Nike Air Jordan 1",
    "Funko Pop Chase",
    "Nintendo Switch OLED",
    "PlayStation 5 Console",
    "Callaway Paradym Driver",
    "Canada Goose Parka",
    "Peloton Bike",
}

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

# ── Logging ───────────────────────────────────────────────────────────────
_log_lock = Lock()
_log_file = None

def _init_log():
    global _log_file
    log_path = DATA_DIR / f"collect_v6_{datetime.now().strftime('%Y%m%d_%H%M%S')}.log"
    _log_file = open(log_path, "w", encoding="utf-8")
    log(f"Log file: {log_path.name}")

def log(msg):
    ts = datetime.now().strftime("%H:%M:%S")
    line = f"[{ts}] {msg}"
    with _log_lock:
        print(line, flush=True)
        if _log_file:
            _log_file.write(line + "\n")
            _log_file.flush()


# ── Cost & progress tracking ──────────────────────────────────────────
class CostTracker:
    def __init__(self, total_gpt_calls):
        self._lock = Lock()
        self.input_tokens = 0
        self.output_tokens = 0
        self.calls_completed = 0
        self.total_gpt_calls = total_gpt_calls
        self.start_time = None

    def start(self):
        self.start_time = time.time()

    def add(self, input_tok, output_tok):
        with self._lock:
            self.input_tokens += input_tok
            self.output_tokens += output_tok
            self.calls_completed += 1

    def cost_so_far(self):
        with self._lock:
            return (self.input_tokens * PRICE_INPUT_PER_M / 1_000_000 +
                    self.output_tokens * PRICE_OUTPUT_PER_M / 1_000_000)

    def progress_str(self):
        with self._lock:
            pct = self.calls_completed / max(self.total_gpt_calls, 1) * 100
            cost = (self.input_tokens * PRICE_INPUT_PER_M / 1_000_000 +
                    self.output_tokens * PRICE_OUTPUT_PER_M / 1_000_000)
            if self.calls_completed == 0:
                return f"{self.calls_completed}/{self.total_gpt_calls} (0.0%) | $0.00/est $0.00 | ETA: calculating..."
            avg_input = self.input_tokens / self.calls_completed
            avg_output = self.output_tokens / self.calls_completed
            remaining = self.total_gpt_calls - self.calls_completed
            est_total = cost + (remaining * avg_input * PRICE_INPUT_PER_M / 1_000_000 +
                                remaining * avg_output * PRICE_OUTPUT_PER_M / 1_000_000)
            elapsed = time.time() - self.start_time if self.start_time else 0
            rate = self.calls_completed / max(elapsed, 1)
            eta_secs = remaining / max(rate, 0.001)
            if eta_secs < 60:
                eta = f"{eta_secs:.0f}s"
            elif eta_secs < 3600:
                eta = f"{eta_secs / 60:.1f}m"
            else:
                eta = f"{eta_secs / 3600:.1f}h"
            return f"{self.calls_completed}/{self.total_gpt_calls} ({pct:.1f}%) | ${cost:.2f}/~${est_total:.2f} | ETA: {eta}"


cost_tracker = None  # initialized in main()


# ── Load API keys ─────────────────────────────────────────────────────────
def load_api_keys():
    for path in [
        DATA_DIR / ".." / ".." / "AIOMarketMaker.Api" / "bin" / "Debug" / "net8.0" / "local.settings.json",
        DATA_DIR / ".." / ".." / "AIOMarketMaker.Console" / "local.settings.json",
    ]:
        try:
            with open(path) as f:
                settings = json.load(f)
                return settings["Values"].get("OpenAi:ApiKey", ""), settings["Values"].get("Pinecone:ApiKey", "")
        except (FileNotFoundError, KeyError):
            continue
    return os.environ.get("OPENAI_API_KEY", ""), os.environ.get("PINECONE_API_KEY", "")


OPENAI_API_KEY, PINECONE_API_KEY = load_api_keys()

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


# ── Load existing v5 pairs for dedup ──────────────────────────────────────
def load_v5_seen_pairs():
    seen = set()
    if not V5_CSV.exists():
        return seen
    with open(V5_CSV, "r", encoding="utf-8", errors="replace") as f:
        reader = csv.DictReader(f)
        for row in reader:
            pair_key = tuple(sorted([row["anchor_id"], row["neighbor_id"]]))
            seen.add(pair_key)
    log(f"Loaded {len(seen)} existing v5 pairs for dedup")
    return seen


# ── Checkpoint management ─────────────────────────────────────────────────
def load_checkpoint():
    if CHECKPOINT_FILE.exists():
        with open(CHECKPOINT_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    return {
        "completed_sources": [],
        "pairs": [],
        "seen_pairs": [],
        "unlabeled_pairs": [],
    }


def save_checkpoint(checkpoint):
    with open(CHECKPOINT_FILE, "w", encoding="utf-8") as f:
        json.dump(checkpoint, f)


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
    return all_vectors


def pinecone_query(vector, top_k=100, job_id=None):
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


_fatal_error = None
_fatal_lock = Lock()

def _set_fatal(msg):
    global _fatal_error
    with _fatal_lock:
        if _fatal_error is None:
            _fatal_error = msg
            log(f"  FATAL: {msg}")

def _is_fatal():
    with _fatal_lock:
        return _fatal_error is not None


def label_pair(title_a, desc_a, title_b, desc_b, product_name, session):
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
                        _set_fatal(f"Quota exceeded: {error_body.get('message', '')[:100]}")
                        return None
                    log(f"    Quota error, retrying in {wait}s")
                else:
                    log(f"    Rate limited, waiting {wait}s")
                time.sleep(wait)
                continue

            if resp.status_code >= 500:
                time.sleep(2 ** (attempt + 1))
                continue

            resp.raise_for_status()
            resp_json = resp.json()
            content = resp_json["choices"][0]["message"]["content"]
            if not content:
                time.sleep(1)
                continue

            usage = resp_json.get("usage", {})
            if cost_tracker and usage:
                cost_tracker.add(usage.get("prompt_tokens", 0), usage.get("completion_tokens", 0))

            result = json.loads(content)
            return result["label"], result["confidence"], result["reasoning"]

        except requests.exceptions.ConnectionError as e:
            if attempt == 2:
                return None
            time.sleep(2 ** (attempt + 1))
        except Exception as e:
            if attempt == 2:
                log(f"    Label failed: {str(e)[:80]}")
                return None
            time.sleep(1)

    return None


def label_batch(pairs, batch_name):
    """Label a batch of pairs in parallel. Modifies pairs in place."""
    session = requests.Session()
    session.headers.update({
        "Authorization": f"Bearer {OPENAI_API_KEY}",
        "Content-Type": "application/json",
    })

    to_label = [(i, p) for i, p in enumerate(pairs) if p.get("label") is None]
    if not to_label:
        log(f"  [{batch_name}] All {len(pairs)} pairs already labeled")
        return 0

    log(f"  [{batch_name}] Labeling {len(to_label)} pairs ({GPT_WORKERS} workers)...")
    completed = [0]
    total = len(to_label)
    failures = [0]

    def label_one(item):
        idx, p = item
        result = label_pair(
            p["anchor_title"], p["anchor_desc"],
            p["neighbor_title"], p["neighbor_desc"],
            p["product_name"], session,
        )
        if result is None:
            failures[0] += 1
        else:
            label, confidence, reasoning = result
            pairs[idx]["label"] = label
            pairs[idx]["confidence"] = confidence
            pairs[idx]["reasoning"] = reasoning[:200]
        completed[0] += 1
        if completed[0] % 500 == 0 or completed[0] == total:
            log(f"    [{batch_name}] {completed[0]}/{total} | {cost_tracker.progress_str()}")

    with ThreadPoolExecutor(max_workers=GPT_WORKERS) as executor:
        futures = [executor.submit(label_one, item) for item in to_label]
        for f in as_completed(futures):
            try:
                f.result()
            except Exception as e:
                log(f"    Thread error: {e}")

    labeled = sum(1 for p in pairs if p.get("label") is not None)
    log(f"  [{batch_name}] Done: {labeled}/{len(pairs)} labeled, {failures[0]} failures")
    return failures[0]


# ══════════════════════════════════════════════════════════════════════════
# PAIR GENERATION STRATEGIES (same as pilot)
# ══════════════════════════════════════════════════════════════════════════

def generate_banded_pairs(listings, products, valid_jobs, seen_pairs, target):
    """Source 1: Pinecone same-category pairs sampled by similarity band."""
    log(f"Generating {target} banded pairs...")

    pairs = []
    base_per_job = max(10, target // len(valid_jobs))

    for jid, job_listings in valid_jobs.items():
        product_name = products[jid]
        job_target = base_per_job * 2 if product_name in WEAK_CATEGORIES else base_per_job

        random.shuffle(job_listings)
        max_anchors = min(100, len(job_listings) // 2)
        anchors = job_listings[:max_anchors]

        anchor_ids = [a["listingId"] for a in anchors]
        anchor_vectors = pinecone_fetch(anchor_ids)

        # Parallel query for all anchors
        valid_anchors = [(a, anchor_vectors[a["listingId"]]["values"])
                         for a in anchors if a["listingId"] in anchor_vectors]

        def query_one(item):
            anchor, vec = item
            return anchor, pinecone_query(vec, top_k=100, job_id=jid)

        with ThreadPoolExecutor(max_workers=PINECONE_WORKERS) as executor:
            results = list(executor.map(query_one, valid_anchors))

        # Sequential pair creation (dedup needs thread safety)
        job_pairs = 0
        for anchor, matches in results:
            if job_pairs >= job_target:
                break
            aid = anchor["listingId"]

            fresh = []
            for m in matches:
                if m["id"] == aid:
                    continue
                pair_key = tuple(sorted([aid, m["id"]]))
                if pair_key in seen_pairs:
                    continue
                if m["id"] not in listings:
                    continue
                fresh.append(m)

            band_high = [m for m in fresh if m["score"] >= 0.90]
            band_mid = [m for m in fresh if 0.80 <= m["score"] < 0.90]
            band_low = [m for m in fresh if 0.70 <= m["score"] < 0.80]

            sampled = []
            for band, count in [(band_high, 2), (band_mid, 2), (band_low, 2)]:
                sampled.extend(random.sample(band, min(count, len(band))))

            for m in sampled:
                if job_pairs >= job_target:
                    break
                nid = m["id"]
                pair_key = tuple(sorted([aid, nid]))
                seen_pairs.add(pair_key)
                neighbor = listings[nid]
                pairs.append({
                    "anchor_id": aid,
                    "neighbor_id": nid,
                    "job_id": jid,
                    "product_name": product_name,
                    "anchor_title": anchor["title"],
                    "neighbor_title": neighbor["title"],
                    "anchor_desc": anchor["description"],
                    "neighbor_desc": neighbor["description"],
                    "source": "banded",
                    "label": None, "confidence": None, "reasoning": None,
                })
                job_pairs += 1

        if job_pairs > 0:
            log(f"  {product_name}: {job_pairs} banded pairs")

    random.shuffle(pairs)
    return pairs[:target]


def generate_cross_category_pairs(listings, products, valid_jobs, seen_pairs, target):
    """Source 2: Pinecone query without job filter — different-category matches."""
    log(f"Generating {target} cross-category pairs...")

    pairs = []
    pairs_per_job = max(5, target // len(valid_jobs))

    for jid, job_listings in valid_jobs.items():
        product_name = products[jid]
        random.shuffle(job_listings)
        anchors = job_listings[:min(15, len(job_listings) // 3)]
        anchor_ids = [a["listingId"] for a in anchors]
        anchor_vectors = pinecone_fetch(anchor_ids)

        valid_anchors = [(a, anchor_vectors[a["listingId"]]["values"])
                         for a in anchors if a["listingId"] in anchor_vectors]

        def query_one(item):
            anchor, vec = item
            return anchor, pinecone_query(vec, top_k=50)

        with ThreadPoolExecutor(max_workers=PINECONE_WORKERS) as executor:
            results = list(executor.map(query_one, valid_anchors))

        job_pairs = 0
        for anchor, matches in results:
            if job_pairs >= pairs_per_job:
                break
            aid = anchor["listingId"]

            cross = []
            for m in matches:
                if m["id"] == aid:
                    continue
                neighbor = listings.get(m["id"])
                if not neighbor:
                    continue
                if neighbor["scrapeJobId"] == jid:
                    continue
                pair_key = tuple(sorted([aid, m["id"]]))
                if pair_key in seen_pairs:
                    continue
                cross.append((m, neighbor))

            sampled = random.sample(cross, min(3, len(cross)))

            for m, neighbor in sampled:
                if job_pairs >= pairs_per_job:
                    break
                pair_key = tuple(sorted([aid, m["id"]]))
                seen_pairs.add(pair_key)
                pairs.append({
                    "anchor_id": aid,
                    "neighbor_id": neighbor["listingId"],
                    "job_id": jid,
                    "product_name": product_name,
                    "anchor_title": anchor["title"],
                    "neighbor_title": neighbor["title"],
                    "anchor_desc": anchor["description"],
                    "neighbor_desc": neighbor["description"],
                    "source": "cross_category",
                    "label": 0, "confidence": "high", "reasoning": "Different product categories",
                })
                job_pairs += 1

        if job_pairs > 0:
            log(f"  {product_name}: {job_pairs} cross-category pairs")

    random.shuffle(pairs)
    return pairs[:target]


def generate_random_same_category_pairs(listings, products, valid_jobs, seen_pairs, target):
    """Source 3: Random pairs from same category — same-brand-different-model."""
    log(f"Generating {target} random same-category pairs...")

    pairs = []
    pairs_per_job = max(5, target // len(valid_jobs))

    for jid, job_listings in valid_jobs.items():
        if len(job_listings) < 5:
            continue
        product_name = products[jid]
        job_pairs = 0

        for _ in range(pairs_per_job * 5):
            if job_pairs >= pairs_per_job:
                break
            a, b = random.sample(job_listings, 2)
            pair_key = tuple(sorted([a["listingId"], b["listingId"]]))
            if pair_key in seen_pairs:
                continue
            seen_pairs.add(pair_key)
            pairs.append({
                "anchor_id": a["listingId"],
                "neighbor_id": b["listingId"],
                "job_id": jid,
                "product_name": product_name,
                "anchor_title": a["title"],
                "neighbor_title": b["title"],
                "anchor_desc": a["description"],
                "neighbor_desc": b["description"],
                "source": "random_same_cat",
                "label": None, "confidence": None, "reasoning": None,
            })
            job_pairs += 1

        if job_pairs > 0:
            log(f"  {product_name}: {job_pairs} random pairs")

    random.shuffle(pairs)
    return pairs[:target]


def generate_description_overlap_pairs(listings, products, valid_jobs, seen_pairs, target):
    """Source 4: Near-identical descriptions across different categories."""
    log(f"Generating {target} description-overlap pairs...")

    desc_groups = defaultdict(list)
    for lid, l in listings.items():
        if not l["description"] or len(l["description"]) < 80:
            continue
        key = l["description"][:80].lower().strip()
        desc_groups[key].append(l)

    # Cross-category groups
    pairs = []
    for key, group in desc_groups.items():
        if len(pairs) >= target:
            break
        job_ids = set(l["scrapeJobId"] for l in group)
        if len(job_ids) < 2 or len(group) < 2:
            continue

        by_job = defaultdict(list)
        for l in group:
            by_job[l["scrapeJobId"]].append(l)

        job_id_list = list(by_job.keys())
        for i in range(len(job_id_list)):
            for j in range(i + 1, len(job_id_list)):
                if len(pairs) >= target:
                    break
                a = random.choice(by_job[job_id_list[i]])
                b = random.choice(by_job[job_id_list[j]])
                pair_key = tuple(sorted([a["listingId"], b["listingId"]]))
                if pair_key in seen_pairs:
                    continue
                seen_pairs.add(pair_key)
                pairs.append({
                    "anchor_id": a["listingId"],
                    "neighbor_id": b["listingId"],
                    "job_id": a["scrapeJobId"],
                    "product_name": products.get(a["scrapeJobId"], "unknown"),
                    "anchor_title": a["title"],
                    "neighbor_title": b["title"],
                    "anchor_desc": a["description"],
                    "neighbor_desc": b["description"],
                    "source": "desc_overlap",
                    "label": 0, "confidence": "high", "reasoning": "Different product categories, shared description template",
                })

    # Same-category, overlapping descriptions, different titles
    # Cap per category to prevent skew
    cat_counts = defaultdict(int)
    max_per_cat = max(10, target // max(len(valid_jobs), 1))

    for key, group in desc_groups.items():
        if len(pairs) >= target:
            break
        if len(group) < 2:
            continue
        by_job = defaultdict(list)
        for l in group:
            by_job[l["scrapeJobId"]].append(l)

        for jid, same_cat in by_job.items():
            if len(same_cat) < 2 or len(pairs) >= target:
                continue
            if cat_counts[jid] >= max_per_cat:
                continue
            for k in range(min(3, len(same_cat))):
                for m in range(k + 1, min(4, len(same_cat))):
                    a, b = same_cat[k], same_cat[m]
                    if a["title"][:30].lower() == b["title"][:30].lower():
                        continue
                    pair_key = tuple(sorted([a["listingId"], b["listingId"]]))
                    if pair_key in seen_pairs:
                        continue
                    seen_pairs.add(pair_key)
                    cat_counts[jid] += 1
                    pairs.append({
                        "anchor_id": a["listingId"],
                        "neighbor_id": b["listingId"],
                        "job_id": jid,
                        "product_name": products.get(jid, "unknown"),
                        "anchor_title": a["title"],
                        "neighbor_title": b["title"],
                        "anchor_desc": a["description"],
                        "neighbor_desc": b["description"],
                        "source": "desc_overlap",
                        "label": None, "confidence": None, "reasoning": None,
                    })
                    if len(pairs) >= target or cat_counts[jid] >= max_per_cat:
                        break

    random.shuffle(pairs)
    log(f"  Generated {len(pairs)} description-overlap pairs")
    return pairs[:target]


# ── CSV output ────────────────────────────────────────────────────────────
CSV_FIELDS = [
    "anchor_id", "neighbor_id", "job_id", "product_name",
    "anchor_title", "neighbor_title",
    "anchor_desc", "neighbor_desc",
    "label", "confidence", "reasoning", "source",
]

def save_pairs_csv(pairs, path):
    labeled = [p for p in pairs if p.get("label") is not None]
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=CSV_FIELDS, extrasaction="ignore")
        writer.writeheader()
        for p in labeled:
            writer.writerow(p)
    return len(labeled)


def merge_with_v5(v6_pairs):
    """Merge v6 pairs with existing v5 pairs into a single training file."""
    v5_pairs = []
    if V5_CSV.exists():
        with open(V5_CSV, "r", encoding="utf-8", errors="replace") as f:
            reader = csv.DictReader(f)
            for row in reader:
                row["source"] = "v5_original"
                v5_pairs.append(row)

    v6_labeled = [p for p in v6_pairs if p.get("label") is not None]

    all_pairs = v5_pairs + v6_labeled
    count = save_pairs_csv(all_pairs, MERGED_CSV)
    return count, len(v5_pairs), len(v6_labeled)


# ── Main ──────────────────────────────────────────────────────────────────
def main():
    parser = argparse.ArgumentParser(description="V6 full collection: 200K labeled pairs")
    parser.add_argument("--target", type=int, default=DEFAULT_TARGET, help="New pairs to generate")
    parser.add_argument("--resume", action="store_true", help="Resume from checkpoint")
    parser.add_argument("--seed", type=int, default=42, help="Random seed")
    parser.add_argument("--batch-size", type=int, default=5000, help="Label in batches of N (for checkpointing)")
    args = parser.parse_args()

    random.seed(args.seed)
    _init_log()

    log("=" * 70)
    log(f"V6 Full Collection — {args.target} new pairs + 24K v5 = ~{args.target + 24260} total")
    log(f"Model: {GPT_MODEL} | Workers: {GPT_WORKERS} | Batch size: {args.batch_size}")
    log("=" * 70)

    if not OPENAI_API_KEY or not PINECONE_API_KEY:
        log("ERROR: Missing API keys")
        sys.exit(1)

    # Load data
    products = load_products_from_db()
    log(f"Found {len(products)} enabled scrape jobs")

    listings = load_listings()
    log(f"Loaded {len(listings)} listings")

    seen_pairs = load_v5_seen_pairs()

    # Group by job
    by_job = {}
    for l in listings.values():
        jid = l["scrapeJobId"]
        if jid in products:
            by_job.setdefault(jid, []).append(l)

    valid_jobs = {jid: ls for jid, ls in by_job.items() if len(ls) >= 10}
    log(f"Valid categories: {len(valid_jobs)}")

    # ── Resume or generate pairs ──────────────────────────────────────────
    checkpoint = load_checkpoint() if args.resume else {
        "completed_sources": [],
        "pairs": [],
        "seen_pairs": [],
        "unlabeled_pairs": [],
    }

    if args.resume and checkpoint["pairs"]:
        all_new_pairs = checkpoint["pairs"]
        seen_pairs.update(tuple(sp) for sp in checkpoint["seen_pairs"])
        log(f"Resumed: {len(all_new_pairs)} pairs from checkpoint, {len(seen_pairs)} seen")
    else:
        # Calculate targets per source
        t_banded = int(args.target * RATIO_BANDED)
        t_cross = int(args.target * RATIO_CROSS_CAT)
        t_random = int(args.target * RATIO_RANDOM)
        t_desc = args.target - t_banded - t_cross - t_random

        log(f"\nTargets: banded={t_banded}, cross={t_cross}, random={t_random}, desc={t_desc}")

        all_new_pairs = []

        # Generate each source
        if "banded" not in checkpoint["completed_sources"]:
            banded = generate_banded_pairs(listings, products, valid_jobs, seen_pairs, t_banded)
            all_new_pairs.extend(banded)
            log(f"Banded: {len(banded)} pairs")
            checkpoint["completed_sources"].append("banded")

        if "cross_category" not in checkpoint["completed_sources"]:
            cross = generate_cross_category_pairs(listings, products, valid_jobs, seen_pairs, t_cross)
            all_new_pairs.extend(cross)
            log(f"Cross-category: {len(cross)} pairs")
            checkpoint["completed_sources"].append("cross_category")

        if "random_same_cat" not in checkpoint["completed_sources"]:
            rand_pairs = generate_random_same_category_pairs(listings, products, valid_jobs, seen_pairs, t_random)
            all_new_pairs.extend(rand_pairs)
            log(f"Random same-category: {len(rand_pairs)} pairs")
            checkpoint["completed_sources"].append("random_same_cat")

        if "desc_overlap" not in checkpoint["completed_sources"]:
            desc = generate_description_overlap_pairs(listings, products, valid_jobs, seen_pairs, t_desc)
            all_new_pairs.extend(desc)
            log(f"Description-overlap: {len(desc)} pairs")
            checkpoint["completed_sources"].append("desc_overlap")

        # Save checkpoint after pair generation
        checkpoint["pairs"] = all_new_pairs
        checkpoint["seen_pairs"] = [list(sp) for sp in seen_pairs]
        save_checkpoint(checkpoint)

    auto_labeled = sum(1 for p in all_new_pairs if p.get("label") is not None)
    log(f"\nTotal new pairs: {len(all_new_pairs)} ({auto_labeled} auto-labeled, {len(all_new_pairs) - auto_labeled} need GPT)")

    # ── Label in batches with checkpointing ───────────────────────────────
    unlabeled = sum(1 for p in all_new_pairs if p.get("label") is None)
    log(f"Unlabeled: {unlabeled}/{len(all_new_pairs)}")

    global cost_tracker
    cost_tracker = CostTracker(total_gpt_calls=unlabeled)
    cost_tracker.start()

    batch_num = 0
    total_failures = 0

    while unlabeled > 0:
        if _is_fatal():
            log(f"Fatal error — saving checkpoint and exiting")
            break

        batch_num += 1
        # Find next unlabeled batch
        batch_indices = [i for i, p in enumerate(all_new_pairs) if p.get("label") is None][:args.batch_size]
        batch = [all_new_pairs[i] for i in batch_indices]

        log(f"\n--- Batch {batch_num}: {len(batch)} pairs (remaining: {unlabeled}) ---")
        failures = label_batch(batch, f"batch_{batch_num}")
        total_failures += failures

        # Update the main list
        for idx, bi in enumerate(batch_indices):
            all_new_pairs[bi] = batch[idx]

        # Save checkpoint
        checkpoint["pairs"] = all_new_pairs
        save_checkpoint(checkpoint)

        # Save incremental CSV
        saved = save_pairs_csv(all_new_pairs, OUTPUT_CSV)
        log(f"  Checkpoint saved: {saved} labeled pairs in {OUTPUT_CSV.name}")

        unlabeled = sum(1 for p in all_new_pairs if p.get("label") is None)

        # Progress summary
        labeled = len(all_new_pairs) - unlabeled
        same = sum(1 for p in all_new_pairs if p.get("label") == 1)
        diff = sum(1 for p in all_new_pairs if p.get("label") == 0)
        high = sum(1 for p in all_new_pairs if p.get("confidence") == "high")
        log(f"  Progress: {labeled}/{len(all_new_pairs)} labeled | same={same} diff={diff} | high_conf={high} | failures={total_failures}")

    # ── Final output ──────────────────────────────────────────────────────
    log(f"\n{'=' * 70}")
    log("FINAL SUMMARY")
    log(f"{'=' * 70}")

    labeled_count = save_pairs_csv(all_new_pairs, OUTPUT_CSV)
    log(f"New v6 pairs: {labeled_count} saved to {OUTPUT_CSV.name}")

    total, v5_count, v6_count = merge_with_v5(all_new_pairs)
    log(f"Merged dataset: {total} total ({v5_count} v5 + {v6_count} v6) saved to {MERGED_CSV.name}")

    # Source breakdown
    by_source = defaultdict(list)
    for p in all_new_pairs:
        if p.get("label") is not None:
            by_source[p.get("source", "?")].append(p)

    log(f"\nBy source:")
    for source, source_pairs in sorted(by_source.items()):
        n = len(source_pairs)
        same = sum(1 for p in source_pairs if p["label"] == 1)
        high = sum(1 for p in source_pairs if p["confidence"] == "high")
        log(f"  {source:<20} {n:>6} pairs | {same/n:.0%} same | {high/n:.0%} high conf")

    # Category breakdown
    by_cat = defaultdict(int)
    for p in all_new_pairs:
        if p.get("label") is not None:
            by_cat[p["product_name"]] += 1

    log(f"\nBy category ({len(by_cat)} categories):")
    for cat in sorted(by_cat.keys()):
        weak = " [WEAK - 2x]" if cat in WEAK_CATEGORIES else ""
        log(f"  {cat:<35} {by_cat[cat]:>5} pairs{weak}")

    log(f"\nTotal failures: {total_failures}")
    if cost_tracker and cost_tracker.start_time:
        elapsed = time.time() - cost_tracker.start_time
        log(f"Total cost: ${cost_tracker.cost_so_far():.2f} | {elapsed / 60:.1f} minutes | {cost_tracker.calls_completed} GPT calls")
    log(f"\nDone. Use {MERGED_CSV.name} for training.")


if __name__ == "__main__":
    main()
