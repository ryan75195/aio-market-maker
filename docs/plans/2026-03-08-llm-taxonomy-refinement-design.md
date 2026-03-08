# LLM Taxonomy Refinement Design

## Goal

Enhance the statistical taxonomy pipeline with a single LLM call that refines axes (labels, cleans values, merges, ranks importance, suggests missing values), then feeds the refined schema back into the statistical pipeline for re-assignment.

## Architecture: Hybrid Feedback Loop

```
Listing titles
    |
    v
Statistical Pipeline (existing)
    |  extracts n-gram candidates, builds axes via Louvain
    |  runs all PostProcess steps (dedup, stop words, outliers, etc.)
    v
Raw TaxonomyResult { Axis[], CellAssignments[], Coverage% }
    |
    v
LLM Refinement (single structured call)
    |  Input: raw axes + values + product name + 20 sample titles
    |  Output: TaxonomyRefinement (delta-based)
    v
Apply Deltas (pure function)
    |  drops, merges, removes/adds values, sets names + importance
    v
Statistical Pipeline Re-assignment (existing AssignListings)
    |  re-runs assignment against all titles with refined axes
    v
Final TaxonomyResult { named axes, importance scores, improved coverage }
    |
    v
Persist to DB (existing TaxonomyPersistenceService)
```

## LLM Structured Output Schema (Delta-Based)

The LLM returns only changes, not full value lists:

```json
{
  "axes": [
    {
      "original": "Axis 0",
      "name": "Edition",
      "importance": 5,
      "remove_values": ["portal"],
      "add_values": ["slim"]
    }
  ],
  "merge_axes": [
    { "keep": "Axis 0", "absorb": "Axis 3" }
  ],
  "drop_axes": ["Axis 7"]
}
```

Fields:
- `axes` -- one entry per surviving axis. Delta only:
  - `original` -- which statistical axis this maps to
  - `name` -- human-readable label (e.g., "Edition", "Color", "Condition")
  - `importance` -- 1-5 price relevance (5 = most affects price)
  - `remove_values` -- values to drop from this axis
  - `add_values` -- new values to add for improved coverage
- `merge_axes` -- pairs of axes to combine (absorb's values move into keep)
- `drop_axes` -- axes to remove entirely (junk, mixed concepts)

## Components

### ITaxonomyRefiner Interface

```csharp
Task<TaxonomyRefinement> Refine(
    IEnumerable<Axis> axes,
    string productName,
    IEnumerable<string> sampleTitles,
    CancellationToken ct);
```

### LlmTaxonomyRefiner

- Builds prompt with product name, raw axes+values, 20 sample titles
- Calls OpenAI (gpt-5-mini) with JSON schema structured output
- Deserializes response into TaxonomyRefinement record
- Model configurable via local.settings.json

### ApplyRefinement (pure static method)

Applies delta operations to raw axes:
1. Drop axes listed in `drop_axes`
2. Merge axes listed in `merge_axes`
3. Remove/add values per axis
4. Set name and importance

### TaxonomyService Changes

Generate method updated:
```
... PostProcess ...
refinement = await _refiner.Refine(axes, productName, sampleTitles, ct);
axes = ApplyRefinement(axes, refinement);
assignments = AssignListings(titles, axes, matchSetLookup);
```

### DB Changes

Add `Importance` column (int, nullable) to `TaxonomyAxes` table.
Name column already exists -- currently stores "Axis 0" etc., will now store LLM labels.

## Fallback

If LLM call fails (timeout, rate limit, malformed response), log a warning and continue with unrefined axes. The statistical pipeline output is functional as-is.

## Testing Strategy

### Integration Tests (real OpenAI, TDD)

Category: `[Category("Integration")]`, `[Explicit]`. Iterate prompt until tests pass.

1. PS5 -- should label and clean mixed axes, drop junk
2. iPhone -- should merge related axes, separate mixed values
3. Canon R6 -- should handle camera-specific terminology
4. Schema compliance -- response always matches JSON schema
5. Additional values -- should suggest coverage improvements
6. Graceful fallback -- returns original axes on failure

### Unit Tests (no API, fast)

- `ApplyRefinement` -- pure function, all delta operations
- Prompt construction -- verify includes product name, axes, sample titles

## Model

gpt-5-mini via OpenAI structured output (JSON schema mode), matching existing project pattern in MarketsChatService.

## Cost

~$0.01 per taxonomy generation (single call with small input/output).
