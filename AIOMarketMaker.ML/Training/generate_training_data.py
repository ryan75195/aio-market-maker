"""
Generate fine-tuning training data for the local extraction model.

Uses GPT-5-nano as teacher. Two modes:
  - Batch API (50% cheaper, up to 24h): prepare -> submit -> collect
  - Direct API (instant, concurrent): run

Excludes eval set jobs to prevent data leakage. Resumable via per-job caching.

Usage:
    python generate_training_data.py run --n-jobs 100 --n-per-job 50
    python generate_training_data.py run --n-jobs 100 --dry-run
    python generate_training_data.py prepare --dry-run
    python generate_training_data.py prepare
    python generate_training_data.py submit
    python generate_training_data.py collect
    python generate_training_data.py status
"""

import argparse
import asyncio
import json
import random
import time
from pathlib import Path

from experiment_topdown_taxonomy import (
    load_settings, get_db_connection, generate_skeleton, sample_diverse,
    OUTPUT_DIR,
)

TRAIN_DIR = OUTPUT_DIR / "training"
TRAIN_DIR.mkdir(parents=True, exist_ok=True)
EVAL_DIR = OUTPUT_DIR / "eval"

# ── Shared prompt constants ─────────────────────────────────────────────────
# These are the EXACT prompts the fine-tuned model will see at inference.
# GPT-5-nano (teacher) sees the same prompts and generates the target output.
# The fine-tuned model (student) learns to reproduce the teacher's output.

EXTRACTION_SYSTEM = (
    "Extract product variant attributes from eBay listing titles. "
    "Match values from the provided lists against text in the title. "
    "Return valid JSON with every axis name as a key, "
    "using null for axes with no matching text in the title."
)

EXTRACTION_USER_TEMPLATE = """Extract product variant attributes from this eBay listing title.

Rules:
- ONLY return values from the provided value lists below
- A value MUST have supporting text in the title — do not infer or assume defaults
- Match flexibly: "Gen 4" matches "gen 4", "MkII" matches "mk2", "2 Pack" matches "2-pack"
- If the listing is for an accessory, part, or unrelated product, return null for ALL axes
- Include ALL axis names in your JSON response (use null for unmatched)

Axes:
{axes_desc}

Title: {title}

JSON:"""


def format_axes_description(skeleton):
    """Format skeleton axes for the extraction prompt.

    Shows full value lists (up to 30) so the model knows exactly
    what values are valid for each axis.
    """
    lines = []
    for ax in skeleton["axes"]:
        name = ax["name"]
        desc = ax.get("description", "")
        values = ", ".join(ax["values"][:30])
        lines.append(f"- {name} ({desc}): {values}")
    return "\n".join(lines)


def format_training_example(title, skeleton, extraction):
    """Format a single training example as a chat message.

    The assistant response includes ALL axes with explicit nulls,
    teaching the model to consider every axis and decide.
    """
    axes_desc = format_axes_description(skeleton)
    user_msg = EXTRACTION_USER_TEMPLATE.format(axes_desc=axes_desc, title=title)

    # Build response with ALL axes — explicit nulls for unmatched
    all_axes = [ax["name"] for ax in skeleton["axes"]]
    response = {}
    for axis_name in all_axes:
        response[axis_name] = extraction.get(axis_name)

    return {
        "messages": [
            {"role": "system", "content": EXTRACTION_SYSTEM},
            {"role": "user", "content": user_msg},
            {"role": "assistant", "content": json.dumps(response)},
        ]
    }


def get_eval_job_ids():
    """Load eval set job IDs to exclude from training."""
    eval_path = EVAL_DIR / "eval_set.json"
    if not eval_path.exists():
        return set()
    with open(eval_path) as f:
        data = json.load(f)
    return set(s["job_id"] for s in data["samples"])


def get_diverse_titles(conn, job_id, n=50, max_pool=500):
    """Get n diverse titles from a job using farthest-point sampling."""
    cursor = conn.cursor()
    cursor.execute(
        "SELECT Id, Title FROM Listings "
        "WHERE ScrapeJobId = ? AND Title IS NOT NULL "
        "ORDER BY Id",
        job_id,
    )
    rows = cursor.fetchall()
    if not rows:
        return []

    if len(rows) > max_pool:
        rng = random.Random(job_id)
        rows = rng.sample(rows, max_pool)

    titles = [r[1] for r in rows]
    indices = sample_diverse(titles, n=min(n, len(titles)))
    return [rows[i][1] for i in indices]


def select_jobs(conn, eval_job_ids, n_jobs):
    """Select diverse training jobs, excluding eval jobs."""
    cursor = conn.cursor()
    cursor.execute(
        "SELECT sj.Id, sj.SearchTerm, COUNT(*) as Cnt "
        "FROM Listings l JOIN ScrapeJobs sj ON l.ScrapeJobId = sj.Id "
        "WHERE l.Title IS NOT NULL "
        "GROUP BY sj.Id, sj.SearchTerm "
        "HAVING COUNT(*) > 100 "
        "ORDER BY NEWID()"
    )
    all_jobs = [
        (r[0], r[1], r[2]) for r in cursor.fetchall()
        if r[0] not in eval_job_ids
    ]
    return all_jobs[:n_jobs]


# ── Phase 1: prepare ────────────────────────────────────────────────────────

def cmd_prepare(args):
    """Sample titles, generate skeletons, create batch request JSONL."""
    settings = load_settings()
    conn = get_db_connection(settings)
    eval_job_ids = get_eval_job_ids()
    print(f"Excluding {len(eval_job_ids)} eval jobs: {sorted(eval_job_ids)}")

    # Check for cached job selection (for deterministic reruns)
    jobs_path = TRAIN_DIR / "selected_jobs.json"
    if jobs_path.exists() and not args.dry_run:
        print(f"Loading cached job selection from {jobs_path}")
        with open(jobs_path) as f:
            jobs = [tuple(j) for j in json.load(f)]
    else:
        jobs = select_jobs(conn, eval_job_ids, args.n_jobs)
        if not args.dry_run:
            with open(jobs_path, "w") as f:
                json.dump(jobs, f, indent=2)

    print(f"\nSelected {len(jobs)} training jobs:")
    for jid, term, cnt in jobs:
        print(f"  Job {jid}: {term} ({cnt} listings)")

    total_titles = len(jobs) * args.n_per_job
    # Batch API is 50% cheaper than real-time
    est_cost = total_titles * 400 / 1_000_000 * 0.15

    print(f"\nEstimate:")
    print(f"  Training examples: ~{total_titles}")
    print(f"  Batch API requests: {total_titles}")
    print(f"  Est. cost (batch 50% off): ~${est_cost:.2f}")

    if args.dry_run:
        print("\n[DRY RUN] Exiting without API calls.")
        conn.close()
        return

    # Generate skeletons (sequential — one per job, small count)
    batch_requests = []
    manifest = []  # Maps custom_id → (job_id, title, skeleton_axes)

    for job_id, search_term, count in jobs:
        print(f"\n--- Job {job_id}: {search_term} ---")

        # Generate or load skeleton
        skeleton_path = TRAIN_DIR / f"skeleton_{job_id}.json"
        if skeleton_path.exists():
            print(f"  Loading cached skeleton")
            with open(skeleton_path) as f:
                skeleton = json.load(f)
        else:
            skel_titles = get_diverse_titles(
                conn, job_id, n=args.skeleton_samples
            )
            print(f"  Generating skeleton from {len(skel_titles)} titles...")
            skeleton = generate_skeleton(
                search_term, skel_titles, count, settings
            )
            with open(skeleton_path, "w") as f:
                json.dump(skeleton, f, indent=2)

        # Sample diverse titles
        titles = get_diverse_titles(conn, job_id, n=args.n_per_job)
        print(f"  Sampled {len(titles)} diverse titles")

        axes_desc = format_axes_description(skeleton)
        axis_names = [ax["name"] for ax in skeleton["axes"]]

        for i, title in enumerate(titles):
            custom_id = f"job{job_id}_t{i}"
            user_msg = EXTRACTION_USER_TEMPLATE.format(
                axes_desc=axes_desc, title=title
            )

            batch_requests.append({
                "custom_id": custom_id,
                "method": "POST",
                "url": "/v1/chat/completions",
                "body": {
                    "model": "gpt-5-nano",
                    "messages": [
                        {"role": "system", "content": EXTRACTION_SYSTEM},
                        {"role": "user", "content": user_msg},
                    ],
                    "response_format": {"type": "json_object"},
                },
            })

            manifest.append({
                "custom_id": custom_id,
                "job_id": job_id,
                "search_term": search_term,
                "title": title,
                "skeleton_axes": axis_names,
            })

    conn.close()

    # Save batch request file
    batch_path = TRAIN_DIR / "batch_requests.jsonl"
    with open(batch_path, "w", encoding="utf-8") as f:
        for req in batch_requests:
            f.write(json.dumps(req, ensure_ascii=False) + "\n")

    # Save manifest (needed by collect phase)
    manifest_path = TRAIN_DIR / "batch_manifest.json"
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)

    print(f"\n{'='*60}")
    print(f"BATCH PREPARED")
    print(f"{'='*60}")
    print(f"Requests: {len(batch_requests)}")
    print(f"Batch file: {batch_path}")
    print(f"Manifest: {manifest_path}")
    print(f"\nNext step: python generate_training_data.py submit")


# ── Phase 2: submit ─────────────────────────────────────────────────────────

def cmd_submit(args):
    """Upload batch file and start the batch job."""
    from openai import OpenAI

    settings = load_settings()
    client = OpenAI(api_key=settings["OpenAi"]["ApiKey"])

    batch_path = TRAIN_DIR / "batch_requests.jsonl"
    if not batch_path.exists():
        print("No batch_requests.jsonl found. Run 'prepare' first.")
        return

    # Count requests
    with open(batch_path, encoding="utf-8") as f:
        n_requests = sum(1 for _ in f)
    print(f"Uploading {n_requests} requests...")

    # Upload the batch file
    with open(batch_path, "rb") as f:
        uploaded = client.files.create(file=f, purpose="batch")
    print(f"Uploaded file: {uploaded.id}")

    # Create the batch
    batch = client.batches.create(
        input_file_id=uploaded.id,
        endpoint="/v1/chat/completions",
        completion_window="24h",
        metadata={"description": "taxonomy extraction training data"},
    )

    # Save batch ID for status/collect
    batch_state = {
        "batch_id": batch.id,
        "input_file_id": uploaded.id,
        "status": batch.status,
        "created_at": time.strftime("%Y-%m-%d %H:%M:%S"),
        "n_requests": n_requests,
    }
    state_path = TRAIN_DIR / "batch_state.json"
    with open(state_path, "w") as f:
        json.dump(batch_state, f, indent=2)

    print(f"\n{'='*60}")
    print(f"BATCH SUBMITTED")
    print(f"{'='*60}")
    print(f"Batch ID: {batch.id}")
    print(f"Status: {batch.status}")
    print(f"Requests: {n_requests}")
    print(f"\nCheck progress: python generate_training_data.py status")
    print(f"When complete:  python generate_training_data.py collect")


# ── Phase 2b: status ────────────────────────────────────────────────────────

def cmd_status(args):
    """Check batch job progress."""
    from openai import OpenAI

    settings = load_settings()
    client = OpenAI(api_key=settings["OpenAi"]["ApiKey"])

    state_path = TRAIN_DIR / "batch_state.json"
    if not state_path.exists():
        print("No batch_state.json found. Run 'submit' first.")
        return

    with open(state_path) as f:
        state = json.load(f)

    batch = client.batches.retrieve(state["batch_id"])

    completed = batch.request_counts.completed if batch.request_counts else 0
    failed = batch.request_counts.failed if batch.request_counts else 0
    total = batch.request_counts.total if batch.request_counts else 0

    print(f"Batch: {batch.id}")
    print(f"Status: {batch.status}")
    print(f"Progress: {completed}/{total} completed, {failed} failed")

    if batch.status == "completed":
        print(f"\nBatch complete! Run: python generate_training_data.py collect")

    # Update saved state
    state["status"] = batch.status
    state["completed"] = completed
    state["failed"] = failed
    with open(state_path, "w") as f:
        json.dump(state, f, indent=2)


# ── Phase 3: collect ────────────────────────────────────────────────────────

def cmd_collect(args):
    """Download batch results and build train/val JSONL files."""
    from openai import OpenAI

    settings = load_settings()
    client = OpenAI(api_key=settings["OpenAi"]["ApiKey"])

    state_path = TRAIN_DIR / "batch_state.json"
    manifest_path = TRAIN_DIR / "batch_manifest.json"

    if not state_path.exists() or not manifest_path.exists():
        print("Missing batch_state.json or batch_manifest.json. "
              "Run 'prepare' and 'submit' first.")
        return

    with open(state_path) as f:
        state = json.load(f)
    with open(manifest_path, encoding="utf-8") as f:
        manifest = json.load(f)

    # Check batch status
    batch = client.batches.retrieve(state["batch_id"])
    if batch.status != "completed":
        print(f"Batch status: {batch.status} — not yet complete.")
        if batch.request_counts:
            print(f"Progress: {batch.request_counts.completed}/"
                  f"{batch.request_counts.total}")
        return

    # Download results
    print(f"Downloading results from batch {batch.id}...")
    output_file_id = batch.output_file_id
    content = client.files.content(output_file_id)

    results_path = TRAIN_DIR / "batch_results.jsonl"
    with open(results_path, "wb") as f:
        f.write(content.read())

    # Parse results into a lookup by custom_id
    results_by_id = {}
    errors = 0
    with open(results_path, encoding="utf-8") as f:
        for line in f:
            result = json.loads(line)
            cid = result["custom_id"]
            if result.get("error"):
                errors += 1
                continue
            body = result["response"]["body"]
            msg_content = body["choices"][0]["message"]["content"]
            results_by_id[cid] = json.loads(msg_content)

    print(f"Downloaded {len(results_by_id)} results, {errors} errors")

    # Build manifest lookup
    manifest_by_id = {m["custom_id"]: m for m in manifest}

    # Load skeletons (needed for format_training_example)
    skeleton_cache = {}

    all_examples = []
    missing = 0

    for item in manifest:
        cid = item["custom_id"]
        job_id = item["job_id"]
        title = item["title"]
        axis_names = item["skeleton_axes"]

        if cid not in results_by_id:
            missing += 1
            continue

        raw = results_by_id[cid]

        # Normalize: ensure ALL axes present, clean values
        cleaned = {}
        for axis_name in axis_names:
            val = raw.get(axis_name)
            if (val is not None
                    and str(val).strip().lower()
                    not in ("null", "none", "n/a", "")):
                cleaned[axis_name] = str(val).strip().lower()
            else:
                cleaned[axis_name] = None

        # Load skeleton for formatting
        if job_id not in skeleton_cache:
            skel_path = TRAIN_DIR / f"skeleton_{job_id}.json"
            with open(skel_path) as f:
                skeleton_cache[job_id] = json.load(f)

        skeleton = skeleton_cache[job_id]
        example = format_training_example(title, skeleton, cleaned)
        all_examples.append(example)

    print(f"Built {len(all_examples)} training examples "
          f"({missing} missing results)")

    # Shuffle and split 90/10 train/val
    random.seed(42)
    random.shuffle(all_examples)

    split = int(len(all_examples) * 0.9)
    train_examples = all_examples[:split]
    val_examples = all_examples[split:]

    # Save as JSONL
    train_path = TRAIN_DIR / "train.jsonl"
    val_path = TRAIN_DIR / "val.jsonl"

    with open(train_path, "w", encoding="utf-8") as f:
        for ex in train_examples:
            f.write(json.dumps(ex, ensure_ascii=False) + "\n")

    with open(val_path, "w", encoding="utf-8") as f:
        for ex in val_examples:
            f.write(json.dumps(ex, ensure_ascii=False) + "\n")

    # Stats
    total_axes = 0
    non_null = 0
    for ex in all_examples:
        resp = json.loads(ex["messages"][2]["content"])
        total_axes += len(resp)
        non_null += sum(1 for v in resp.values() if v is not None)

    print(f"\n{'='*60}")
    print(f"TRAINING DATA GENERATED")
    print(f"{'='*60}")
    print(f"Total examples: {len(all_examples)}")
    print(f"Train: {len(train_examples)}, Val: {len(val_examples)}")
    print(f"Total axis slots: {total_axes}")
    print(f"Non-null: {non_null} ({non_null/total_axes*100:.1f}%)")
    print(f"Null: {total_axes-non_null} "
          f"({(total_axes-non_null)/total_axes*100:.1f}%)")
    print(f"\nSaved to:")
    print(f"  {train_path}")
    print(f"  {val_path}")
    print(f"\nNext step: python finetune_extraction.py")


# ── Direct API mode ────────────────────────────────────────────────────────

async def call_extraction(client, sem, title, axes_desc, custom_id, model):
    """Call GPT-5-nano for a single extraction with concurrency limiting."""
    user_msg = EXTRACTION_USER_TEMPLATE.format(
        axes_desc=axes_desc, title=title
    )
    async with sem:
        try:
            resp = await client.chat.completions.create(
                model=model,
                messages=[
                    {"role": "system", "content": EXTRACTION_SYSTEM},
                    {"role": "user", "content": user_msg},
                ],
                response_format={"type": "json_object"},
            )
            content = resp.choices[0].message.content
            usage = resp.usage
            return custom_id, json.loads(content), usage, None
        except Exception as e:
            return custom_id, None, None, str(e)


async def process_job_direct(client, sem, job_id, search_term, titles,
                             skeleton, model, progress):
    """Process all titles for a single job concurrently."""
    axes_desc = format_axes_description(skeleton)
    axis_names = [ax["name"] for ax in skeleton["axes"]]

    tasks = []
    manifest_entries = []

    for i, title in enumerate(titles):
        custom_id = f"job{job_id}_t{i}"
        tasks.append(
            call_extraction(client, sem, title, axes_desc, custom_id, model)
        )
        manifest_entries.append({
            "custom_id": custom_id,
            "job_id": job_id,
            "search_term": search_term,
            "title": title,
            "skeleton_axes": axis_names,
        })

    results = []
    errors = 0
    for coro in asyncio.as_completed(tasks):
        custom_id, extraction, usage, error = await coro
        progress["done"] += 1
        if error:
            errors += 1
            print(f"  ERROR [{custom_id}]: {error}")
        else:
            results.append((custom_id, extraction, usage))

        if progress["done"] % 50 == 0 or progress["done"] == progress["total"]:
            elapsed = time.time() - progress["t0"]
            rate = progress["done"] / elapsed
            eta = (progress["total"] - progress["done"]) / rate if rate > 0 else 0
            print(
                f"  [{progress['done']}/{progress['total']}] "
                f"{rate:.1f} req/s, ETA {eta:.0f}s"
            )

    return manifest_entries, results, errors


def cmd_run(args):
    """Generate training data using direct API calls (fast, concurrent)."""
    settings = load_settings()
    conn = get_db_connection(settings)
    eval_job_ids = get_eval_job_ids()
    print(f"Excluding {len(eval_job_ids)} eval jobs")

    # Select jobs (reuse v2 cache if exists, else pick new)
    jobs_path = TRAIN_DIR / "selected_jobs_v2.json"
    if jobs_path.exists() and not args.dry_run:
        print(f"Loading cached job selection from {jobs_path}")
        with open(jobs_path) as f:
            jobs = [tuple(j) for j in json.load(f)]
    else:
        jobs = select_jobs(conn, eval_job_ids, args.n_jobs)
        if not args.dry_run:
            with open(jobs_path, "w") as f:
                json.dump(jobs, f, indent=2)

    # Also exclude v1 training jobs to get fresh categories
    v1_jobs_path = TRAIN_DIR / "selected_jobs.json"
    v1_job_ids = set()
    if v1_jobs_path.exists() and not args.include_v1:
        with open(v1_jobs_path) as f:
            v1_job_ids = {j[0] for j in json.load(f)}
        jobs = [j for j in jobs if j[0] not in v1_job_ids]
        print(f"Excluding {len(v1_job_ids)} v1 training jobs, "
              f"{len(jobs)} remaining")

    total_titles = len(jobs) * args.n_per_job
    # Direct API pricing (no batch discount)
    est_cost = total_titles * 1500 / 1_000_000 * 0.60  # ~1500 output tokens
    est_cost += total_titles * 400 / 1_000_000 * 0.15   # ~400 input tokens

    print(f"\nSelected {len(jobs)} jobs")
    print(f"Training examples: ~{total_titles}")
    print(f"Concurrency: {args.concurrency}")
    print(f"Est. cost (direct API): ~${est_cost:.2f}")

    if args.dry_run:
        print("\n[DRY RUN] Jobs:")
        for jid, term, cnt in jobs:
            print(f"  Job {jid}: {term} ({cnt} listings)")
        conn.close()
        return

    # Run async extraction
    asyncio.run(_run_direct(
        args, settings, conn, jobs, total_titles
    ))


async def generate_skeleton_async(client, sem, search_term, sample_titles,
                                  total_count):
    """Generate a skeleton via async OpenAI call."""
    from experiment_topdown_taxonomy import SKELETON_PROMPT

    titles_text = "\n".join(f"- {t}" for t in sample_titles)
    prompt = SKELETON_PROMPT.format(
        search_term=search_term,
        total_count=total_count,
        sample_count=len(sample_titles),
        sample_titles=titles_text,
    )

    async with sem:
        t0 = time.time()
        resp = await client.chat.completions.create(
            model="gpt-5-nano",
            messages=[
                {"role": "system",
                 "content": "You are a product taxonomy expert. "
                            "Return only valid JSON."},
                {"role": "user", "content": prompt},
            ],
            response_format={"type": "json_object"},
        )
        elapsed = time.time() - t0

    result = json.loads(resp.choices[0].message.content)
    axes_summary = ", ".join(
        f"{ax['name']}({len(ax['values'])})"
        for ax in result["axes"]
    )
    print(f"  [{search_term}] skeleton: {len(result['axes'])} axes "
          f"in {elapsed:.0f}s — {axes_summary}")
    return result


async def _run_direct(args, settings, conn, jobs, total_titles):
    """Async entrypoint for direct API calls."""
    from openai import AsyncOpenAI

    client = AsyncOpenAI(api_key=settings["OpenAi"]["ApiKey"])
    sem = asyncio.Semaphore(args.concurrency)

    # ── Phase 1: Generate all skeletons concurrently ──────────────────────
    skeletons = {}
    skel_tasks = {}
    jobs_needing_skeletons = []

    for job_id, search_term, count in jobs:
        skeleton_path = TRAIN_DIR / f"skeleton_{job_id}.json"
        if skeleton_path.exists():
            with open(skeleton_path) as f:
                skeletons[job_id] = json.load(f)
        else:
            skel_titles = get_diverse_titles(
                conn, job_id, n=args.skeleton_samples
            )
            jobs_needing_skeletons.append(
                (job_id, search_term, count, skel_titles)
            )

    print(f"\nSkeletons: {len(skeletons)} cached, "
          f"{len(jobs_needing_skeletons)} to generate")

    if jobs_needing_skeletons:
        # Use a separate semaphore for skeletons (they're heavier)
        skel_sem = asyncio.Semaphore(10)
        skel_coros = []
        skel_job_ids = []

        for job_id, search_term, count, skel_titles in jobs_needing_skeletons:
            skel_coros.append(
                generate_skeleton_async(
                    client, skel_sem, search_term, skel_titles, count
                )
            )
            skel_job_ids.append(job_id)

        print(f"Generating {len(skel_coros)} skeletons (10 concurrent)...")
        t0_skel = time.time()

        results = await asyncio.gather(*skel_coros, return_exceptions=True)

        for job_id, result in zip(skel_job_ids, results):
            if isinstance(result, Exception):
                print(f"  ERROR skeleton job {job_id}: {result}")
                continue
            skeletons[job_id] = result
            skeleton_path = TRAIN_DIR / f"skeleton_{job_id}.json"
            with open(skeleton_path, "w") as f:
                json.dump(result, f, indent=2)

        print(f"Skeletons done in {time.time() - t0_skel:.0f}s")

    # ── Phase 2: Run all extractions concurrently ─────────────────────────
    all_manifest = []
    all_results = {}
    total_errors = 0
    total_prompt_tokens = 0
    total_completion_tokens = 0

    # Filter to jobs with successful skeletons
    valid_jobs = [(jid, st, cnt) for jid, st, cnt in jobs
                  if jid in skeletons]
    actual_titles = len(valid_jobs) * args.n_per_job

    progress = {"done": 0, "total": actual_titles, "t0": time.time()}

    print(f"\nExtracting from {len(valid_jobs)} jobs, "
          f"~{actual_titles} titles...")

    # Launch all jobs concurrently
    job_coros = []
    for job_id, search_term, count in valid_jobs:
        titles = get_diverse_titles(conn, job_id, n=args.n_per_job)
        skeleton = skeletons[job_id]
        job_coros.append(
            process_job_direct(
                client, sem, job_id, search_term, titles,
                skeleton, args.model, progress,
            )
        )

    job_results = await asyncio.gather(*job_coros, return_exceptions=True)

    for result in job_results:
        if isinstance(result, Exception):
            print(f"  ERROR: {result}")
            total_errors += 1
            continue
        manifest_entries, results, errors = result
        all_manifest.extend(manifest_entries)
        total_errors += errors
        for cid, extraction, usage in results:
            all_results[cid] = extraction
            if usage:
                total_prompt_tokens += usage.prompt_tokens
                total_completion_tokens += usage.completion_tokens

    conn.close()
    await client.close()

    elapsed = time.time() - progress["t0"]
    print(f"\n{'='*60}")
    print(f"API CALLS COMPLETE")
    print(f"{'='*60}")
    print(f"Results: {len(all_results)}, Errors: {total_errors}")
    print(f"Time: {elapsed:.0f}s ({len(all_results)/elapsed:.1f} req/s)")
    print(f"Tokens: {total_prompt_tokens:,} in, "
          f"{total_completion_tokens:,} out")

    # Estimate cost
    input_cost = total_prompt_tokens * 0.15 / 1_000_000
    output_cost = total_completion_tokens * 0.60 / 1_000_000
    print(f"Cost: ${input_cost + output_cost:.2f} "
          f"(input ${input_cost:.2f} + output ${output_cost:.2f})")

    # Build training examples (same logic as cmd_collect)
    skeleton_cache = {}
    all_examples = []

    for item in all_manifest:
        cid = item["custom_id"]
        job_id = item["job_id"]
        title = item["title"]
        axis_names = item["skeleton_axes"]

        if cid not in all_results:
            continue

        raw = all_results[cid]

        # Normalize
        cleaned = {}
        for axis_name in axis_names:
            val = raw.get(axis_name)
            if (val is not None
                    and str(val).strip().lower()
                    not in ("null", "none", "n/a", "")):
                cleaned[axis_name] = str(val).strip().lower()
            else:
                cleaned[axis_name] = None

        if job_id not in skeleton_cache:
            skel_path = TRAIN_DIR / f"skeleton_{job_id}.json"
            with open(skel_path) as f:
                skeleton_cache[job_id] = json.load(f)

        skeleton = skeleton_cache[job_id]
        example = format_training_example(title, skeleton, cleaned)
        all_examples.append(example)

    # Load existing v1 training data and merge
    v1_train_path = TRAIN_DIR / "train.jsonl"
    v1_val_path = TRAIN_DIR / "val.jsonl"
    existing_examples = []
    if v1_train_path.exists():
        with open(v1_train_path, encoding="utf-8") as f:
            for line in f:
                existing_examples.append(json.loads(line))
    if v1_val_path.exists():
        with open(v1_val_path, encoding="utf-8") as f:
            for line in f:
                existing_examples.append(json.loads(line))

    print(f"\nNew examples: {len(all_examples)}")
    print(f"Existing (v1) examples: {len(existing_examples)}")

    combined = existing_examples + all_examples
    print(f"Combined total: {len(combined)}")

    # Shuffle and split 90/10
    random.seed(42)
    random.shuffle(combined)

    split = int(len(combined) * 0.9)
    train_examples = combined[:split]
    val_examples = combined[split:]

    # Save merged files (backup originals first)
    if existing_examples:
        v1_backup_train = TRAIN_DIR / "train_v1.jsonl"
        v1_backup_val = TRAIN_DIR / "val_v1.jsonl"
        if not v1_backup_train.exists() and v1_train_path.exists():
            v1_train_path.rename(v1_backup_train)
            print(f"Backed up v1 train -> {v1_backup_train.name}")
        if not v1_backup_val.exists() and v1_val_path.exists():
            v1_val_path.rename(v1_backup_val)
            print(f"Backed up v1 val -> {v1_backup_val.name}")

    train_path = TRAIN_DIR / "train.jsonl"
    val_path = TRAIN_DIR / "val.jsonl"

    with open(train_path, "w", encoding="utf-8") as f:
        for ex in train_examples:
            f.write(json.dumps(ex, ensure_ascii=False) + "\n")

    with open(val_path, "w", encoding="utf-8") as f:
        for ex in val_examples:
            f.write(json.dumps(ex, ensure_ascii=False) + "\n")

    # Save v2 manifest
    manifest_path = TRAIN_DIR / "batch_manifest_v2.json"
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(all_manifest, f, indent=2, ensure_ascii=False)

    # Stats
    total_axes = 0
    non_null = 0
    for ex in combined:
        resp = json.loads(ex["messages"][2]["content"])
        total_axes += len(resp)
        non_null += sum(1 for v in resp.values() if v is not None)

    print(f"\n{'='*60}")
    print(f"TRAINING DATA V2 GENERATED")
    print(f"{'='*60}")
    print(f"Total examples: {len(combined)} "
          f"({len(existing_examples)} v1 + {len(all_examples)} v2)")
    print(f"Train: {len(train_examples)}, Val: {len(val_examples)}")
    print(f"Total axis slots: {total_axes}")
    print(f"Non-null: {non_null} ({non_null/total_axes*100:.1f}%)")
    print(f"Null: {total_axes-non_null} "
          f"({(total_axes-non_null)/total_axes*100:.1f}%)")
    print(f"\nSaved to:")
    print(f"  {train_path}")
    print(f"  {val_path}")
    print(f"\nNext step: python finetune_extraction.py")


# ── Main ────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="Generate fine-tuning training data"
    )
    sub = parser.add_subparsers(dest="command")

    # run (direct API, fast)
    p_run = sub.add_parser(
        "run", help="Generate training data via direct API (fast, concurrent)"
    )
    p_run.add_argument(
        "--n-jobs", type=int, default=100,
        help="Number of jobs to sample from"
    )
    p_run.add_argument(
        "--n-per-job", type=int, default=50,
        help="Diverse titles to label per job"
    )
    p_run.add_argument(
        "--skeleton-samples", type=int, default=100,
        help="Diverse titles for skeleton generation per job"
    )
    p_run.add_argument(
        "--concurrency", type=int, default=30,
        help="Max concurrent API requests"
    )
    p_run.add_argument(
        "--model", default="gpt-5-nano",
        help="Teacher model for extraction labeling"
    )
    p_run.add_argument(
        "--include-v1", action="store_true",
        help="Include v1 training jobs (default: exclude for fresh categories)"
    )
    p_run.add_argument(
        "--dry-run", action="store_true",
        help="Preview without API calls"
    )

    # prepare (batch API)
    p_prep = sub.add_parser(
        "prepare", help="Sample titles, generate skeletons, create batch file"
    )
    p_prep.add_argument(
        "--n-jobs", type=int, default=30,
        help="Number of jobs to sample from"
    )
    p_prep.add_argument(
        "--n-per-job", type=int, default=50,
        help="Diverse titles to label per job"
    )
    p_prep.add_argument(
        "--skeleton-samples", type=int, default=100,
        help="Diverse titles for skeleton generation per job"
    )
    p_prep.add_argument(
        "--dry-run", action="store_true",
        help="Preview without API calls"
    )

    # submit
    sub.add_parser("submit", help="Upload and start batch job")

    # status
    sub.add_parser("status", help="Check batch progress")

    # collect
    sub.add_parser("collect", help="Download results and build JSONL")

    args = parser.parse_args()

    if args.command == "run":
        cmd_run(args)
    elif args.command == "prepare":
        cmd_prepare(args)
    elif args.command == "submit":
        cmd_submit(args)
    elif args.command == "status":
        cmd_status(args)
    elif args.command == "collect":
        cmd_collect(args)
    else:
        parser.print_help()


if __name__ == "__main__":
    main()
