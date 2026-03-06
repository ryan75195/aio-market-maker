# Taxonomy Service — Stage 1 Design

## Goal

Port the deterministic core of the Python taxonomy pipeline (V5) to C# as a testable service layer. No LLM calls, no database persistence, no batch pipeline integration — just the algorithm, callable from tests.

## Scope

**In scope:**
- N-gram extraction (unigrams, bigrams, trigrams) from listing titles
- Embedding-based n-gram deduplication (cosine >0.95, numeric variant guard)
- Mutual exclusivity detection (overlap <5%)
- Louvain community detection (hand-ported, ~150 lines)
- Post-processing (community scoring, value dedup, overlap pruning, modifier separation)
- Cell assignment and pricing statistics
- Unit tests with synthetic title sets

**Out of scope (future stages):**
- LLM cleanup (gpt-4.1-mini structured output)
- LLM regex injection (structured identifier patterns)
- Slot-based sub-threshold recovery
- Database persistence (Taxonomies / TaxonomyAssignments tables)
- Batch pipeline integration (TaxonomyBatchStage)
- Chat agent integration
- DI registration in Program.cs

## Component Architecture

### Records

All records live in `ITaxonomyService.cs`, above the interface.

```csharp
record Ngram(string Canonical, IEnumerable<string> Forms, int Frequency);

record MatchSet(Ngram Ngram, IReadOnlySet<int> ListingIndices);

record MutuallyExclusivePair(
    Ngram A, Ngram B, double Overlap, double EmbeddingSimilarity);

record WeightedEdge(int NodeA, int NodeB, double Weight);

record Community(int Id, IEnumerable<Ngram> Members,
    double ExclusivityDensity, double Coherence, double Coverage);

record Axis(string Name, IEnumerable<AxisValue> Values);
record AxisValue(string Label, IEnumerable<Ngram> Ngrams);

record CellAssignment(int ListingIndex,
    IReadOnlyDictionary<string, string> Cell, bool HasConflict);

record CellStats(IReadOnlyDictionary<string, string> Cell,
    int Count, int Active, int Sold, int SellThroughPct,
    decimal MedianActivePrice, decimal MedianSoldPrice);

record TaxonomyResult(IEnumerable<Axis> Axes,
    IEnumerable<CellAssignment> Assignments, IEnumerable<CellStats> Cells,
    double CoveragePercent, double ConflictPercent);
```

### Interfaces

```csharp
INgramExtractor
├─ Extract(IEnumerable<string> titles) → IEnumerable<Ngram>
└─ Deduplicate(IEnumerable<Ngram> ngrams) → IEnumerable<Ngram>

IMutualExclusivityAnalyzer
├─ ComputeMatchSets(IEnumerable<string> titles, IEnumerable<Ngram> ngrams)
│      → IEnumerable<MatchSet>
└─ FindExclusivePairs(IEnumerable<MatchSet> matchSets, double threshold)
       → IEnumerable<MutuallyExclusivePair>

ICommunityDetector
└─ Detect(IEnumerable<WeightedEdge> edges, int nodeCount, double resolution)
       → IEnumerable<Community>

ITaxonomyService
└─ Generate(IEnumerable<string> titles) → TaxonomyResult
```

### Implementations

- `NgramExtractor` — depends on `IEmbeddingService` (existing) for dedup only
- `MutualExclusivityAnalyzer` — pure computation, no external dependencies
- `LouvainCommunityDetector` — pure computation, hand-ported algorithm
- `TaxonomyService` — orchestrates the above, owns post-processing logic

## Data Flow

```
Titles (string[])
  → INgramExtractor.Extract → Ngram[]
  → INgramExtractor.Deduplicate (+ IEmbeddingService) → Ngram[] (deduplicated)
  → IMutualExclusivityAnalyzer.ComputeMatchSets → MatchSet[]
  → IMutualExclusivityAnalyzer.FindExclusivePairs → MutuallyExclusivePair[]
  → Build WeightedEdge[] (weight = embedding similarity)
  → ICommunityDetector.Detect → Community[]
  → Post-processing (score, filter, dedup, prune, assign)
  → TaxonomyResult
```

Post-processing stays inside TaxonomyService — it's orchestration logic that doesn't warrant its own interface.

## File Layout

```
AIOMarketMaker.Core/
└─ Services/
   └─ Taxonomy/
      ├─ ITaxonomyService.cs          — interface + all records
      ├─ TaxonomyService.cs           — orchestrator + post-processing
      ├─ INgramExtractor.cs           — interface
      ├─ NgramExtractor.cs            — implementation
      ├─ IMutualExclusivityAnalyzer.cs — interface
      ├─ MutualExclusivityAnalyzer.cs  — implementation
      ├─ ICommunityDetector.cs         — interface
      └─ LouvainCommunityDetector.cs   — implementation

AIOMarketMaker.Tests.Unit/
└─ Taxonomy/
   ├─ NgramExtractorTests.cs
   ├─ MutualExclusivityAnalyzerTests.cs
   ├─ LouvainCommunityDetectorTests.cs
   └─ TaxonomyServiceTests.cs
```

## Dependencies

```
TaxonomyService
├─ INgramExtractor
│   └─ IEmbeddingService (existing, for dedup only)
├─ IMutualExclusivityAnalyzer (no external deps)
└─ ICommunityDetector (no external deps)
```

No new NuGet packages. No database changes. No DI registration yet.

## Key Parameters (from Python experiments)

| Parameter | Value | Source |
|-----------|-------|--------|
| Embedding similarity threshold (dedup) | 0.95 | param_search.py (36-point sweep) |
| Mutual exclusivity overlap threshold | 0.05 (5%) | V3 experiments |
| Significance threshold (min listing match) | 0.03 (3%) | V3 experiments |
| Minimum exclusive pairs per candidate | 3 | V3 experiments |
| Louvain resolution | 2.0 | V5 experiments |
| Embedding similarity for graph edges | 0.15 | V5 experiments |
| Modifier threshold (singleton coverage) | 0.04 (4%) | V5 experiments |
| Value dedup overlap | 0.85 (85%) | V5 experiments |
| Adaptive pruning threshold | 0.20-0.35 | V5 experiments |

## Testing Strategy

Unit tests with synthetic title sets (10-30 titles), no database, no real API calls. IEmbeddingService mocked for dedup tests.

**NgramExtractorTests:** extraction, frequency counting, stop word filtering, dedup merging, numeric variant guard.

**MutualExclusivityAnalyzerTests:** match set computation, exclusive pair detection, threshold behavior, candidate filtering, word boundary matching.

**LouvainCommunityDetectorTests:** fully connected graph, disconnected components, resolution splitting, edge weight respect, empty graph.

**TaxonomyServiceTests:** end-to-end with synthetic PS5 titles, cell assignment correctness, conflict detection, coverage/conflict percentages, cell stats with active/sold split.

## Future Stages

- **Stage 2:** LLM cleanup + regex injection
- **Stage 3:** Database persistence + batch pipeline hook
- **Stage 4:** Chat agent integration
- **Stage 5:** Pricing integration (replace ONNX comparables)
