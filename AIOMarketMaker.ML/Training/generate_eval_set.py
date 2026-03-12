"""
Generate a ground-truth eval set using GPT-5-nano as labeler.

Samples diverse titles across multiple product categories, has GPT-5-nano
extract the correct axes, then saves as a labeled eval set we can score
the local LLM against.

Usage:
    python generate_eval_set.py
    python generate_eval_set.py --n-per-job 10 --max-jobs 5  # quick test
"""

import argparse
import json
import time
from pathlib import Path

import pyodbc
import numpy as np
from sklearn.feature_extraction.text import TfidfVectorizer
from sklearn.metrics.pairwise import cosine_distances

from experiment_topdown_taxonomy import (
    load_settings, get_db_connection, generate_skeleton, sample_diverse,
    OUTPUT_DIR,
)

EVAL_DIR = OUTPUT_DIR / "eval"
EVAL_DIR.mkdir(parents=True, exist_ok=True)


def get_diverse_titles_for_job(conn, job_id, search_term, n=20, max_pool=500):
    """Get n diverse titles from a job using farthest-point sampling.

    To avoid expensive TF-IDF on huge jobs, randomly subsample to max_pool first.
    """
    import random

    cursor = conn.cursor()
    cursor.execute(
        "SELECT Id, Title, Price, ListingStatus, Condition "
        "FROM Listings WHERE ScrapeJobId = ? AND Title IS NOT NULL "
        "ORDER BY Id",
        job_id,
    )
    rows = cursor.fetchall()
    if not rows:
        return []

    # Subsample large jobs before expensive TF-IDF
    if len(rows) > max_pool:
        rng = random.Random(job_id)
        rows = rng.sample(rows, max_pool)

    titles = [r[1] for r in rows]
    indices = sample_diverse(titles, n=min(n, len(titles)))

    return [
        {
            "listing_id": rows[i][0],
            "title": rows[i][1],
            "price": float(rows[i][2]) if rows[i][2] else None,
            "status": rows[i][3],
            "condition": rows[i][4],
            "job_id": job_id,
            "search_term": search_term,
        }
        for i in indices
    ]


def label_with_gpt5(titles, skeleton, settings):
    """Use GPT-5-nano to extract ground-truth axes for each title."""
    from openai import OpenAI

    client = OpenAI(api_key=settings["OpenAi"]["ApiKey"])

    axes_desc = "\n".join(
        f"- {ax['name']}: {ax.get('description', '')}. Values: {', '.join(ax['values'][:20])}"
        for ax in skeleton["axes"]
    )

    results = []
    for i, title in enumerate(titles):
        prompt = f"""Extract product attributes from this eBay listing title.

CRITICAL RULES:
- ONLY extract values whose text appears verbatim in the title
- If a value is NOT written in the title, you MUST return null for that axis
- Do NOT use your product knowledge to fill in missing values
- Do NOT guess, infer, or assume defaults

Axes to extract:
{axes_desc}

Title: {title}

Return JSON with axis names as keys. Use null for any axis where no matching text appears in the title.
"""
        try:
            response = client.chat.completions.create(
                model="gpt-5-nano",
                messages=[
                    {"role": "system", "content": "Extract product attributes from the title text ONLY. Never infer. Return valid JSON."},
                    {"role": "user", "content": prompt},
                ],
                response_format={"type": "json_object"},
            )
            extracted = json.loads(response.choices[0].message.content)
            # Clean nulls
            cleaned = {k: str(v).strip().lower() for k, v in extracted.items()
                       if v is not None and str(v).strip().lower() not in ("null", "none", "")}
            results.append(cleaned)
        except Exception as e:
            print(f"  Error on title {i}: {e}")
            results.append({})

        if (i + 1) % 10 == 0:
            print(f"  Labeled {i+1}/{len(titles)}")

    return results


def main():
    parser = argparse.ArgumentParser(description="Generate eval set for extraction")
    parser.add_argument("--n-per-job", type=int, default=20, help="Diverse titles per job")
    parser.add_argument("--max-jobs", type=int, default=10, help="Max jobs to sample from")
    parser.add_argument("--skeleton-samples", type=int, default=100,
                        help="Diverse titles for skeleton generation per job")
    args = parser.parse_args()

    settings = load_settings()
    conn = get_db_connection(settings)

    # Pick diverse jobs by category
    cursor = conn.cursor()
    cursor.execute(
        "SELECT sj.Id, sj.SearchTerm, COUNT(*) as Cnt "
        "FROM Listings l JOIN ScrapeJobs sj ON l.ScrapeJobId = sj.Id "
        "WHERE l.Title IS NOT NULL "
        "GROUP BY sj.Id, sj.SearchTerm "
        "HAVING COUNT(*) > 100 "
        "ORDER BY NEWID()"  # random order
    )
    jobs = [(r[0], r[1], r[2]) for r in cursor.fetchall()][:args.max_jobs]
    print(f"Selected {len(jobs)} jobs for eval set:")
    for jid, term, cnt in jobs:
        print(f"  Job {jid}: {term} ({cnt} listings)")

    eval_data = []

    for job_id, search_term, count in jobs:
        print(f"\n{'='*60}")
        print(f"Job {job_id}: {search_term}")
        print(f"{'='*60}")

        # Get diverse titles for this job
        samples = get_diverse_titles_for_job(conn, job_id, search_term, n=args.n_per_job)
        if not samples:
            print(f"  No titles found, skipping")
            continue

        titles_for_skeleton = get_diverse_titles_for_job(conn, job_id, search_term, n=args.skeleton_samples)

        # Generate skeleton for this product
        skeleton_path = EVAL_DIR / f"skeleton_{job_id}.json"
        if skeleton_path.exists():
            print(f"  Loading saved skeleton")
            with open(skeleton_path) as f:
                skeleton = json.load(f)
        else:
            skeleton = generate_skeleton(
                search_term,
                [s["title"] for s in titles_for_skeleton],
                count,
                settings,
            )
            with open(skeleton_path, "w") as f:
                json.dump(skeleton, f, indent=2)

        # Label titles with GPT-5-nano
        print(f"  Labeling {len(samples)} titles with GPT-5-nano...")
        t0 = time.time()
        labels = label_with_gpt5([s["title"] for s in samples], skeleton, settings)
        elapsed = time.time() - t0
        print(f"  Labeled in {elapsed:.1f}s")

        for sample, label in zip(samples, labels):
            eval_data.append({
                **sample,
                "skeleton_axes": [ax["name"] for ax in skeleton["axes"]],
                "expected": label,
            })

    conn.close()

    # Save eval set
    eval_path = EVAL_DIR / "eval_set.json"
    with open(eval_path, "w") as f:
        json.dump({
            "generated_at": time.strftime("%Y-%m-%d %H:%M:%S"),
            "total_samples": len(eval_data),
            "jobs": len(jobs),
            "samples": eval_data,
        }, f, indent=2)

    print(f"\n{'='*60}")
    print(f"Eval set saved: {eval_path}")
    print(f"Total samples: {len(eval_data)} across {len(jobs)} jobs")
    print(f"{'='*60}")


if __name__ == "__main__":
    main()
