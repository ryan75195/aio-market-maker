"""
Smoke test: LLM structured-output classifier vs ONNX on real DB pairs.
Pulls 20 random pairs (10 comparable, 10 not-comparable per ONNX),
runs them through gpt-4o-mini with structured outputs, and compares.
"""

import asyncio
import json
import sys
import pyodbc

try:
    from openai import AsyncOpenAI
except ImportError:
    import subprocess
    subprocess.check_call([sys.executable, "-m", "pip", "install", "openai", "-q"])
    from openai import AsyncOpenAI

# --- Config ---
with open("AIOMarketMaker/AIOMarketMaker.Console/local.settings.json") as f:
    settings = json.load(f)
API_KEY = settings["Values"]["OpenAi:ApiKey"]
MODEL = "gpt-4o-mini"
SAMPLE_PER_GROUP = 10

client = AsyncOpenAI(api_key=API_KEY)

SYSTEM_PROMPT = None  # Will be loaded from the actual C# source

# Mirror the C# prompt exactly (read it fresh so it matches production)
def load_system_prompt():
    """Build the system prompt with interpolated enum values, matching production."""
    return """You are a pricing comparability classifier for eBay listings. Given two listings, determine if they are COMPARABLE FOR PRICING — meaning one listing's sold price is a valid price reference for the other.

Two listings are comparable when they are the same product in a similar enough state that a buyer would expect to pay roughly the same price.

RULES:

1. SAME PRODUCT: Must be the same product, model, and key specs (storage, size, color when it affects price).
2. CONDITION BAND: Listings must be in a similar condition tier to be comparable. Use these bands:
   - New/Sealed: factory sealed, BNIB, brand new, unworn with tags
   - Excellent: like new, grade A, pristine, mint, barely used, near mint
   - Good: grade B, pre-owned, used, light wear, good condition
   - Fair/Poor: grade C or below, heavy wear, damaged, scratches, dents, for parts, not working
   Listings in the SAME band or ADJACENT bands (e.g., New and Excellent, or Good and Fair) are comparable.
   Listings TWO OR MORE bands apart are NOT comparable (e.g., New vs Good, Excellent vs Fair/Poor).
   When no condition is stated, assume Good (typical eBay pre-owned).
3. BUNDLES are NOT comparable. If one listing includes accessories (keyboard, case, controller, extra lenses) that the other does not, they are NOT comparable — the bundle commands a higher price.
4. QUANTITY must match. A single item is not comparable to a lot/bundle of multiple identical items.
5. SPECIAL EDITIONS are NOT comparable. Limited editions, collaboration colorways (e.g., Pokemon Edition Switch vs standard Switch OLED), anniversary editions differ in price.
6. STORAGE/RAM/CPU differences are NOT comparable (e.g., 128GB vs 256GB, i5 vs i7, M3 vs M3 Pro).
7. SIZE differences are NOT comparable (e.g., 40mm vs 44mm watch, PM vs MM bag).
8. ACCESSORIES vs FULL PRODUCTS are NOT comparable (e.g., "PS5 Disc Drive" accessory vs "PS5 Console").
9. MODIFICATIONS: Stock items are NOT comparable to modified/customized items (custom paint, aftermarket parts, engraved).
10. TRIVIAL differences are OK and do NOT make listings incomparable: manufacture year or purchase date (e.g., "2024" vs "2023" for the same reference/model), seller location, box condition, minor cosmetic detail, included cables, listing photos.
11. MISSING INFORMATION IS NOT A DIFFERENCE. Only compare what is explicitly stated. If one listing says "Wi-Fi 128GB" and the other just says "128GB" without mentioning connectivity, do NOT assume they differ — the missing detail was simply not listed. Never infer specs, features, or conditions that are not explicitly written in the listing.

First explain your reason (under 20 words), then set verdict to "same" if the listings are comparable for pricing, "different" if not, or "uncertain" if there is insufficient detail to determine."""


RESPONSE_SCHEMA = {
    "type": "object",
    "properties": {
        "reason": {"type": "string"},
        "verdict": {"type": "string", "enum": ["same", "different", "uncertain"]}
    },
    "required": ["reason", "verdict"],
    "additionalProperties": False
}


def load_pairs():
    """Pull random pairs from the DB — 10 comparable, 10 not-comparable."""
    conn = pyodbc.connect(
        "Driver={ODBC Driver 17 for SQL Server};"
        "Server=(localdb)\\MSSQLLocalDB;"
        "Database=AIOMarketMaker;"
        "Trusted_Connection=yes;"
    )
    cursor = conn.cursor()

    query = """
    WITH ComparableSample AS (
        SELECT TOP ({sample}) r.Id AS RelId, r.ListingIdA, r.ListingIdB,
               r.IsComparable, r.SimilarityScore,
               a.Title AS TitleA, a.Description AS DescA,
               b.Title AS TitleB, b.Description AS DescB,
               sj.SearchTerm
        FROM ListingRelationships r
        INNER JOIN Listings a ON a.Id = r.ListingIdA
        INNER JOIN Listings b ON b.Id = r.ListingIdB
        LEFT JOIN ScrapeJobs sj ON sj.Id = a.ScrapeJobId
        WHERE r.IsComparable = 1
          AND a.Title IS NOT NULL AND b.Title IS NOT NULL
          AND LEN(a.Title) > 10 AND LEN(b.Title) > 10
        ORDER BY NEWID()
    ),
    NotComparableSample AS (
        SELECT TOP ({sample}) r.Id AS RelId, r.ListingIdA, r.ListingIdB,
               r.IsComparable, r.SimilarityScore,
               a.Title AS TitleA, a.Description AS DescA,
               b.Title AS TitleB, b.Description AS DescB,
               sj.SearchTerm
        FROM ListingRelationships r
        INNER JOIN Listings a ON a.Id = r.ListingIdA
        INNER JOIN Listings b ON b.Id = r.ListingIdB
        LEFT JOIN ScrapeJobs sj ON sj.Id = a.ScrapeJobId
        WHERE r.IsComparable = 0
          AND a.Title IS NOT NULL AND b.Title IS NOT NULL
          AND LEN(a.Title) > 10 AND LEN(b.Title) > 10
        ORDER BY NEWID()
    )
    SELECT * FROM ComparableSample
    UNION ALL
    SELECT * FROM NotComparableSample
    """.format(sample=SAMPLE_PER_GROUP)

    cursor.execute(query)
    columns = [desc[0] for desc in cursor.description]
    rows = [dict(zip(columns, row)) for row in cursor.fetchall()]
    conn.close()
    return rows


async def classify_pair(pair, index, total):
    """Run a single pair through the LLM classifier."""
    user_prompt = f"""Listing A:
Title: {pair['TitleA']}
Description: {pair['DescA'] or ''}

Listing B:
Title: {pair['TitleB']}
Description: {pair['DescB'] or ''}"""

    try:
        response = await client.chat.completions.create(
            model=MODEL,
            messages=[
                {"role": "system", "content": load_system_prompt()},
                {"role": "user", "content": user_prompt},
            ],
            temperature=0.0,
            response_format={
                "type": "json_schema",
                "json_schema": {
                    "name": "ClassifierResponse",
                    "strict": True,
                    "schema": RESPONSE_SCHEMA
                }
            },
            max_completion_tokens=200,
        )
        result = json.loads(response.choices[0].message.content)
        return {
            **pair,
            "llm_verdict": result["verdict"],
            "llm_reason": result["reason"],
            "tokens_in": response.usage.prompt_tokens,
            "tokens_out": response.usage.completion_tokens,
        }
    except Exception as e:
        return {**pair, "llm_verdict": "error", "llm_reason": str(e), "tokens_in": 0, "tokens_out": 0}


async def main():
    pairs = load_pairs()
    print(f"Loaded {len(pairs)} pairs ({SAMPLE_PER_GROUP} comparable, {SAMPLE_PER_GROUP} not-comparable)\n")

    tasks = [classify_pair(p, i, len(pairs)) for i, p in enumerate(pairs)]
    results = await asyncio.gather(*tasks)

    # Stats
    agree = 0
    disagree = 0
    uncertain_count = 0
    total_in = 0
    total_out = 0

    for r in results:
        total_in += r["tokens_in"]
        total_out += r["tokens_out"]

        onnx_label = "same" if r["IsComparable"] else "different"
        llm = r["llm_verdict"]

        if llm == "uncertain":
            uncertain_count += 1
            marker = "???"
        elif llm == onnx_label:
            agree += 1
            marker = " OK"
        else:
            disagree += 1
            marker = "!!!"

        category = r.get("SearchTerm", "?")[:25]
        print(f"[{marker}] ONNX={onnx_label:<9} LLM={llm:<9} | {category}")
        print(f"      A: {r['TitleA'][:80]}")
        print(f"      B: {r['TitleB'][:80]}")
        print(f"      Reason: {r['llm_reason']}")
        print()

    print("=" * 70)
    print(f"Agreement:   {agree}/{len(results)} ({agree/len(results)*100:.0f}%)")
    print(f"Disagreement:{disagree}/{len(results)} ({disagree/len(results)*100:.0f}%)")
    print(f"Uncertain:   {uncertain_count}/{len(results)} ({uncertain_count/len(results)*100:.0f}%)")
    print(f"Tokens:      {total_in:,} in / {total_out:,} out")

    # Show disagreements summary
    disagreements = [r for r in results if r["llm_verdict"] not in ("uncertain", "error")
                     and r["llm_verdict"] != ("same" if r["IsComparable"] else "different")]
    if disagreements:
        print(f"\n{'=' * 70}")
        print(f"DISAGREEMENTS ({len(disagreements)}):")
        for r in disagreements:
            onnx_label = "same" if r["IsComparable"] else "different"
            print(f"\n  ONNX={onnx_label}, LLM={r['llm_verdict']}: {r['llm_reason']}")
            print(f"  A: {r['TitleA'][:90]}")
            print(f"  B: {r['TitleB'][:90]}")


if __name__ == "__main__":
    import io
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
    asyncio.run(main())
