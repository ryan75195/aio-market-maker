"""
V6 Pilot: Generate a small batch of pairs using the banded hard-negative
strategy for quality validation before committing to a full 200K run.

Strategy (4 pair sources):
  1. Pinecone banded (same category) — 3 bands: 0.90+, 0.80-0.90, 0.70-0.80
  2. Pinecone cross-category — query without job filter, keep different-category matches
  3. Same-category random — random pairs from same job, NOT Pinecone neighbors
  4. Description-overlap — listings with near-identical descriptions across categories

Usage:
  py -3.12 collect_v6_pilot.py              # default 5000 pairs
  py -3.12 collect_v6_pilot.py --target 2000
  py -3.12 collect_v6_pilot.py --target 500 --dry-run   # generate pairs without labeling
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
OUTPUT_CSV = DATA_DIR / "v6_pilot_pairs.csv"
OUTPUT_JSON = DATA_DIR / "v6_pilot_results.json"

PINECONE_HOST = "https://arbitrage-d207f30.svc.aped-4627-b74a.pinecone.io"

GPT_MODEL = "gpt-5-mini"
GPT_MAX_TOKENS = 2000
GPT_WORKERS = 15
PINECONE_SLEEP = 0.1

# Pair source ratios
RATIO_BANDED = 0.60       # 60% — Pinecone same-category banded
RATIO_CROSS_CAT = 0.15    # 15% — Pinecone cross-category
RATIO_RANDOM = 0.15       # 15% — same-category random (non-neighbor)
RATIO_DESC_OVERLAP = 0.10 # 10% — description-overlap across categories

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
    log_path = DATA_DIR / f"v6_pilot_{datetime.now().strftime('%Y%m%d_%H%M%S')}.log"
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


# ── Load API keys ─────────────────────────────────────────────────────────
def load_api_keys():
    for path in [
        DATA_DIR / ".." / ".." / "AIOMarketMaker.Api" / "bin" / "Debug" / "net8.0" / "local.settings.json",
        DATA_DIR / ".." / ".." / "AIOMarketMaker.Etl" / "local.settings.json",
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
            content = resp.json()["choices"][0]["message"]["content"]
            if not content:
                time.sleep(1)
                continue

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


def label_pairs_parallel(pairs_to_label, max_workers=GPT_WORKERS):
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


# ══════════════════════════════════════════════════════════════════════════
# PAIR GENERATION STRATEGIES
# ══════════════════════════════════════════════════════════════════════════

def generate_banded_pairs(listings, products, valid_jobs, seen_pairs, target):
    """Source 1: Pinecone same-category pairs sampled by similarity band."""
    log(f"  Generating {target} banded pairs...")

    pairs = []
    pairs_per_job = max(3, target // len(valid_jobs))

    # Weak categories get 2x
    job_targets = {}
    for jid in valid_jobs:
        name = products[jid]
        job_targets[jid] = pairs_per_job * 2 if name in WEAK_CATEGORIES else pairs_per_job

    for jid, job_listings in valid_jobs.items():
        job_target = job_targets[jid]
        product_name = products[jid]

        # Pick anchors
        random.shuffle(job_listings)
        anchors = job_listings[:min(20, len(job_listings) // 2)]

        # Fetch vectors
        anchor_ids = [a["listingId"] for a in anchors]
        anchor_vectors = pinecone_fetch(anchor_ids)
        job_pairs = 0

        for anchor in anchors:
            if job_pairs >= job_target:
                break
            aid = anchor["listingId"]
            if aid not in anchor_vectors:
                continue

            vec = anchor_vectors[aid]["values"]
            matches = pinecone_query(vec, top_k=100, job_id=jid)

            # Filter self and seen
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

            # Band sampling: 2 from each band
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
                    "cosine_sim": m["score"],
                    "band": "high" if m["score"] >= 0.90 else "mid" if m["score"] >= 0.80 else "low",
                })
                job_pairs += 1

            time.sleep(PINECONE_SLEEP)

        if job_pairs > 0:
            log(f"    {product_name}: {job_pairs} banded pairs")

    random.shuffle(pairs)
    return pairs[:target]


def generate_cross_category_pairs(listings, products, valid_jobs, seen_pairs, target):
    """Source 2: Pinecone query without job filter — pairs from different categories."""
    log(f"  Generating {target} cross-category pairs...")

    pairs = []
    pairs_per_job = max(2, target // len(valid_jobs))

    for jid, job_listings in valid_jobs.items():
        product_name = products[jid]

        # Pick a few anchors
        random.shuffle(job_listings)
        anchors = job_listings[:5]
        anchor_ids = [a["listingId"] for a in anchors]
        anchor_vectors = pinecone_fetch(anchor_ids)
        job_pairs = 0

        for anchor in anchors:
            if job_pairs >= pairs_per_job:
                break
            aid = anchor["listingId"]
            if aid not in anchor_vectors:
                continue

            vec = anchor_vectors[aid]["values"]
            matches = pinecone_query(vec, top_k=50)  # NO job_id filter

            # Keep only matches from DIFFERENT categories
            cross = []
            for m in matches:
                if m["id"] == aid:
                    continue
                neighbor = listings.get(m["id"])
                if not neighbor:
                    continue
                if neighbor["scrapeJobId"] == jid:
                    continue  # same category, skip
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

                # Use anchor's category as the product_name
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
                    "cosine_sim": m["score"],
                    "neighbor_category": products.get(neighbor["scrapeJobId"], "unknown"),
                })
                job_pairs += 1

            time.sleep(PINECONE_SLEEP)

        if job_pairs > 0:
            log(f"    {product_name}: {job_pairs} cross-category pairs")

    random.shuffle(pairs)
    return pairs[:target]


def generate_random_same_category_pairs(listings, products, valid_jobs, seen_pairs, target):
    """Source 3: Random pairs from same category — catches same-brand-different-model."""
    log(f"  Generating {target} random same-category pairs...")

    pairs = []
    pairs_per_job = max(2, target // len(valid_jobs))

    for jid, job_listings in valid_jobs.items():
        if len(job_listings) < 5:
            continue
        product_name = products[jid]
        job_pairs = 0

        # Oversample to account for seen/self pairs
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
                "cosine_sim": None,
            })
            job_pairs += 1

        if job_pairs > 0:
            log(f"    {product_name}: {job_pairs} random pairs")

    random.shuffle(pairs)
    return pairs[:target]


def generate_description_overlap_pairs(listings, products, valid_jobs, seen_pairs, target):
    """Source 4: Listings with near-identical descriptions across different categories."""
    log(f"  Generating {target} description-overlap pairs...")

    # Index descriptions by first 80 chars (proxy for template sellers)
    desc_groups = defaultdict(list)
    for lid, l in listings.items():
        if not l["description"] or len(l["description"]) < 80:
            continue
        key = l["description"][:80].lower().strip()
        desc_groups[key].append(l)

    # Find groups that span multiple categories
    cross_cat_groups = []
    for key, group in desc_groups.items():
        job_ids = set(l["scrapeJobId"] for l in group)
        if len(job_ids) >= 2 and len(group) >= 2:
            cross_cat_groups.append(group)

    log(f"    Found {len(cross_cat_groups)} description groups spanning multiple categories")

    pairs = []
    for group in cross_cat_groups:
        if len(pairs) >= target:
            break

        # Pair listings from different categories within this group
        by_job = defaultdict(list)
        for l in group:
            by_job[l["scrapeJobId"]].append(l)

        job_ids = list(by_job.keys())
        for i in range(len(job_ids)):
            for j in range(i + 1, len(job_ids)):
                if len(pairs) >= target:
                    break
                a = random.choice(by_job[job_ids[i]])
                b = random.choice(by_job[job_ids[j]])
                pair_key = tuple(sorted([a["listingId"], b["listingId"]]))
                if pair_key in seen_pairs:
                    continue
                seen_pairs.add(pair_key)

                product_a = products.get(a["scrapeJobId"], "unknown")
                product_b = products.get(b["scrapeJobId"], "unknown")
                pairs.append({
                    "anchor_id": a["listingId"],
                    "neighbor_id": b["listingId"],
                    "job_id": a["scrapeJobId"],
                    "product_name": product_a,
                    "anchor_title": a["title"],
                    "neighbor_title": b["title"],
                    "anchor_desc": a["description"],
                    "neighbor_desc": b["description"],
                    "source": "desc_overlap",
                    "cosine_sim": None,
                    "neighbor_category": product_b,
                })

    # Also pair same-category listings with overlapping descriptions
    # Cap per category to prevent skew
    cat_counts = defaultdict(int)
    max_per_cat = max(3, target // max(len(valid_jobs), 1))

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
                        "cosine_sim": None,
                    })
                    if len(pairs) >= target or cat_counts[jid] >= max_per_cat:
                        break

    random.shuffle(pairs)
    log(f"    Generated {len(pairs)} description-overlap pairs")
    return pairs[:target]


# ── CSV output ────────────────────────────────────────────────────────────
CSV_FIELDS = [
    "anchor_id", "neighbor_id", "job_id", "product_name",
    "anchor_title", "neighbor_title",
    "anchor_desc", "neighbor_desc",
    "label", "confidence", "reasoning", "source",
]

def save_csv(pairs):
    with open(OUTPUT_CSV, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=CSV_FIELDS, extrasaction="ignore")
        writer.writeheader()
        for p in pairs:
            writer.writerow(p)


# ── Main ──────────────────────────────────────────────────────────────────
def main():
    parser = argparse.ArgumentParser(description="V6 pilot: validate banded pair strategy")
    parser.add_argument("--target", type=int, default=5000, help="Target number of new pairs")
    parser.add_argument("--dry-run", action="store_true", help="Generate pairs without labeling")
    parser.add_argument("--seed", type=int, default=42, help="Random seed")
    args = parser.parse_args()

    random.seed(args.seed)
    _init_log()

    log("=" * 70)
    log(f"V6 Pilot — {args.target} pairs, banded hard-negative strategy")
    log(f"{'DRY RUN — no GPT labeling' if args.dry_run else f'Labeling with {GPT_MODEL}'}")
    log("=" * 70)

    if not args.dry_run and (not OPENAI_API_KEY or not PINECONE_API_KEY):
        log("ERROR: Missing API keys")
        sys.exit(1)

    if not PINECONE_API_KEY:
        log("ERROR: Missing Pinecone API key")
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

    # Calculate targets per source
    t_banded = int(args.target * RATIO_BANDED)
    t_cross = int(args.target * RATIO_CROSS_CAT)
    t_random = int(args.target * RATIO_RANDOM)
    t_desc = args.target - t_banded - t_cross - t_random

    log(f"\nPair targets: banded={t_banded}, cross_cat={t_cross}, random={t_random}, desc_overlap={t_desc}")

    # Generate pairs from each source
    log("\n--- Generating pairs ---")
    all_pairs = []

    banded = generate_banded_pairs(listings, products, valid_jobs, seen_pairs, t_banded)
    all_pairs.extend(banded)
    log(f"  Banded: {len(banded)} pairs")

    cross = generate_cross_category_pairs(listings, products, valid_jobs, seen_pairs, t_cross)
    all_pairs.extend(cross)
    log(f"  Cross-category: {len(cross)} pairs")

    rand_pairs = generate_random_same_category_pairs(listings, products, valid_jobs, seen_pairs, t_random)
    all_pairs.extend(rand_pairs)
    log(f"  Random same-category: {len(rand_pairs)} pairs")

    desc = generate_description_overlap_pairs(listings, products, valid_jobs, seen_pairs, t_desc)
    all_pairs.extend(desc)
    log(f"  Description-overlap: {len(desc)} pairs")

    log(f"\nTotal generated: {len(all_pairs)} pairs")

    # ── Label with GPT ────────────────────────────────────────────────────
    if not args.dry_run:
        log(f"\nLabeling {len(all_pairs)} pairs with {GPT_MODEL} ({GPT_WORKERS} workers)...")
        pairs_to_label = [
            (p["anchor_title"], p["anchor_desc"], p["neighbor_title"], p["neighbor_desc"], p["product_name"])
            for p in all_pairs
        ]
        label_results = label_pairs_parallel(pairs_to_label)

        failures = 0
        for i, result in enumerate(label_results):
            if result is None:
                failures += 1
                all_pairs[i]["label"] = None
                all_pairs[i]["confidence"] = None
                all_pairs[i]["reasoning"] = None
            else:
                label, confidence, reasoning = result
                all_pairs[i]["label"] = label
                all_pairs[i]["confidence"] = confidence
                all_pairs[i]["reasoning"] = reasoning[:200]

        labeled = [p for p in all_pairs if p.get("label") is not None]
        log(f"Labeled: {len(labeled)}/{len(all_pairs)} ({failures} failures)")

        # Save
        save_csv(labeled)
        with open(OUTPUT_JSON, "w", encoding="utf-8") as f:
            json.dump(labeled, f, indent=2, ensure_ascii=False)
        log(f"Saved to {OUTPUT_CSV.name} and {OUTPUT_JSON.name}")
    else:
        labeled = all_pairs
        log("Dry run — skipping labeling")

    # ══════════════════════════════════════════════════════════════════════
    # QUALITY REPORT
    # ══════════════════════════════════════════════════════════════════════
    log(f"\n{'=' * 70}")
    log("QUALITY REPORT")
    log(f"{'=' * 70}")

    # Distribution by source
    by_source = defaultdict(list)
    for p in labeled:
        by_source[p.get("source", "unknown")].append(p)

    log(f"\nPairs by source:")
    for source, source_pairs in sorted(by_source.items()):
        n = len(source_pairs)
        if not args.dry_run:
            same = sum(1 for p in source_pairs if p.get("label") == 1)
            diff = sum(1 for p in source_pairs if p.get("label") == 0)
            high = sum(1 for p in source_pairs if p.get("confidence") == "high")
            low = sum(1 for p in source_pairs if p.get("confidence") == "low")
            log(f"  {source:<20} {n:>5} pairs | same={same} diff={diff} ({same/n:.0%} same) | high_conf={high} low_conf={low}")
        else:
            log(f"  {source:<20} {n:>5} pairs")

    # Cosine similarity distribution for banded pairs
    banded_pairs = by_source.get("banded", [])
    if banded_pairs:
        log(f"\nBanded pair similarity distribution:")
        for band_name in ["high", "mid", "low"]:
            band = [p for p in banded_pairs if p.get("band") == band_name]
            if band and not args.dry_run:
                same = sum(1 for p in band if p.get("label") == 1)
                log(f"  {band_name:<6} (n={len(band):>4}): {same/len(band):.0%} same")
            elif band:
                sims = [p["cosine_sim"] for p in band if p.get("cosine_sim")]
                log(f"  {band_name:<6} (n={len(band):>4}): sim range {min(sims):.3f}-{max(sims):.3f}" if sims else f"  {band_name:<6} (n={len(band):>4})")

    # Category coverage
    by_cat = defaultdict(list)
    for p in labeled:
        by_cat[p["product_name"]].append(p)

    log(f"\nCategory coverage ({len(by_cat)} categories):")
    for cat in sorted(by_cat.keys()):
        cat_pairs = by_cat[cat]
        sources = defaultdict(int)
        for p in cat_pairs:
            sources[p.get("source", "?")] += 1
        source_str = ", ".join(f"{s}={c}" for s, c in sorted(sources.items()))
        if not args.dry_run:
            same = sum(1 for p in cat_pairs if p.get("label") == 1)
            log(f"  {cat:<35} {len(cat_pairs):>4} pairs ({same/len(cat_pairs):.0%} same) | {source_str}")
        else:
            log(f"  {cat:<35} {len(cat_pairs):>4} pairs | {source_str}")

    # Sample pairs for manual inspection (5 per source)
    if not args.dry_run:
        log(f"\n{'=' * 70}")
        log("SAMPLE PAIRS FOR MANUAL INSPECTION (5 per source)")
        log(f"{'=' * 70}")

        for source, source_pairs in sorted(by_source.items()):
            log(f"\n  --- {source} ---")
            sample = random.sample(source_pairs, min(5, len(source_pairs)))
            for p in sample:
                lbl = "SAME" if p.get("label") == 1 else "DIFF"
                conf = p.get("confidence", "?")
                sim = f"sim={p['cosine_sim']:.3f}" if p.get("cosine_sim") else "sim=N/A"
                log(f"    [{lbl} {conf}] {sim} [{p['product_name']}]")
                log(f"      A: {p['anchor_title'][:80]}")
                log(f"      B: {p['neighbor_title'][:80]}")
                if p.get("reasoning"):
                    log(f"      R: {p['reasoning'][:120]}")

    log(f"\n{'=' * 70}")
    log("DONE")
    log(f"{'=' * 70}")


if __name__ == "__main__":
    main()
