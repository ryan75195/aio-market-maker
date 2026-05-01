"""Live model test: Pick a random listing, find top-50 similar via Pinecone, classify with BERT."""

import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

import json
import pyodbc
import torch
from pinecone import Pinecone
from transformers import AutoTokenizer, AutoModelForSequenceClassification

# Config
MODEL_PATH = "E:/Dev/ml-training/variant-classifier/v5/pytorch"  # NOTE: v5 model was deleted during cleanup
PINECONE_INDEX = "arbitrage"
DB_CONN = (
    r"Driver={ODBC Driver 17 for SQL Server};"
    r"Server=(localdb)\MSSQLLocalDB;"
    r"Database=AIOMarketMaker;"
    r"Trusted_Connection=yes;"
)
TOP_K = 50


def load_pinecone_key():
    # Resolve local.settings.json relative to the repo root.
    import os
    repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
    settings_path = os.environ.get(
        "AIOMM_LOCAL_SETTINGS",
        os.path.join(repo_root, "AIOMarketMaker.Console", "local.settings.json"),
    )
    with open(settings_path) as f:
        settings = json.load(f)
    return settings["Values"]["Pinecone:ApiKey"]


def get_random_listing(cursor, retries=20):
    """Pick a random listing that exists in Pinecone."""
    cursor.execute("""
        SELECT TOP 20 l.ListingId, l.Title, l.Description, l.Price, sj.SearchTerm
        FROM Listings l
        JOIN ScrapeJobs sj ON l.ScrapeJobId = sj.Id
        WHERE l.Description IS NOT NULL AND LEN(l.Description) > 50
        ORDER BY NEWID()
    """)
    rows = cursor.fetchall()
    return [
        {
            "listing_id": str(row[0]),
            "title": row[1],
            "description": row[2] or "",
            "price": float(row[3]) if row[3] else None,
            "search_term": row[4],
        }
        for row in rows
    ]


def get_listings_by_ids(cursor, listing_ids):
    """Fetch listings from DB by their listing IDs."""
    placeholders = ",".join(["?" for _ in listing_ids])
    cursor.execute(
        f"""
        SELECT l.ListingId, l.Title, l.Description, l.Price, l.Condition,
               l.ListingStatus, sj.SearchTerm
        FROM Listings l
        JOIN ScrapeJobs sj ON l.ScrapeJobId = sj.Id
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
            "search_term": row[6],
        }
    return results


def classify_pairs(model, tokenizer, anchor, candidates, device, max_length=256):
    """Classify all pairs in a batch."""
    results = []
    # Process in mini-batches of 16
    batch_size = 16
    candidate_list = list(candidates.values())

    for i in range(0, len(candidate_list), batch_size):
        batch = candidate_list[i : i + batch_size]
        text_as = []
        text_bs = []
        for c in batch:
            text_as.append(f"{anchor['title']} | {anchor['description'][:500]}")
            text_bs.append(f"{c['title']} | {c['description'][:500]}")

        inputs = tokenizer(
            text_as,
            text_bs,
            max_length=max_length,
            truncation=True,
            padding=True,
            return_tensors="pt",
        ).to(device)

        with torch.no_grad():
            outputs = model(**inputs)
            probs = torch.softmax(outputs.logits, dim=-1)
            preds = torch.argmax(probs, dim=-1)

        for j, c in enumerate(batch):
            pred = preds[j].item()
            conf = probs[j][pred].item()
            results.append(
                {
                    **c,
                    "prediction": "SAME" if pred == 1 else "DIFFERENT",
                    "confidence": conf,
                }
            )

    return results


def main():
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Device: {device}")

    # Load model
    print("Loading BERT model...")
    tokenizer = AutoTokenizer.from_pretrained(MODEL_PATH)
    model = AutoModelForSequenceClassification.from_pretrained(MODEL_PATH).to(device)
    model.eval()
    print("Model loaded.\n")

    # Connect to Pinecone
    print("Connecting to Pinecone...")
    api_key = load_pinecone_key()
    pc = Pinecone(api_key=api_key)
    index = pc.Index(PINECONE_INDEX)
    print("Connected.\n")

    # Connect to DB
    print("Connecting to database...")
    conn = pyodbc.connect(DB_CONN)
    cursor = conn.cursor()
    print("Connected.\n")

    # Pick random listing that exists in Pinecone
    print("Finding a random indexed listing...")
    candidates_pool = get_random_listing(cursor)
    anchor = None
    anchor_vector = None

    for candidate in candidates_pool:
        fetch_result = index.fetch(ids=[candidate["listing_id"]])
        if candidate["listing_id"] in fetch_result.vectors:
            anchor = candidate
            anchor_vector = fetch_result.vectors[candidate["listing_id"]].values
            break

    if not anchor:
        print("ERROR: Could not find any indexed listing after 20 attempts.")
        return

    print(f"\n{'='*90}")
    print(f"ANCHOR LISTING")
    print(f"{'='*90}")
    print(f"  ID:       {anchor['listing_id']}")
    print(f"  Category: {anchor['search_term']}")
    print(f"  Title:    {anchor['title']}")
    if anchor["price"]:
        print(f"  Price:    ${anchor['price']:.2f}")
    print(f"  Desc:     {anchor['description'][:200]}...")

    # Query for top K similar
    print(f"\nQuerying top {TOP_K} similar listings from Pinecone...")
    query_result = index.query(
        vector=anchor_vector, top_k=TOP_K + 1, include_metadata=True
    )

    # Filter out anchor itself
    matches = [
        m for m in query_result.matches if m.id != anchor["listing_id"]
    ][:TOP_K]

    print(
        f"Got {len(matches)} candidates "
        f"(sim scores: {matches[0].score:.3f} - {matches[-1].score:.3f})"
    )

    # Build score lookup
    score_lookup = {m.id: m.score for m in matches}

    # Fetch candidate details from DB
    candidate_ids = [m.id for m in matches]
    candidate_listings = get_listings_by_ids(cursor, candidate_ids)
    print(f"Found {len(candidate_listings)}/{len(matches)} in database.\n")

    # Classify all pairs
    print(f"Classifying {len(candidate_listings)} pairs through BERT model...")
    results = classify_pairs(model, tokenizer, anchor, candidate_listings, device)

    # Attach similarity scores
    for r in results:
        r["similarity_score"] = score_lookup.get(r["listing_id"], 0)

    same_variants = sorted(
        [r for r in results if r["prediction"] == "SAME"],
        key=lambda x: x["confidence"],
        reverse=True,
    )
    different_variants = sorted(
        [r for r in results if r["prediction"] == "DIFFERENT"],
        key=lambda x: x["similarity_score"],
        reverse=True,
    )

    # Report: SAME group
    print(f"\n{'='*90}")
    print(f"SAME VARIANT GROUP ({len(same_variants)} listings)")
    print(f"{'='*90}")

    if not same_variants:
        print("  (none found)")
    else:
        for i, r in enumerate(same_variants, 1):
            price_str = f"${r['price']:.2f}" if r["price"] else "N/A"
            print(
                f"  {i:>2}. [{r['confidence']:.0%} conf, {r['similarity_score']:.3f} sim] "
                f"{price_str:>10} | {r['condition'] or '?':15} | {r['title'][:70]}"
            )

    # Report: DIFFERENT (top 10)
    print(f"\n{'='*90}")
    print(f"DIFFERENT VARIANT ({len(different_variants)} listings) - top 10 by similarity")
    print(f"{'='*90}")

    for i, r in enumerate(different_variants[:10], 1):
        price_str = f"${r['price']:.2f}" if r["price"] else "N/A"
        print(
            f"  {i:>2}. [{r['confidence']:.0%} conf, {r['similarity_score']:.3f} sim] "
            f"{price_str:>10} | {r['condition'] or '?':15} | {r['title'][:70]}"
        )

    if len(different_variants) > 10:
        print(f"  ... and {len(different_variants) - 10} more")

    # Summary
    print(f"\n{'='*90}")
    print(f"SUMMARY")
    print(f"{'='*90}")
    print(f"  Anchor:     {anchor['title'][:80]}")
    print(f"  Category:   {anchor['search_term']}")
    if anchor["price"]:
        print(f"  Anchor $:   ${anchor['price']:.2f}")
    print(f"  Same:       {len(same_variants)}/{len(candidate_listings)} candidates")
    print(f"  Different:  {len(different_variants)}/{len(candidate_listings)} candidates")

    if same_variants:
        prices = [r["price"] for r in same_variants if r["price"]]
        if prices:
            print(f"  Same price range: ${min(prices):.2f} - ${max(prices):.2f}")

    # ===== BERT vs Cosine Threshold Comparison =====
    print(f"\n{'='*90}")
    print(f"BERT vs PURE COSINE THRESHOLD COMPARISON")
    print(f"{'='*90}")

    # Use BERT predictions as ground truth (best we have without manual labels)
    bert_same = {r["listing_id"] for r in results if r["prediction"] == "SAME"}
    bert_diff = {r["listing_id"] for r in results if r["prediction"] == "DIFFERENT"}

    thresholds = [0.70, 0.75, 0.80, 0.85, 0.90]
    print(f"\n  Using BERT as reference ({len(bert_same)} same, {len(bert_diff)} diff):")
    print(f"  {'Threshold':>10} | {'Cosine Same':>11} | {'Agree':>5} | {'Cosine adds (FP)':>17} | {'Cosine misses (FN)':>19} | {'Precision':>9} | {'Recall':>6}")
    print(f"  {'-'*10}-+-{'-'*11}-+-{'-'*5}-+-{'-'*17}-+-{'-'*19}-+-{'-'*9}-+-{'-'*6}")

    for thresh in thresholds:
        cosine_same = {r["listing_id"] for r in results if r["similarity_score"] >= thresh}
        agree = cosine_same & bert_same
        false_pos = cosine_same - bert_same  # cosine says same, BERT says diff
        false_neg = bert_same - cosine_same  # BERT says same, cosine misses

        precision = len(agree) / len(cosine_same) if cosine_same else 0
        recall = len(agree) / len(bert_same) if bert_same else 0

        print(
            f"  {thresh:>10.2f} | {len(cosine_same):>11} | {len(agree):>5} | "
            f"{len(false_pos):>17} | {len(false_neg):>19} | "
            f"{precision:>8.0%} | {recall:>5.0%}"
        )

    # Show the disagreement zone: items where BERT and cosine 0.80 disagree
    cosine_80_same = {r["listing_id"] for r in results if r["similarity_score"] >= 0.80}

    # High sim but BERT says different
    high_sim_rejected = [
        r for r in results
        if r["similarity_score"] >= 0.80 and r["prediction"] == "DIFFERENT"
    ]
    high_sim_rejected.sort(key=lambda x: x["similarity_score"], reverse=True)

    # Low sim but BERT says same
    low_sim_accepted = [
        r for r in results
        if r["similarity_score"] < 0.80 and r["prediction"] == "SAME"
    ]
    low_sim_accepted.sort(key=lambda x: x["confidence"], reverse=True)

    if high_sim_rejected:
        print(f"\n  BERT REJECTS despite high cosine (sim >= 0.80):")
        for r in high_sim_rejected[:5]:
            price_str = f"${r['price']:.2f}" if r["price"] else "N/A"
            print(
                f"    sim={r['similarity_score']:.3f} bert={r['prediction']} "
                f"({r['confidence']:.0%}) {price_str:>8} | {r['title'][:65]}"
            )

    if low_sim_accepted:
        print(f"\n  BERT ACCEPTS despite low cosine (sim < 0.80):")
        for r in low_sim_accepted[:5]:
            price_str = f"${r['price']:.2f}" if r["price"] else "N/A"
            print(
                f"    sim={r['similarity_score']:.3f} bert={r['prediction']} "
                f"({r['confidence']:.0%}) {price_str:>8} | {r['title'][:65]}"
            )

    conn.close()


if __name__ == "__main__":
    main()
