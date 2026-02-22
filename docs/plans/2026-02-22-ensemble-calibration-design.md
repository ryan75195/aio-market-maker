# Ensemble Calibration Integration Design

**Date:** 2026-02-22
**Based on:** `docs/investigations/2026-02-22-ensemble-calibration-experiment.md`

## Goal

Integrate the validated logistic regression ensemble into the variant classification pipeline so confidence scores become calibrated and trustworthy for downstream filtering/pricing.

## Architecture

```
VariantModelRunner              (ONNX inference → raw logits)
         ↓ injected into
VariantClassifier : IVariantClassifierClient  (logits + similarity → calibrated verdict)
         ↓ injected into
ComparablesEtlService           (calls Classify, knows nothing about internals)
```

### VariantModelRunner (rename from OnnxVariantClassifier)

Dumb ONNX model wrapper. Tokenize input pairs, run inference, return raw logits. No softmax-to-confidence conversion — that's the calibration layer's job.

Returns `PairResult` with `LogitDiff` populated (difference between class 1 and class 0 logits).

### VariantClassifier (new)

Implements `IVariantClassifierClient`. Injects `VariantModelRunner` + `EnsembleConfig`. For each pair:

1. Call `VariantModelRunner.Classify()` to get raw logits
2. Read `SimilarityScore` from the request
3. Apply logistic regression: `score = w1 * logitDiff + w2 * similarity + intercept`
4. Convert to probability: `confidence = sigmoid(score)`
5. Return `PairResult(isComparable: confidence > 0.5, confidence)`

**Fallback:** When `SimilarityScore` is null, use cross-encoder logits only with temperature scaling or pass through raw softmax. This preserves backward compatibility for any caller that doesn't have similarity scores.

### DTO Changes

```csharp
public record ClassifyPairRequest(
    string TitleA,
    string DescriptionA,
    string TitleB,
    string DescriptionB,
    float? SimilarityScore = null);  // NEW — optional, backward-compatible

public record PairResult(
    bool IsComparable,
    float Confidence,
    string? Reason = null,
    float? LogitDiff = null);  // NEW — populated by VariantModelRunner
```

### Config

```json
"VariantClassifier": {
    "ModelPath": "...",
    "VocabPath": "...",
    "MergesPath": "...",
    "MaxLength": 256,
    "Ensemble": {
        "LogitWeight": 2.4910,
        "SimilarityWeight": 0.4324,
        "Intercept": -2.6254
    }
}
```

### DI Registration

```csharp
services.AddSingleton<VariantModelRunner>();
services.AddSingleton<IVariantClassifierClient, VariantClassifier>();
```

`VariantModelRunner` is no longer registered as `IVariantClassifierClient` — only `VariantClassifier` implements that interface. Downstream consumers are unaware of the change.

## Pipeline Change in ComparablesEtlService

Currently similarity scores are available in `CandidatePair.Score` but not passed to the classifier. Change:

```csharp
// Before
new ClassifyPairRequest(a.Title, a.Description, b.Title, b.Description)

// After
new ClassifyPairRequest(a.Title, a.Description, b.Title, b.Description, pair.Score)
```

## What Doesn't Change

- Vector index / embedding pipeline
- Database schema (ListingRelationships, ListingPredictions)
- API endpoints / response formats
- ONNX model file — no retraining
- LlmVariantClassifier — still implements IVariantClassifierClient independently

## Coefficients

From 5-fold cross-validated experiment on 143K labeled pairs:

| Parameter | Value |
|---|---|
| LogitWeight | 2.4910 |
| SimilarityWeight | 0.4324 |
| Intercept | -2.6254 |

## Risks

| Risk | Mitigation |
|---|---|
| No similarity score available | Fallback to cross-encoder only when SimilarityScore is null |
| Coefficients drift | Re-run experiment script when ONNX model or embedding model changes |
| Added latency | Negligible — one multiply-add + sigmoid per pair vs ~0.5ms for ONNX |
