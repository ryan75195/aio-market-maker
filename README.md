# AIOMarketMaker

A .NET 8 platform for solving **eBay variant matching at scale** — given an active eBay listing, identify the sold listings that represent the same product *variant*, then price the active listing against them.

The problem sounds straightforward and isn't. Titles are unstructured ("PS5 Slim 1TB White" vs "Sony PlayStation 5 Slim Disc Edition 1TB Console — White"), accessories contaminate naive matches ("PS5 Controller White" looks similar to a PS5 console), and variant-defining attributes are category-specific (PS5 uses *edition / storage / form_factor*; sneakers use *size / colorway / style code*). Solving it well requires web scraping, NLP, transformer fine-tuning, graph clustering, and LLM-orchestrated taxonomy extraction — and an honest acknowledgement of the trade-offs each approach makes.

## Scale (production)

- **374,000+** eBay listings ingested across **126** search-term jobs
- **16,310** pricing opportunities computed via cell-based comparable matching
- **143K** labeled pairs used to fine-tune a RoBERTa-large variant-matching classifier (test F1 = **0.913**)

## Approaches explored

The matching problem has been attacked three ways, each addressing the limitations of the previous.

### Generation 1 — Pairwise classifier

RoBERTa-large fine-tuned on 143K labeled "same variant?" pairs, exported to ONNX for ~12 ms-per-pair GPU inference. Accurate, but doesn't scale: a 7,230-listing job produces 26M pairwise comparisons — roughly 87 hours per job. Embedding-based candidate filtering (Pinecone, then USearch) cut the pair count but reintroduced the original problem: similarity search returns accessories and adjacent variants. Still used as a fallback for small batches.

### Generation 2 — Bottom-up taxonomy *(production)*

Discover variant axes directly from title text:

1. Extract n-grams from titles in a category
2. Find mutually exclusive token pairs (e.g. "disc" and "digital" rarely co-occur)
3. Cluster tokens into axes via Louvain community detection
4. Refine the raw communities with an LLM pass

Produces interpretable axes for **~$0.01 per category** in under 15 seconds and runs across all 126 production jobs. Plateaus at **~78% coverage** with an **~8.4% conflict rate** — listings that match contradictory values on the same axis — which is the biggest open accuracy issue.

### Generation 3 — Top-down extraction *(experimental)*

Two-stage pipeline: **GPT-5-nano** defines axes and allowed values once per category (the "skeleton"); a **fine-tuned Qwen3-4B GGUF** model then extracts values from each title locally on GPU. Cleaner output (PS5 benchmark: 84% coverage, 0% conflicts, 7 semantically meaningful axes), but currently takes 45 minutes per job because the inference runtime creates a fresh CUDA context per title. Solving that bottleneck is the next milestone.

## Architecture

| Project | Role |
|---|---|
| `AIOMarketMaker.Api` | ASP.NET Core API — scrape jobs, listings, history, nightly scheduling |
| `AIOMarketMaker.Core` | Domain logic, EF Core data layer, eBay HTML parsers (AngleSharp), ETL services |
| `AIOMarketMaker.ML` | ONNX runtime, OpenAI embeddings, Ward agglomerative clustering, USearch ANN, Qwen3 GGUF inference |
| `AIOMarketMaker.Console` | DI-discovered CLI tasks: search, pricing, backfill, comparables, validation |
| `AIOMarketMaker.Desktop` | Electron + Vue 3 admin app — batch monitoring, progress bars, stats dashboards |
| `AIOMarketMaker.Tests.{Unit,Integration,E2E,Contract,Common}` | NUnit + Moq, including HTML-snapshot contract tests against parser regressions |

## Tech stack

- **.NET 8**, Entity Framework Core, custom embedded-SQL migration runner
- **AngleSharp** for HTML parsing of eBay listing/search pages
- **ONNX Runtime** with CUDA for transformer inference
- **OpenAI** APIs + custom **Qwen3-4B GGUF** for skeleton/extraction stages
- **Ward agglomerative clustering** + **USearch** (265K vectors, 3072 dims) for clustering and approximate nearest neighbour
- **SQL Server** (LocalDB locally, Azure SQL in deployment)
- **Azure Table / Blob Storage** for job persistence and HTML snapshots
- **Electron + Vue 3 + Chart.js** for the desktop admin UI
- **Vitest + Playwright** for Electron integration tests

## Notable files

- `Core/Services/Taxonomy/TaxonomyService.cs` — Gen 2 production pipeline
- `Core/Services/Taxonomy/TopDownTaxonomyService.cs` — Gen 3 experimental pipeline
- `Core/Services/Taxonomy/CellPricingService.cs` — cell-based comparable pricing
- `ML/Services/VariantModelRunner.cs` — ONNX pairwise classifier (Gen 1, fallback)
- `ML/Services/ExtractionModelRunner.cs` — Qwen3-4B GGUF inference (Gen 3)
- `Tests.Contract/` — saved-HTML snapshot tests that surface eBay layout changes before parsers silently start producing garbage

## Open problems

These are the unsolved parts I'm actively working on — listed for honesty, not as TODOs:

1. **Conflict rate (~8.4%)** in Gen 2 — the dominant pricing-accuracy issue
2. **Variant vs condition axes** — no current approach distinguishes axes that *define* the product (edition, storage) from axes that *modify* its price (packaging, bundled accessories)
3. **Accessory rejection at extraction time** — bottom-up filtering only catches brand-level mismatches; the Gen 3 model occasionally accepts accessories
4. **Partial-axis cells** — neither approach enforces full coverage of variant-defining axes, which produces overlapping/noisy comparable groups
5. **Skeleton stability** — each Gen 3 run generates fresh axes, so cells aren't comparable across runs

## Status

Active R&D. Production runs Generation 2 across all jobs. Generation 3 is being benchmarked with the goal of replacing it once the per-title CUDA overhead is resolved.

---

© Ryan Kilgour. All rights reserved.
