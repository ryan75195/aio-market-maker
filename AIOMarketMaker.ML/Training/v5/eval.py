"""Batch live test: One listing per category, BERT vs cosine comparison."""

import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

import json
import time
import pyodbc
import torch
from pinecone import Pinecone
from transformers import AutoTokenizer, AutoModelForSequenceClassification

MODEL_PATH = "E:/Dev/ml-training/variant-classifier/v5/pytorch"  # NOTE: v5 model was deleted during cleanup
PINECONE_INDEX = "arbitrage"
DB_CONN = (
    r"Driver={ODBC Driver 17 for SQL Server};"
    r"Server=(localdb)\MSSQLLocalDB;"
    r"Database=AIOMarketMaker;"
    r"Trusted_Connection=yes;"
)
TOP_K = 50
COSINE_THRESHOLD = 0.80


def load_pinecone_key():
    # Resolve local.settings.json relative to the repo root (this file is at
    # AIOMarketMaker.ML/Training/v5/eval.py — three levels under the repo).
    import os
    repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
    path = os.environ.get(
        "AIOMM_LOCAL_SETTINGS",
        os.path.join(repo_root, "AIOMarketMaker.Console", "local.settings.json"),
    )
    with open(path) as f:
        return json.load(f)["Values"]["Pinecone:ApiKey"]


def get_categories(cursor):
    cursor.execute("""
        SELECT sj.Id, sj.SearchTerm
        FROM ScrapeJobs sj
        JOIN Listings l ON l.ScrapeJobId = sj.Id
        WHERE l.Description IS NOT NULL AND LEN(l.Description) > 50
        GROUP BY sj.Id, sj.SearchTerm
        HAVING COUNT(*) >= 50
        ORDER BY sj.SearchTerm
    """)
    return [(row[0], row[1]) for row in cursor.fetchall()]


def get_random_listings_for_job(cursor, job_id, count=10):
    cursor.execute("""
        SELECT TOP (?) l.ListingId, l.Title, l.Description, l.Price
        FROM Listings l
        WHERE l.ScrapeJobId = ? AND l.Description IS NOT NULL AND LEN(l.Description) > 50
        ORDER BY NEWID()
    """, (count, job_id))
    return [
        {
            "listing_id": str(row[0]),
            "title": row[1],
            "description": row[2] or "",
            "price": float(row[3]) if row[3] else None,
        }
        for row in cursor.fetchall()
    ]


def get_listings_by_ids(cursor, listing_ids):
    if not listing_ids:
        return {}
    placeholders = ",".join(["?" for _ in listing_ids])
    cursor.execute(
        f"""
        SELECT l.ListingId, l.Title, l.Description, l.Price, l.Condition, l.ListingStatus
        FROM Listings l
        WHERE l.ListingId IN ({placeholders})
    """,
        listing_ids,
    )
    results = {}
    for row in cursor.fetchall():
        results[str(row[0])] = {
            "listing_id": str(row[0]),
            "title": row[1],
            "description": row[2] or "",
            "price": float(row[3]) if row[3] else None,
            "condition": row[4],
            "status": row[5],
        }
    return results


def classify_pairs(model, tokenizer, anchor, candidates, device, max_length=256):
    results = []
    batch_size = 16
    candidate_list = list(candidates.values())

    for i in range(0, len(candidate_list), batch_size):
        batch = candidate_list[i : i + batch_size]
        text_as = [f"{anchor['title']} | {anchor['description'][:500]}" for _ in batch]
        text_bs = [f"{c['title']} | {c['description'][:500]}" for c in batch]

        inputs = tokenizer(
            text_as, text_bs,
            max_length=max_length, truncation=True, padding=True,
            return_tensors="pt",
        ).to(device)

        with torch.no_grad():
            outputs = model(**inputs)
            probs = torch.softmax(outputs.logits, dim=-1)
            preds = torch.argmax(probs, dim=-1)

        for j, c in enumerate(batch):
            pred = preds[j].item()
            conf = probs[j][pred].item()
            results.append({
                **c,
                "prediction": "SAME" if pred == 1 else "DIFFERENT",
                "confidence": conf,
            })

    return results


def main():
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Device: {device}")

    print("Loading BERT model...")
    tokenizer = AutoTokenizer.from_pretrained(MODEL_PATH)
    model = AutoModelForSequenceClassification.from_pretrained(MODEL_PATH).to(device)
    model.eval()

    print("Connecting to Pinecone...")
    pc = Pinecone(api_key=load_pinecone_key())
    index = pc.Index(PINECONE_INDEX)

    print("Connecting to database...")
    conn = pyodbc.connect(DB_CONN)
    cursor = conn.cursor()

    categories = get_categories(cursor)
    print(f"Found {len(categories)} categories with 50+ listings\n")

    # Results accumulator
    all_results = []

    for cat_idx, (job_id, search_term) in enumerate(categories):
        print(f"[{cat_idx+1}/{len(categories)}] {search_term}...", end=" ", flush=True)

        # Find an indexed listing
        candidates_pool = get_random_listings_for_job(cursor, job_id, count=10)
        anchor = None
        anchor_vector = None

        for c in candidates_pool:
            fetch_result = index.fetch(ids=[c["listing_id"]])
            if c["listing_id"] in fetch_result.vectors:
                anchor = c
                anchor_vector = fetch_result.vectors[c["listing_id"]].values
                break

        if not anchor:
            print("SKIP (not indexed)")
            continue

        # Query Pinecone
        query_result = index.query(vector=anchor_vector, top_k=TOP_K + 1, include_metadata=True)
        matches = [m for m in query_result.matches if m.id != anchor["listing_id"]][:TOP_K]

        if len(matches) < 10:
            print(f"SKIP (only {len(matches)} matches)")
            continue

        score_lookup = {m.id: m.score for m in matches}

        # Fetch from DB
        candidate_ids = [m.id for m in matches]
        candidate_listings = get_listings_by_ids(cursor, candidate_ids)

        if len(candidate_listings) < 10:
            print(f"SKIP (only {len(candidate_listings)} in DB)")
            continue

        # BERT classification
        results = classify_pairs(model, tokenizer, anchor, candidate_listings, device)
        for r in results:
            r["similarity_score"] = score_lookup.get(r["listing_id"], 0)

        bert_same = [r for r in results if r["prediction"] == "SAME"]
        bert_diff = [r for r in results if r["prediction"] == "DIFFERENT"]

        # Cosine threshold comparison
        cosine_same = [r for r in results if r["similarity_score"] >= COSINE_THRESHOLD]
        cosine_diff = [r for r in results if r["similarity_score"] < COSINE_THRESHOLD]

        # Disagreements (using BERT as reference)
        bert_same_ids = {r["listing_id"] for r in bert_same}
        cosine_same_ids = {r["listing_id"] for r in cosine_same}

        # False positives: cosine says same, BERT says different
        false_pos = cosine_same_ids - bert_same_ids
        # False negatives: BERT says same, cosine misses
        false_neg = bert_same_ids - cosine_same_ids

        cosine_precision = len(cosine_same_ids & bert_same_ids) / len(cosine_same_ids) if cosine_same_ids else 1.0
        cosine_recall = len(cosine_same_ids & bert_same_ids) / len(bert_same_ids) if bert_same_ids else 1.0

        # High-confidence BERT stats
        high_conf_same = [r for r in bert_same if r["confidence"] >= 0.80]
        high_conf_diff = [r for r in bert_diff if r["confidence"] >= 0.80]
        low_conf = [r for r in results if r["confidence"] < 0.80]

        # Example disagreements
        high_sim_rejected = sorted(
            [r for r in results if r["similarity_score"] >= COSINE_THRESHOLD and r["prediction"] == "DIFFERENT"],
            key=lambda x: x["confidence"], reverse=True,
        )

        cat_result = {
            "category": search_term,
            "anchor_title": anchor["title"][:60],
            "anchor_price": anchor["price"],
            "total_candidates": len(results),
            "bert_same": len(bert_same),
            "bert_diff": len(bert_diff),
            "cosine_same": len(cosine_same),
            "cosine_diff": len(cosine_diff),
            "cosine_precision": cosine_precision,
            "cosine_recall": cosine_recall,
            "false_pos": len(false_pos),
            "false_neg": len(false_neg),
            "high_conf_same": len(high_conf_same),
            "high_conf_diff": len(high_conf_diff),
            "low_conf": len(low_conf),
            "sim_range": f"{matches[0].score:.3f}-{matches[-1].score:.3f}",
            "top_rejection": high_sim_rejected[0]["title"][:50] if high_sim_rejected else None,
            "top_rejection_sim": high_sim_rejected[0]["similarity_score"] if high_sim_rejected else None,
        }

        all_results.append(cat_result)

        # Price spread analysis
        same_prices = [r["price"] for r in bert_same if r["price"]]
        price_spread = ""
        if same_prices and len(same_prices) >= 2:
            ratio = max(same_prices) / min(same_prices) if min(same_prices) > 0 else 0
            price_spread = f" price_ratio={ratio:.1f}x"

        print(
            f"BERT: {len(bert_same):>2}S/{len(bert_diff):>2}D | "
            f"Cosine>=0.80: {len(cosine_same):>2}S | "
            f"FP={len(false_pos):>2} FN={len(false_neg):>2} | "
            f"P={cosine_precision:.0%} R={cosine_recall:.0%} | "
            f"sim={matches[0].score:.3f}-{matches[-1].score:.3f}"
            f"{price_spread}"
        )

    # ===== AGGREGATE SUMMARY =====
    print(f"\n{'='*100}")
    print(f"AGGREGATE RESULTS ACROSS {len(all_results)} CATEGORIES")
    print(f"{'='*100}")

    total_candidates = sum(r["total_candidates"] for r in all_results)
    total_bert_same = sum(r["bert_same"] for r in all_results)
    total_bert_diff = sum(r["bert_diff"] for r in all_results)
    total_cosine_same = sum(r["cosine_same"] for r in all_results)
    total_fp = sum(r["false_pos"] for r in all_results)
    total_fn = sum(r["false_neg"] for r in all_results)
    total_high_conf_same = sum(r["high_conf_same"] for r in all_results)
    total_low_conf = sum(r["low_conf"] for r in all_results)

    avg_precision = sum(r["cosine_precision"] for r in all_results) / len(all_results)
    avg_recall = sum(r["cosine_recall"] for r in all_results) / len(all_results)

    print(f"\n  Total candidates evaluated:  {total_candidates}")
    print(f"  BERT same / different:       {total_bert_same} / {total_bert_diff}")
    print(f"  Cosine>=0.80 same:           {total_cosine_same}")
    print(f"  Cosine false positives (FP): {total_fp} (cosine says same, BERT disagrees)")
    print(f"  Cosine false negatives (FN): {total_fn} (BERT says same, cosine<0.80)")
    print(f"  Avg cosine precision:        {avg_precision:.1%}")
    print(f"  Avg cosine recall:           {avg_recall:.1%}")
    print(f"  BERT high-conf same (>=80%): {total_high_conf_same}/{total_bert_same} ({total_high_conf_same/total_bert_same*100:.0f}%)" if total_bert_same else "")
    print(f"  Low-confidence predictions:  {total_low_conf}/{total_candidates} ({total_low_conf/total_candidates*100:.0f}%)")

    # Per-category detail table
    print(f"\n  {'Category':<30} | {'BERT S/D':>8} | {'Cos S':>5} | {'FP':>3} | {'FN':>3} | {'Prec':>5} | {'Rec':>5} | {'LowConf':>7} | Sim Range")
    print(f"  {'-'*30}-+-{'-'*8}-+-{'-'*5}-+-{'-'*3}-+-{'-'*3}-+-{'-'*5}-+-{'-'*5}-+-{'-'*7}-+-{'-'*13}")

    for r in sorted(all_results, key=lambda x: x["cosine_precision"]):
        print(
            f"  {r['category']:<30} | "
            f"{r['bert_same']:>2}/{r['bert_diff']:<5} | "
            f"{r['cosine_same']:>5} | "
            f"{r['false_pos']:>3} | "
            f"{r['false_neg']:>3} | "
            f"{r['cosine_precision']:>4.0%} | "
            f"{r['cosine_recall']:>4.0%} | "
            f"{r['low_conf']:>7} | "
            f"{r['sim_range']}"
        )

    # Categories where BERT adds most value (lowest cosine precision = most FPs filtered)
    print(f"\n  TOP 10 CATEGORIES WHERE BERT ADDS MOST VALUE (lowest cosine precision):")
    for r in sorted(all_results, key=lambda x: x["cosine_precision"])[:10]:
        print(
            f"    {r['category']:<30} cosine_prec={r['cosine_precision']:.0%} "
            f"FP={r['false_pos']:>2} | "
            f"e.g. rejected: {r['top_rejection']}" if r['top_rejection'] else
            f"    {r['category']:<30} cosine_prec={r['cosine_precision']:.0%} FP={r['false_pos']:>2}"
        )

    # Categories where BERT is uncertain (most low-confidence)
    print(f"\n  TOP 10 CATEGORIES WITH MOST UNCERTAINTY (low-confidence predictions):")
    for r in sorted(all_results, key=lambda x: x["low_conf"], reverse=True)[:10]:
        print(
            f"    {r['category']:<30} low_conf={r['low_conf']:>2}/{r['total_candidates']} "
            f"({r['low_conf']/r['total_candidates']*100:.0f}%)"
        )

    # Save full results
    output_path = "E:/Dev/ml-training/variant-classifier/batch_test_results.json"
    with open(output_path, "w") as f:
        json.dump(all_results, f, indent=2)
    print(f"\n  Full results saved to: {output_path}")

    conn.close()


if __name__ == "__main__":
    main()
