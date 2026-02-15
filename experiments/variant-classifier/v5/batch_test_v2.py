"""Batch test v2: Output actual titles per category for manual quality judgment."""

import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

import json
import pyodbc
import torch
from pinecone import Pinecone
from transformers import AutoTokenizer, AutoModelForSequenceClassification

MODEL_PATH = "E:/Dev/ml-training/variant-classifier/model_v5"
PINECONE_INDEX = "arbitrage"
DB_CONN = (
    r"Driver={ODBC Driver 17 for SQL Server};"
    r"Server=(localdb)\MSSQLLocalDB;"
    r"Database=AIOMarketMaker;"
    r"Trusted_Connection=yes;"
)
TOP_K = 50
COSINE_THRESHOLD = 0.80
BERT_CONF_THRESHOLD = 0.80


def load_pinecone_key():
    path = r"<REPO_ROOT>\AIOMarketMaker\AIOMarketMaker.Etl\local.settings.json"
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
            # Also get "same" probability specifically
            same_prob = probs[j][1].item()
            results.append({
                **c,
                "prediction": "SAME" if pred == 1 else "DIFFERENT",
                "confidence": conf,
                "same_prob": same_prob,
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
    print(f"Testing {len(categories)} categories\n")

    all_categories = []

    for cat_idx, (job_id, search_term) in enumerate(categories):
        sys.stdout.flush()
        print(f"[{cat_idx+1}/{len(categories)}] {search_term}...", end=" ", flush=True)

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
            print("SKIP")
            continue

        query_result = index.query(vector=anchor_vector, top_k=TOP_K + 1, include_metadata=True)
        matches = [m for m in query_result.matches if m.id != anchor["listing_id"]][:TOP_K]

        if len(matches) < 10:
            print(f"SKIP ({len(matches)} matches)")
            continue

        score_lookup = {m.id: m.score for m in matches}
        candidate_ids = [m.id for m in matches]
        candidate_listings = get_listings_by_ids(cursor, candidate_ids)

        if len(candidate_listings) < 10:
            print(f"SKIP ({len(candidate_listings)} in DB)")
            continue

        results = classify_pairs(model, tokenizer, anchor, candidate_listings, device)
        for r in results:
            r["similarity_score"] = score_lookup.get(r["listing_id"], 0)

        all_categories.append({
            "category": search_term,
            "anchor": anchor,
            "results": results,
        })

        bert_same = sum(1 for r in results if r["prediction"] == "SAME")
        bert_hc = sum(1 for r in results if r["prediction"] == "SAME" and r["confidence"] >= BERT_CONF_THRESHOLD)
        cos_same = sum(1 for r in results if r["similarity_score"] >= COSINE_THRESHOLD)
        print(f"done (SS:{cos_same}, BERT:{bert_same}, BERT>=80%:{bert_hc})")

    # ===== DETAILED OUTPUT =====
    print(f"\n\n{'#'*100}")
    print(f"DETAILED RESULTS: {len(all_categories)} CATEGORIES")
    print(f"{'#'*100}")

    for cat_data in all_categories:
        cat = cat_data["category"]
        anchor = cat_data["anchor"]
        results = cat_data["results"]

        # Sort by similarity score descending
        results_by_sim = sorted(results, key=lambda x: x["similarity_score"], reverse=True)

        # Approach 1: Pure SS (cosine >= 0.80)
        ss_group = [r for r in results_by_sim if r["similarity_score"] >= COSINE_THRESHOLD]

        # Approach 2: SS + BERT (confidence >= 0.80)
        bert_group = sorted(
            [r for r in results if r["prediction"] == "SAME" and r["confidence"] >= BERT_CONF_THRESHOLD],
            key=lambda x: x["confidence"], reverse=True,
        )

        # Approach 2b: BERT any confidence
        bert_all = sorted(
            [r for r in results if r["prediction"] == "SAME"],
            key=lambda x: x["confidence"], reverse=True,
        )

        price_str = f"${anchor['price']:.2f}" if anchor['price'] else "N/A"
        print(f"\n{'='*100}")
        print(f"[{cat}] ANCHOR: {price_str} | {anchor['title'][:85]}")
        print(f"{'='*100}")

        # SS results
        print(f"\n  --- APPROACH 1: Pure SS (cosine >= {COSINE_THRESHOLD}) --- [{len(ss_group)} results]")
        if not ss_group:
            print(f"  (none above threshold, top sim = {results_by_sim[0]['similarity_score']:.3f})")
        else:
            for i, r in enumerate(ss_group[:8], 1):
                p = f"${r['price']:.2f}" if r['price'] else "N/A"
                print(f"    {i:>2}. sim={r['similarity_score']:.3f} {p:>9} | {r['title'][:75]}")
            if len(ss_group) > 8:
                print(f"    ... +{len(ss_group)-8} more")

        # BERT high-confidence results
        print(f"\n  --- APPROACH 2: SS + BERT (conf >= {BERT_CONF_THRESHOLD:.0%}) --- [{len(bert_group)} results]")
        if not bert_group:
            print(f"  (none)")
        else:
            for i, r in enumerate(bert_group[:8], 1):
                p = f"${r['price']:.2f}" if r['price'] else "N/A"
                print(f"    {i:>2}. conf={r['confidence']:.0%} sim={r['similarity_score']:.3f} {p:>9} | {r['title'][:70]}")
            if len(bert_group) > 8:
                print(f"    ... +{len(bert_group)-8} more")

        # Low-confidence BERT accepts (50-80% — borderline)
        bert_borderline = [r for r in results if r["prediction"] == "SAME" and r["confidence"] < BERT_CONF_THRESHOLD]
        if bert_borderline:
            bert_borderline.sort(key=lambda x: x["confidence"], reverse=True)
            print(f"\n  --- BERT BORDERLINE (same but conf < 80%) --- [{len(bert_borderline)} results]")
            for i, r in enumerate(bert_borderline[:5], 1):
                p = f"${r['price']:.2f}" if r['price'] else "N/A"
                print(f"    {i:>2}. conf={r['confidence']:.0%} sim={r['similarity_score']:.3f} {p:>9} | {r['title'][:70]}")

        # Key disagreements: high-sim BERT rejects
        high_sim_rejected = [r for r in results if r["similarity_score"] >= COSINE_THRESHOLD and r["prediction"] == "DIFFERENT" and r["confidence"] >= 0.80]
        if high_sim_rejected:
            high_sim_rejected.sort(key=lambda x: x["similarity_score"], reverse=True)
            print(f"\n  --- BERT REJECTS (high sim, high conf different) --- [{len(high_sim_rejected)}]")
            for i, r in enumerate(high_sim_rejected[:5], 1):
                p = f"${r['price']:.2f}" if r['price'] else "N/A"
                print(f"    {i:>2}. conf={r['confidence']:.0%} sim={r['similarity_score']:.3f} {p:>9} | {r['title'][:70]}")

    # Save raw data for further analysis
    output_path = "E:/Dev/ml-training/variant-classifier/batch_test_v2_results.json"
    serializable = []
    for cat_data in all_categories:
        serializable.append({
            "category": cat_data["category"],
            "anchor_title": cat_data["anchor"]["title"],
            "anchor_price": cat_data["anchor"]["price"],
            "results": [
                {
                    "title": r["title"],
                    "price": r["price"],
                    "similarity_score": r["similarity_score"],
                    "prediction": r["prediction"],
                    "confidence": r["confidence"],
                    "same_prob": r["same_prob"],
                }
                for r in cat_data["results"]
            ],
        })
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(serializable, f, indent=2, ensure_ascii=False)
    print(f"\n\nRaw results saved to: {output_path}")

    conn.close()


if __name__ == "__main__":
    main()
