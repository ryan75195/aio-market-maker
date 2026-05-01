# Taxonomy Pipeline Strategy

## Executive Summary

The taxonomy pipeline automatically discovers product variant axes from eBay listing titles, assigns each listing to a variant cell, and computes per-cell pricing statistics. It replaces the ONNX pairwise variant classifier (roberta-large, F1=0.913) with a deterministic, per-category taxonomy that defines comparability through cell membership rather than pairwise prediction.

Across 7 test categories (74,127 listings), the pipeline achieves 62-95% coverage with 1-4% conflict rates. When combined with LLM-suggested regex injection for structured identifiers (LEGO set numbers, Nike style codes), coverage improves by up to +42pp on long-tail categories.

The system runs zero-configuration: feed it a search term and listings, it returns named axes, cell assignments, and pricing stats. No manual catalogue curation required.

## System Overview

### What It Does

Given a set of eBay listings for a product category:

1. **Discovers variant axes** — e.g. for "PlayStation 5 Console" it finds Edition (disc/digital), Model (slim/pro), Storage (825gb/1tb/2tb), Color (blu/black)
2. **Assigns each listing to a cell** — "PS5 Slim Disc 1TB" is one cell, "PS5 Pro Digital 2TB" is another
3. **Computes per-cell pricing** — median active/sold prices, sell-through rate, price spread
4. **Identifies contaminants and modifiers** — Portal accessories (contaminant), "bundle"/"sealed" (modifiers)

### What It Replaces

The ONNX variant classifier (v10, roberta-large) determines comparability by predicting whether two listings are the same variant. This requires O(n^2) pair comparisons and produces a binary yes/no with a confidence score.

The taxonomy approach defines comparability structurally: two listings are comparable if and only if they share the same cell. This is:
- **More precise** — 1-4% conflicts vs 8.7% pair error rate
- **More interpretable** — you know WHY two listings are comparable (same axes)
- **Cheaper at scale** — one taxonomy generation per category vs O(n^2) pair predictions
- **Coverage-limited** — 15-45% of listings fall outside any cell (no axis values matched)

Within coverage range, the taxonomy is strictly better. The ONNX model's advantage is universal coverage — it always produces a prediction.

## How We Got Here

The final pipeline (V5) is the result of five iterations, each solving a specific failure mode of the previous.

### V1: Let the LLM Do Everything

The first approach extracted bigrams and trigrams from listing titles, sent them to gpt-4.1-mini, and asked it to identify axes and generate a regex pattern for each axis value. This worked — the LLM correctly identified Edition, Model, Storage, and Color for PS5 — but required N+1 API calls per category (one to identify axes, one per value to generate regex). More critically, the regex quality was inconsistent: the LLM would sometimes generate overly broad patterns that matched unrelated listings, or overly narrow patterns that missed obvious matches.

### V2: Embedding Dedup, Single LLM Call

To reduce API calls, V2 introduced embedding-based deduplication. Instead of asking the LLM for regex, we embedded all n-grams using `text-embedding-3-large` and merged semantically equivalent ones (cosine similarity >0.95) using a union-find data structure. This collapsed "ps5"/"playstation 5", "bnib"/"brand new in box", etc. into canonical forms, then sent the deduplicated list to the LLM in a single call to group into axes.

This cut API calls from N+1 to 2 (one embedding batch, one LLM call) and improved matching by using substring lookup instead of LLM-generated regex. But the LLM was still *inventing* the axis structure — deciding which n-grams to group and which to ignore. Running the same input twice could produce different axes.

A key discovery during this phase: the 0.95 cosine threshold was calibrated through a parameter sweep across 3 categories. At 0.93, the model merges "1tb" and "2tb" (both are storage sizes, so semantically similar) — a catastrophic error. At 0.97, it fails to merge obvious synonyms. The sweep (`param_search.py`) tested 12 parameter combinations and confirmed 0.95 as the sweet spot.

### V3: Constrain the LLM with Statistical Evidence

The variance problem led to a fundamental insight: if we could pre-compute which n-grams are mutually exclusive in the data, we wouldn't need the LLM to discover structure — only to clean it up. V3 introduced mutual exclusivity (ME) detection: two n-grams are ME if their listing overlap is <5%. "disc" and "digital" rarely appear in the same PS5 listing, so they're statistically validated alternatives.

V3 pre-computed all ME pairs and sent them as hints to the LLM alongside co-occurrence pairs (n-grams that frequently appear together). The LLM's job narrowed from "discover axes from raw n-grams" to "group these pre-validated ME candidates into named axes." Post-validation checked that the LLM's output respected the ME constraints, dropping entire axes where >30% of value pairs violated exclusivity.

This was a major improvement — coverage was stable across runs and conflicts dropped below 5%. But the LLM still had one degree of freedom that caused variance: whether to create an axis at all. Given the same ME pairs, it might create a Color axis on one run and omit it on the next.

### V4: Fix the Axis Set, Eliminate Invention

V4 attacked the remaining variance source directly: a universal axis catalog of 16 predefined types (model, storage, color, size, edition, etc.). The LLM's job reduced to two constrained tasks: (1) pick 3-6 applicable axes from the catalog, (2) assign n-grams to those axes. No new axis types could be invented.

This achieved near-perfect consistency across runs. But it had a fundamental limitation: novel axes not in the catalog couldn't be discovered. A category with an unusual differentiator (e.g., Pokemon set names, LEGO themes) would miss it entirely unless someone had anticipated it in the catalog.

### V5: Deterministic Discovery via Graph Algorithms

The final iteration resolved the tension between consistency (V4) and flexibility (V3) by removing the LLM from structure discovery entirely. V5 builds a weighted graph where n-grams are nodes and ME pairs are edges (weighted by embedding similarity), then runs Louvain community detection to find axis groups algorithmically.

A diagnostic experiment (`cooccurrence_diagnostic.py`) had shown that ME alone produces false positives — rare n-grams are often ME by chance simply because they're rare. The fix was requiring *both* mutual exclusivity and semantic similarity for graph edges. "disc" and "digital" are ME *and* semantically related (both describe editions), so they get a strong edge. "disc" and "obsidian" are ME but semantically unrelated, so they get a weak or no edge. This dual-signal criterion, tested on PS5 ground truth data, cleanly separates same-axis pairs from cross-axis pairs.

The LLM's role in V5 is limited to cleanup: merging communities that the algorithm split too finely, naming axes, and identifying contaminants and modifiers. This is a much more constrained task than structure discovery, making the output stable across runs.

Two additional features were added in V5: LLM regex injection (for high-cardinality identifiers like LEGO set numbers that n-grams can't capture) and slot-based sub-threshold recovery (for rare axis values below the 3% significance threshold). Both are described in detail in the Key Technologies section.

### Validation

Reliability was tested formally: `reliability_test.py` ran V3's pipeline 5 times per category with identical inputs to measure LLM-induced variance. `reliability_test_v4.py` did the same for V4. V5's deterministic discovery eliminated the main variance source, with the LLM cleanup step producing consistent results at temperature 0.0.

The pipeline was tested across 15 diverse categories (`run_15_categories.py`) including electronics, sneakers, collectibles, fashion, watches, and home appliances. The 7-category results table below represents the subset with the most detailed analysis.

### Experiment Tooling

An interactive HTML viewer (`taxonomy_viewer.html`) was built to explore results visually — axis filters, modifier toggles, cell pricing tables, and individual listing inspection. An export script (`export_taxonomy.py`) generates the full dataset (axes, cell stats, listing assignments) as JSON for the viewer. These tools were essential for identifying edge cases and tuning parameters during development.

## Pipeline Architecture

```
┌─────────────────────────────────────────────────────────────┐
│ STAGE 1: Deterministic N-gram Pipeline                      │
│                                                             │
│ extract_ngrams → dedup_ngrams (embedding cosine >0.95)      │
│ → compute_match_sets → filter_significant (>3% of listings) │
│ → compute_me_pairs (overlap <5%) → filter_to_me_candidates  │
└─────────────────────┬───────────────────────────────────────┘
                      │
┌─────────────────────▼───────────────────────────────────────┐
│ STAGE 2: Community Detection                                │
│                                                             │
│ Build graph: ME edges weighted by embedding similarity      │
│ → Louvain community detection (resolution=2.0)              │
│ → Score communities by ME density + coherence + coverage    │
│ → Separate modifiers (singletons with >4% coverage)         │
└─────────────────────┬───────────────────────────────────────┘
                      │
┌─────────────────────▼───────────────────────────────────────┐
│ STAGE 3: LLM Cleanup (1 call per category)                  │
│                                                             │
│ Send raw communities + modifiers to LLM                     │
│ → LLM merges/splits/renames into clean named axes           │
│ → Identifies contaminants and leftover modifiers            │
│ Model: gpt-4.1-mini, structured JSON output, temp=0.0       │
└─────────────────────┬───────────────────────────────────────┘
                      │
┌─────────────────────▼───────────────────────────────────────┐
│ STAGE 3b: LLM Regex Injection (1 call per category)         │
│                                                             │
│ Send discovered axes + 80 sample titles to LLM              │
│ → LLM suggests structured identifier patterns               │
│   (set numbers, style codes, model numbers)                 │
│ → Validate against data: coverage, fragmentation,           │
│   min value count, year/noise filtering                     │
│ → Inject validated patterns as additional axes               │
└─────────────────────┬───────────────────────────────────────┘
                      │
┌─────────────────────▼───────────────────────────────────────┐
│ STAGE 4: Post-processing                                    │
│                                                             │
│ dedup_axis_values (>85% listing overlap = same value)       │
│ → prune_overlapping_values (adaptive threshold 20-35%)      │
│ → enforce_me_per_value (demote generic terms)               │
│ → assign_and_measure → coverage %, conflict %               │
└─────────────────────────────────────────────────────────────┘
```

### Cost Per Category

| Step | API Calls | Model | Typical Cost |
|------|-----------|-------|-------------|
| N-gram dedup (embeddings) | 1 batch | text-embedding-3-small | ~$0.002 |
| Community detection (embeddings) | 1 batch | text-embedding-3-small | ~$0.001 |
| LLM cleanup | 1 call | gpt-4.1-mini | ~$0.003 |
| LLM regex suggestion | 1 call | gpt-4.1-mini | ~$0.003 |
| **Total** | **~4 calls** | | **~$0.01** |

## Key Technologies and Algorithms

### The Core Problem

eBay listing titles are unstructured free text written by thousands of different sellers with no enforced format. The same PS5 console might appear as:

- "Sony PlayStation 5 Disc Edition 1TB White Console"
- "PS5 Slim Digital - 1TB SSD - Brand New Sealed"
- "PLAYSTATION 5 DISC VERSION 825GB + 2 CONTROLLERS BUNDLE"

To price a product accurately, we need to know which listings describe the *same variant*. The pipeline solves this by extracting the meaningful terms from titles, discovering which terms represent alternative options of the same attribute (e.g. "disc" vs "digital" are alternative editions), and grouping them into named axes. Each technology below addresses a specific challenge in this process.

### N-gram Extraction

**What:** N-grams are contiguous word sequences of length 1 (unigrams), 2 (bigrams), or 3 (trigrams) extracted from each listing title. "PS5 Slim Digital" produces unigrams ["ps5", "slim", "digital"], bigrams ["ps5 slim", "slim digital"], and the trigram ["ps5 slim digital"].

**Why n-grams:** Listing titles encode variant information as short phrases — "1tb", "disc edition", "pro max 256gb". N-grams capture these naturally without needing a predefined vocabulary. They're the raw vocabulary from which the pipeline discovers structure.

**Why up to trigrams:** Unigrams miss multi-word values like "disc edition". Trigrams capture these. Beyond trigrams, matches become too sparse to be statistically useful across thousands of listings from different sellers.

### Embedding-based N-gram Deduplication

**What:** After extraction, the pipeline produces thousands of n-grams per category, many of which are surface-level variants of the same concept — "ps5" and "playstation 5", "bnib" and "brand new in box". We embed every n-gram using OpenAI's `text-embedding-3-large` model (which maps text to a 3072-dimensional vector capturing semantic meaning) and merge any pair with cosine similarity >0.95 into one canonical form.

**Why embeddings over string matching:** Simple string similarity (edit distance, substring) would miss that "ps5" and "playstation 5" mean the same thing. Semantic embeddings capture meaning, not spelling. The 0.95 threshold is deliberately strict — we only merge when the model is very confident two terms are synonymous.

**Critical guard — numeric variants:** Embedding models consider "256gb" and "512gb" semantically very similar (both are storage sizes). But for our purpose they represent *different products*. The `_are_numeric_variants()` function detects when two n-grams differ only in their numeric parts and prevents merging them.

### Mutual Exclusivity (ME) Detection

**What:** For each significant n-gram (appearing in >3% of listings), we compute which listings it matches. Two n-grams are **mutually exclusive** if their listing sets overlap by less than 5%. For example, "disc" appears in 40% of PS5 listings and "digital" in 35%, but fewer than 5% contain both — so they're ME.

**Why this is the core signal:** If two terms rarely co-occur in the same listing, sellers are using them as *alternatives* — they describe different options of the same attribute. This is the statistical foundation of the entire pipeline. "disc" and "digital" are alternatives for the Edition axis. "slim" and "pro" are alternatives for the Model axis. ME detection discovers these relationships without any domain knowledge.

**Why 5% threshold:** Real-world titles are messy. A small number of listings genuinely contain both terms (e.g., "PS5 Digital Edition - NOT Disc Version"). The 5% threshold tolerates this noise while still identifying the core exclusivity pattern.

### Louvain Community Detection

**What:** ME detection produces hundreds of pairwise relationships (A is exclusive with B, B with C, etc.). We need to group these into coherent axes. The Louvain algorithm is a graph-based community detection method that partitions nodes (n-grams) into communities (candidate axes) by maximizing *modularity* — a measure of how densely connected nodes are within communities versus between them.

**Why a graph algorithm:** The ME relationships form a natural graph: n-grams are nodes, ME pairs are edges. We need to find clusters where members are mutually exclusive with each other (they're all values of the same axis) but not with members of other clusters (different axes are independent). This is exactly what community detection optimises for.

**Why Louvain specifically:** It handles weighted graphs (we weight edges by embedding similarity so semantically related ME pairs cluster more tightly), scales well to hundreds of nodes, and the resolution parameter (set to 2.0) lets us control granularity. Higher resolution produces smaller, more specific communities — important because we want tight, meaningful axes rather than large catch-all groups. Alternatives like spectral clustering or label propagation were considered but Louvain produced the cleanest axis boundaries in testing.

**Edge weighting:** ME edges are weighted by the embedding similarity between the two n-grams. "disc" and "digital" are semantically similar (both describe PS5 editions) so their edge is weighted high. This pulls semantically related ME pairs into the same community, producing more coherent axes.

### LLM Cleanup (Structured Output)

**What:** The statistical pipeline produces raw communities that are approximately correct but often messy — a community might contain ["disc", "disc edition", "disk", "digital", "digital edition"] mixed together, or two related communities might need merging. A single gpt-4.1-mini call receives all raw communities with their statistics, and returns clean, named axes via JSON schema enforcement (structured output).

**Why an LLM rather than more heuristics:** The statistical pipeline excels at discovering *structure* (which terms are mutually exclusive) but struggles with *semantics* (what to name the axis, which communities to merge, what's a contaminant). An LLM brings world knowledge — it knows "disc" and "digital" are PS5 editions, "portal" is a different product (contaminant), and "sealed" is a condition modifier not a variant. Writing heuristics for this would require per-category rules; the LLM generalises across all categories.

**Why gpt-4.1-mini:** It's cheap (~$0.003/call), fast (<2s), supports structured JSON output (guaranteeing parseable responses), and has sufficient world knowledge for product taxonomy. We tested gpt-4o which produced marginally better naming but at 10x the cost with no measurable improvement in axis quality.

**Why structured output:** The LLM must return a specific JSON schema (axes with values, contaminants, modifiers). JSON schema enforcement guarantees the response is always valid and parseable — no regex parsing of free text, no retry loops for malformed JSON.

### LLM Regex Injection

**What:** Some product categories use structured identifiers that n-gram analysis fundamentally cannot discover. LEGO sets have 5-digit set numbers (75192, 10497), Nike sneakers have style codes (DV0833-103), iPhones have model numbers. These identifiers have too many unique values — each appears in <1% of listings, well below the 3% significance threshold. A second gpt-4.1-mini call examines sample titles and suggests regex patterns to capture these identifiers.

**Why the n-gram pipeline can't handle this:** The 3% significance threshold exists to filter noise. But a LEGO set number like "75192" appears in maybe 0.3% of listings. With 300+ unique set numbers, each is individually rare even though collectively they cover 90%+ of listings. N-grams are designed for *low-cardinality* axes (disc/digital, slim/pro) not *high-cardinality* identifiers.

**Why an LLM suggests the patterns:** The LLM brings domain knowledge — it knows LEGO uses 4-6 digit set numbers, Nike uses a specific alphanumeric code format, etc. A human would look at the titles and immediately spot these patterns; the LLM does the same. Writing a universal regex pattern detector would require encoding knowledge about every product category's identifier scheme.

**Why validation gates:** LLM suggestions are nondeterministic and sometimes wrong. Every suggestion is validated against the actual listing data before injection:

- **Coverage gates (5-98%):** Too low = pattern doesn't match enough listings to be useful. Too high = pattern is matching noise (e.g., every listing has *some* number in it).
- **Fragmentation gate (<80% unique):** If 80%+ of matches are unique values, the pattern is too fine-grained to form meaningful groups.
- **Minimum value count (max(3, 0.1% of listings)):** Each captured value must appear in enough listings to be statistically meaningful for pricing.
- **Year/noise filtering (1990-2034):** Numeric patterns often match year strings in titles. These are filtered as noise.
- **Duplicate pattern detection:** Prevents the LLM from suggesting the same pattern twice with different names.

Regex axes skip ME enforcement because they're pre-validated identifiers — every LEGO set number is inherently exclusive (a listing is for one set, not two).

### Slot-based Sub-threshold Recovery

**What:** Some genuine axis values fall just below the 3% significance threshold — a rare sneaker colorway at 2%, or a less common PS5 bundle at 1.5%. These are real variant values, just uncommon. Slot recovery uses *positional context* to identify them: for each sub-threshold candidate, we collect the words that appear to its left and right across all titles. If this positional fingerprint matches an existing axis value's fingerprint (weighted Jaccard similarity >0.20) and the candidate is ME with the axis, it's promoted.

**Why positional context works:** In eBay titles, variant values occupy consistent "slots". Color names tend to appear in the same position relative to the product name. If "obsidian" appears in the same slot as "white", "black", and "blue" (all confirmed Color axis values), it's probably also a color — even though it only appears in 1.5% of listings.

**Impact:** +2-17pp coverage across categories, with the largest gains in sneaker categories where rare colorway names are individually uncommon but collectively significant.

### Cell Assignment and Pricing

**What:** Once axes are defined, each listing is assigned to a **cell** — the combination of axis values it matches. A listing matching "disc + slim + 1tb" lands in that specific cell. Pricing statistics are then computed per cell rather than across the whole category.

**Why cells matter for pricing:** A "PS5" isn't one product — it's dozens of variants at different price points. The Digital Slim 1TB sells for ~£350 while the Pro 2TB sells for ~£700. Pricing the category as a whole produces a meaningless average. Cell-level pricing gives the actual market value for each specific variant.

**What's computed per cell:**
- **Median active price ("Buy At")** — what the variant is currently listed at
- **Median sold price ("Sells For")** — what it actually sells for
- **Sell-through rate** — percentage of listings that sold (demand signal)
- **Active-vs-sold spread** — the gap between asking and selling price (margin indicator, market efficiency signal)

## Experiment Results

### Coverage and Conflicts Across 7 Categories

| Category | Listings | Coverage | Conflicts | Regex Axes | Regex Values |
|----------|----------|----------|-----------|------------|-------------|
| PlayStation 5 Console | 6,409 | 79.7% | 1.7% | 0 | 0 |
| Nike Air Jordan 1 | 22,224 | 87.9% | 3.7% | 1 (style codes) | 21 |
| iPhone 15 Pro Max | 4,987 | 94.6% | 3.0% | 0 | 0 |
| Pokemon Booster Box | 15,756 | 84.5% | 1.7% | 0 | 0 |
| Adidas Yeezy 350 | 4,905 | 64.7% | 2.0% | 1 (style codes) | 63 |
| LEGO Star Wars Set | 8,074 | 89.5% | 3.8% | 1 (set numbers) | 335 |
| Nike Dunk Low | 11,772 | 61.9% | 1.3% | 1 (style codes) | 48 |

### Key Findings

**Market efficiency detection.** PS5 cells show £1-2 active-vs-sold spread on high-volume variants — a commoditized market with no arbitrage opportunity. Sneaker/Pokemon categories show wider spreads, indicating information asymmetry and opportunity.

**Condition is the biggest pricing noise source.** Within a cell, "C Grade" refurbs at £268 mix with new units at £400. Title-derived condition grading would split these into separate pricing cohorts.

**Regex injection is transformative for long-tail categories.** LEGO went from 47% to 90% coverage with set number injection. Nike categories gained +15-22pp from style code patterns.

## Integration Strategy

### Phase 1: Port Core Pipeline to C#

Create `AIOMarketMaker.Core/Services/TaxonomyService.cs` implementing `ITaxonomyService`:

```
ITaxonomyService
├─ GenerateTaxonomy(jobId, searchTerm) → TaxonomyResult
├─ AssignListings(jobId, taxonomy) → IEnumerable<ListingAssignment>
└─ GetCellPricing(jobId, taxonomy) → IEnumerable<CellPricingStats>
```

**Reuse existing services:**
- `IEmbeddingService` (text-embedding-3-large) — for n-gram dedup and community detection
- `ITfIdfVectorizer` — not needed (taxonomy uses embedding similarity, not TF-IDF)
- OpenAI `ChatClient` (gpt-4.1-mini) — for LLM cleanup and regex injection (already registered in DI)
- `IMarketListingsQueryService` — for loading listings from DB

**New components:**
- `NgramExtractor` — extract and dedup n-grams from titles
- `MutualExclusivityAnalyzer` — compute ME pairs from match sets
- `CommunityDetector` — Louvain community detection (port or use a C# graph library)
- `RegexAxisInjector` — LLM regex suggestion + validation

### Phase 2: Persistence

Store generated taxonomies in the database so they don't need regeneration on every request:

- `Taxonomies` table — one row per job, stores axes + modifiers + contaminants as JSON
- `TaxonomyAssignments` table — per-listing cell assignment
- Refresh strategy: regenerate when listing count changes by >20% or on manual trigger
- Cache in memory for the chat agent session

### Phase 3: Chat Agent Integration

Replace the current `discover_variants` tool (TF-IDF + Ward clustering) with taxonomy-based variant discovery:

- `discover_variants` → runs `ITaxonomyService.GenerateTaxonomy` (or returns cached)
- Returns axes with values, cell pricing stats, and modifier filters
- `query_listings` → uses cell assignment for filtering instead of regex guessing
- The agent can reference axis values directly: "Show me all PS5 Slim Disc 1TB listings"

### Phase 4: Pricing Integration

Replace ONNX pairwise comparability with cell-based comparability:

- Two listings are comparable iff they share the same cell
- Pricing uses median sold price within the cell as the market value
- Confidence = cell size (more listings = more confident price)
- Fall back to ONNX classifier for uncovered listings (no cell assignment)

## Out of Scope — Future Work

### Condition Grading Axis

**Problem:** Title-derived quality language ("C Grade", "mint", "sealed", "faulty") is a universal axis that massively impacts pricing but isn't discovered by the pipeline because condition terms overlap with too many axis values.

**Approach:** Inject a pre-defined condition hierarchy as axis 0 before discovered axes. The pattern set is universal across categories:
- Sealed/BNIB/DS > Mint/VNDS > Excellent > Good > Graded/Refurb > Faulty/Spares
- eBay's structured `Condition` field from item specifics provides a secondary signal

### Directional Modifier Detection

**Problem:** "controller" matches both "with controller" (complete unit) and "no controller" (incomplete). Same for "box", "charger", "manual".

**Approach:** Bigram-aware matching with negation lookbehind. Split each directional modifier into `+controller` / `-controller` based on preceding words ("no", "without", "missing", "w/o").

### Bundle and Accessory Detection

**Problem:** Current "bundle" modifier only catches the literal word. Misses "with Ghost of Yotei", "& GTA 5", "2x controllers".

**Approach:** Three layers:
1. Keyword expansion ("lot", "x2", "combo", "pair of")
2. "with [Product]" pattern — connector word + proper noun signals an appended item
3. Price outlier flagging — listings >1.5x cell median are probable bundles

### Contaminant Removal

**Problem:** V5 detects contaminants (e.g., "portal" in PS5 search) but doesn't remove them from cell assignment and pricing.

**Approach:** After taxonomy generation, remove listings matching contaminant patterns AND zero axis values. Listings matching both contaminants and axis values are kept (the axes are the primary identity).

### Slot Recovery Integration

**Problem:** Sub-threshold n-grams (1-3%) represent genuine axis values too rare individually but collectively significant. Currently exists as a standalone experiment.

**Approach:** Run `recover_sub_threshold` after Stage 4 as an optional coverage boost. Tested at +2-17pp coverage with flat conflicts. Only promotes unigrams that pass ME gating and slot similarity >0.20.

### Regex Axis Reliability

**Problem:** LLM regex suggestions are nondeterministic and the bare `\b(\d{4,6})\b` pattern for LEGO matches non-set-number digits.

**Approach:**
- Run regex suggestion once per category, human-review, then freeze accepted patterns
- Or: validate against a reference database of known identifiers per domain
- Or: use eBay item specifics (MPN, Set Number) as ground truth to verify regex matches

### Multi-listing Deduplication

**Problem:** Same seller listing identical items multiple times inflates cell counts and skews pricing (e.g., 5x "C Grade" listings at £267.99 from one seller).

**Approach:** Deduplicate by (seller + title + price) before cell assignment. Count unique sellers per cell as a liquidity metric.

### Cross-category Taxonomy Transfer

**Problem:** Each category generates its taxonomy from scratch. Similar categories (Nike Air Jordan 1 / Nike Dunk Low) discover similar axes independently.

**Approach:** Build a taxonomy template library from historical runs. New categories start from the closest template and refine, reducing LLM calls and improving consistency.
