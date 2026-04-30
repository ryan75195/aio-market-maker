"""
Calculate exact batch API cost for relabeling v8 training set with gpt-5-mini.
Uses tiktoken to count actual tokens for the system prompt and a sample of user prompts.
"""
import csv
import json
import sys

try:
    import tiktoken
except ImportError:
    print("Installing tiktoken...")
    import subprocess
    subprocess.check_call([sys.executable, "-m", "pip", "install", "tiktoken", "-q"])
    import tiktoken

# --- Constants ---
MODEL = "gpt-4o-mini"  # tiktoken uses gpt-4o-mini encoding (same as gpt-5-mini: o200k_base)
MAX_DESC_LENGTH = 500
import os as _os
_REPO_ROOT = _os.path.abspath(_os.path.join(_os.path.dirname(__file__), "..", ".."))
CSV_PATH = _os.environ.get(
    "AIOMM_LABELED_PAIRS_CSV",
    _os.path.join(_REPO_ROOT, "AIOMarketMaker.ML", "Training", "data", "labeled_pairs_v8.csv"),
)

# Batch API pricing for gpt-5-mini (per 1M tokens)
PRICE_INPUT_UNCACHED = 0.0625   # $0.125 standard * 0.5 batch discount
PRICE_INPUT_CACHED   = 0.00625  # $0.0125 standard cached * 0.5 batch discount
PRICE_OUTPUT         = 0.50     # $1.00 standard * 0.5 batch discount

# --- System prompt (exact text from LlmVariantClassifier.Prompt.cs) ---
SYSTEM_PROMPT = """Classify whether two eBay listings are comparable for pricing — would a buyer expect to pay roughly the same for both?

STEP 1 — PRODUCT IDENTITY (reject if any apply)
- Different product, model, or set. Match by product name, not category.
- Modern reissue ≠ original. "Vintage Collection" (Hasbro) is a modern toy line, not an actual vintage item.
- Accessory ≠ full product (e.g., "PS5 Disc Drive" ≠ "PS5 Console").
- Different spec: storage, RAM, CPU, network lock status.
- Different size, including shoe size and width (e.g., 12.5 4E ≠ 13 D).
- Different quantity (single item ≠ lot or bundle of multiples).
- Standard edition ≠ limited/special/collaboration edition.
- Stock ≠ modified, customized, or engraved.

STEP 2 — CONDITION (reject if 2+ bands apart)
Bands: New/Sealed > Excellent > Good > Fair/Poor.
Same or adjacent band → comparable. Two+ apart → not comparable.
Sealed vs opened → always a gap (sealed = New, opened ≤ Good).
If only one listing states condition, treat the unstated one as compatible.

STEP 3 — COMPLETENESS (reject if included items differ)
Compare what each listing includes: accessories, batteries, packaging, docs.
- Genuine OEM batteries ≠ third-party batteries (different value and reliability).
- Luxury items: box, papers, and certificates significantly affect price.
- Jewelry: determine what's included from the TITLE, not the description. "Bracelet" ≠ "Bracelet With Charms."
- Bundles: extras must be verifiably equivalent. Vague "+Extras" ≠ named specific items. If you cannot confirm contents match → "different".

STEP 4 — COLOR
Non-fashion (electronics, furniture, appliances, doorbells, office chairs): color is always trivial, ignore it (e.g., carbon vs blue office chair → same).
Fashion (sneakers, clothing, watches, jewelry): colorway matters.
- Different named colorways → "different", even if shades look similar.
- One names a colorway, the other doesn't → "different" (cannot confirm match).

STEP 5 — SPARSE LISTINGS
Missing detail ≠ difference. Only reject on explicit contradictions stated by BOTH listings.
Titles are authoritative. eBay auto-generates descriptions that may be inaccurate — trust titles over descriptions.
Trivial and always ignorable: manufacture year, seller location, box condition, included cables.

OUTPUT: First give your reason (under 20 words), then set verdict.
"same" = comparable. "different" = not comparable. "uncertain" = cannot identify product.
Apply each step independently — do not combine individually acceptable differences into a rejection.
Default to "same" when product identity matches and no explicit conflict exists."""

# Structured output schema (sent as part of the request)
RESPONSE_SCHEMA = json.dumps({
    "type": "object",
    "properties": {
        "reason": {"type": "string"},
        "verdict": {"type": "string", "enum": ["same", "different", "uncertain"]}
    },
    "required": ["reason", "verdict"],
    "additionalProperties": False
})

csv.field_size_limit(10_000_000)

def truncate(text, max_len):
    if not text or len(text) <= max_len:
        return text or ""
    return text[:max_len]

def build_user_prompt(title_a, desc_a, title_b, desc_b):
    desc_a = truncate(desc_a, MAX_DESC_LENGTH)
    desc_b = truncate(desc_b, MAX_DESC_LENGTH)
    return f"""Listing A:
Title: {title_a}
Description: {desc_a}

Listing B:
Title: {title_b}
Description: {desc_b}"""

def main():
    enc = tiktoken.encoding_for_model(MODEL)

    # Count system prompt tokens (same for every request, will be cached)
    system_tokens = len(enc.encode(SYSTEM_PROMPT))
    schema_tokens = len(enc.encode(RESPONSE_SCHEMA))

    # Per-request overhead: {"role":"system","content":"..."} + {"role":"user","content":"..."} wrappers
    # In batch JSONL format, there's also the outer wrapper with custom_id, method, url, body
    # Estimate ~50 tokens for JSON wrapper overhead per request
    wrapper_overhead = 50

    print(f"System prompt: {system_tokens} tokens (cached after first request)")
    print(f"Response schema: {schema_tokens} tokens")
    print(f"Wrapper overhead: ~{wrapper_overhead} tokens per request")
    print()

    # Read CSV and count tokens for ALL user prompts
    print(f"Reading {CSV_PATH}...")
    total_pairs = 0
    total_user_tokens = 0
    min_user_tokens = float('inf')
    max_user_tokens = 0
    empty_desc_count = 0

    with open(CSV_PATH, 'r', encoding='utf-8') as f:
        reader = csv.DictReader(f)
        for row in reader:
            title_a = row.get('anchor_title', '')
            desc_a = row.get('anchor_desc', '')
            title_b = row.get('neighbor_title', '')
            desc_b = row.get('neighbor_desc', '')

            if not desc_a or desc_a == 'nan':
                empty_desc_count += 1
                desc_a = ''
            if not desc_b or desc_b == 'nan':
                empty_desc_count += 1
                desc_b = ''

            user_prompt = build_user_prompt(title_a, desc_a, title_b, desc_b)
            user_tokens = len(enc.encode(user_prompt))

            total_user_tokens += user_tokens
            min_user_tokens = min(min_user_tokens, user_tokens)
            max_user_tokens = max(max_user_tokens, user_tokens)
            total_pairs += 1

            if total_pairs % 10000 == 0:
                print(f"  Processed {total_pairs:,} pairs...")

    avg_user_tokens = total_user_tokens / total_pairs

    print(f"\n{'='*60}")
    print(f"EXACT TOKEN COUNTS")
    print(f"{'='*60}")
    print(f"Total pairs: {total_pairs:,}")
    print(f"Empty descriptions: {empty_desc_count:,}")
    print()
    print(f"User prompt tokens:")
    print(f"  Min:     {min_user_tokens}")
    print(f"  Max:     {max_user_tokens}")
    print(f"  Average: {avg_user_tokens:.1f}")
    print(f"  Total:   {total_user_tokens:,}")
    print()

    # Calculate totals
    cached_per_request = system_tokens + schema_tokens  # system prompt + schema (cached)
    uncached_per_request = avg_user_tokens + wrapper_overhead  # user prompt + JSON wrapper

    total_cached = cached_per_request * total_pairs
    total_uncached = total_user_tokens + (wrapper_overhead * total_pairs)

    # Output tokens: {"reason":"<~15 words>","verdict":"same/different/uncertain"}
    # Typical reason is 10-20 words (~15-25 tokens) + JSON structure (~15 tokens)
    avg_output_tokens = 40
    total_output = avg_output_tokens * total_pairs

    print(f"Per request:")
    print(f"  Cached input:   {cached_per_request} tokens (system + schema)")
    print(f"  Uncached input: {uncached_per_request:.0f} tokens avg (user prompt + wrapper)")
    print(f"  Output:         ~{avg_output_tokens} tokens (reason + verdict JSON)")
    print()
    print(f"Total across {total_pairs:,} pairs:")
    print(f"  Cached input:   {total_cached:,} tokens")
    print(f"  Uncached input: {total_uncached:,} tokens")
    print(f"  Output:         {total_output:,} tokens")
    print()

    # Calculate cost
    cost_cached = (total_cached / 1_000_000) * PRICE_INPUT_CACHED
    cost_uncached = (total_uncached / 1_000_000) * PRICE_INPUT_UNCACHED
    cost_output = (total_output / 1_000_000) * PRICE_OUTPUT

    print(f"{'='*60}")
    print(f"BATCH API COST (gpt-5-mini, 50% batch discount)")
    print(f"{'='*60}")
    print(f"  Cached input:   {total_cached:>12,} tokens × ${PRICE_INPUT_CACHED}/1M = ${cost_cached:.2f}")
    print(f"  Uncached input: {total_uncached:>12,} tokens × ${PRICE_INPUT_UNCACHED}/1M = ${cost_uncached:.2f}")
    print(f"  Output:         {total_output:>12,} tokens × ${PRICE_OUTPUT}/1M = ${cost_output:.2f}")
    print(f"  {'─'*50}")
    print(f"  TOTAL: ${cost_cached + cost_uncached + cost_output:.2f}")
    print()
    print(f"  (Without prompt caching: ${(total_cached / 1_000_000) * PRICE_INPUT_UNCACHED + cost_uncached + cost_output:.2f})")
    print(f"  (Without batch discount: ${(total_cached / 1_000_000) * 0.0125 + (total_uncached / 1_000_000) * 0.125 + (total_output / 1_000_000) * 1.00:.2f})")

if __name__ == "__main__":
    main()
