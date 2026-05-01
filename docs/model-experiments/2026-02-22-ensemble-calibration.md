# Ensemble Calibration Experiment: Cross-Encoder + Similarity Score

**Date:** 2026-02-22
**Status:** Experiment complete, integration pending

## Problem

The v10 ONNX variant classifier (RoBERTa-large cross-encoder) achieves 94.8% accuracy but produces overconfident predictions. When the model says it's 80-90% confident, it's only correct 56% of the time — essentially a coin flip. This means confidence scores are unreliable for downstream filtering or ranking.

| Confidence Bucket | Predictions | Actual Accuracy |
|---|---|---|
| 0.80 - 0.90 | 1,326 | 56.2% |
| 0.90 - 0.95 | 3,784 | 70.7% |
| 0.95 - 1.00 | 136,771 | 96.3% |

## Hypothesis

The comparables pipeline already computes OpenAI embedding cosine similarity (from `text-embedding-3-large`) for every candidate pair during vector search. This score is discarded before classification — the cross-encoder only sees raw text. Since OpenAI's embedding model and RoBERTa are completely different models trained on different data, they may carry complementary signals. A simple logistic regression on both outputs could improve calibration and potentially flip some incorrect predictions.

## Approach

1. Load the USearch vector index (265K vectors, 3072 dims) and compute cosine similarity for all 143K labeled pairs
2. Run all pairs through the ONNX cross-encoder to collect raw logits
3. Fit a logistic regression on `[logit_diff, similarity_score]` → binary label
4. Evaluate using 5-fold cross-validation (unbiased predictions)

The logistic regression acts as a lightweight "referee" that sees both the cross-encoder's opinion and the embedding similarity score.

## Results

### Accuracy

| Metric | Cross-Encoder | Ensemble | Delta |
|---|---|---|---|
| Accuracy | 0.9484 | 0.9489 | +0.0006 |
| F1 (macro) | 0.9227 | 0.9231 | +0.0003 |
| NLL (log loss) | 0.1963 | 0.1541 | -0.0422 (21% better) |

563 predictions changed out of 143K (0.39%):
- 321 flipped wrong → correct
- 242 flipped correct → wrong
- **Net: +79 correct predictions**

### Calibration (the real win)

| Confidence Bucket | Cross-Encoder Accuracy | Ensemble Accuracy |
|---|---|---|
| 0.50 - 0.60 | 47.2% | 56.2% |
| 0.60 - 0.70 | 47.1% | 56.8% |
| 0.70 - 0.80 | 53.6% | 62.5% |
| 0.80 - 0.90 | 56.2% | 76.8% |
| 0.90 - 0.95 | 70.7% | 90.7% |
| 0.95 - 1.00 | 96.3% | 98.8% |

The ensemble's confidence scores are significantly more trustworthy. At 0.90-0.95 confidence, the ensemble is correct 91% of the time vs the cross-encoder's 71%.

### Learned Weights

```
Logit diff weight:    2.4910
Similarity weight:    0.4324
Intercept:           -2.6254
```

The cross-encoder dominates (6:1 ratio), but the similarity score carries a meaningful signal. The negative intercept means the ensemble requires stronger evidence to predict "match" than the cross-encoder alone.

## Why It Works

RoBERTa and OpenAI's embedding model make different mistakes:

- **RoBERTa says match, but similarity is low (0.72):** The ensemble lowers confidence. "You think they match but the embeddings say they're not that similar — something's off."
- **RoBERTa is uncertain, but similarity is very high (0.96):** The ensemble nudges toward match. "The cross-encoder isn't sure, but these listings are nearly identical in embedding space."
- **Both agree (high confidence + high similarity):** Confidence stays high. Agreement between independent models is strong evidence.

The key insight: these are fundamentally different models. OpenAI's embeddings capture broad semantic similarity from a massive general corpus. RoBERTa was fine-tuned specifically on eBay product pairs. They see different aspects of the same data.

## Comparison with Temperature Scaling

We also tested temperature scaling (T=1.58, found via NLL minimization). The ensemble outperforms it:

| Method | NLL | Conf on Wrong Predictions |
|---|---|---|
| Baseline (T=1.0) | 0.1963 | 0.935 |
| Temperature (T=1.58) | 0.1614 | 0.876 |
| Ensemble (LR) | 0.1541 | 0.858 |

Temperature scaling also cannot change predictions (it preserves argmax). The ensemble can and does flip 563 predictions, netting +79 correct.

## Architecture: Current vs Proposed

### Current Pipeline

```
Active Listing
    → Vector Index (USearch, OpenAI embeddings)
    → Top-K neighbors with similarity scores
    → Filter to sold neighbors
    → Cross-encoder classifies each pair (title + description text only)
        → Binary: match/not-match + confidence
    → Store as ListingRelationship
    → Compute price prediction from confirmed comparables
```

The similarity score from the vector search is discarded after candidate selection. The cross-encoder makes its decision purely from reading the text.

### Proposed Pipeline

```
Active Listing
    → Vector Index (USearch, OpenAI embeddings)
    → Top-K neighbors with similarity scores        ←── score preserved
    → Filter to sold neighbors
    → Cross-encoder classifies each pair (title + description text only)
        → Raw logits
    → Ensemble layer (logistic regression)           ←── NEW
        → Input: [logit_diff, similarity_score]
        → Output: calibrated probability
    → Store as ListingRelationship (with calibrated confidence)
    → Compute price prediction from confirmed comparables
```

### What Changes

| Component | Change |
|---|---|
| `OnnxVariantClassifier.Classify()` | Return raw logits alongside predictions (or expose a `ClassifyWithLogits` method) |
| `ComparablesEtlService` | Pass similarity scores through to the classifier |
| New: `EnsembleClassifier` | Wraps cross-encoder + logistic regression. Takes `(ClassifyPairRequest, float similarity)` → `PairResult` |
| `ClassifyPairRequest` | Add optional `SimilarityScore` field |
| Config | Add ensemble model coefficients (3 floats: weight_logit, weight_sim, intercept) |

### What Doesn't Change

- Vector index / embedding pipeline — same as today
- Database schema (`ListingRelationships`, `ListingPredictions`) — same fields
- API endpoints — same response format
- ONNX model file — no retraining needed
- DI wiring pattern — same singleton lifecycle

## Integration Steps

1. **Expose raw logits from `OnnxVariantClassifier`** — add a method or modify `Classify()` to return logits alongside the softmax prediction
2. **Thread similarity scores through the pipeline** — `ComparablesEtlService` already has the similarity score from the vector search; pass it to the classifier instead of discarding it
3. **Implement `EnsembleClassifier`** — a thin wrapper that:
   - Calls the ONNX classifier to get logits
   - Applies the logistic regression: `score = w1 * logit_diff + w2 * similarity + intercept`
   - Converts to probability via sigmoid: `prob = 1 / (1 + exp(-score))`
   - Returns `PairResult(isComparable, calibratedConfidence)`
4. **Store coefficients in config** — three floats from the fitted model, in `appsettings.json` under `VariantClassifier:Ensemble`
5. **Recompute comparables** — run `--comparables` to regenerate all predictions with calibrated confidence

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Coefficients overfit to training data | 5-fold cross-validation showed consistent results across folds |
| Similarity score unavailable for some pairs | Fall back to cross-encoder only (T=1.0 baseline) when similarity is missing |
| Ensemble adds latency | Negligible — one multiply-add + sigmoid per pair (~nanoseconds vs ~0.5ms for ONNX inference) |
| Coefficients drift as model/data changes | Re-run `experiment_ensemble.py` periodically; coefficients are stable as long as the ONNX model and embedding model don't change |

## Files

| File | Purpose |
|---|---|
| `AIOMarketMaker.ML/Training/experiment_ensemble.py` | Experiment script (loads vectors, runs ONNX, fits LR, reports metrics) |
| `AIOMarketMaker.ML/Training/eval-v10-onnx.py` | Updated eval script (removed description truncation) |
| `AIOMarketMaker.ML/Services/OnnxVariantClassifier.cs` | Updated (removed description truncation that caused train/serve skew) |

## Decision

Experiment confirms the ensemble approach is worth integrating. The accuracy improvement is modest (+0.05%), but the calibration improvement is significant — confidence scores become trustworthy enough to use for downstream filtering or pricing weight adjustments.

**Recommendation:** Integrate the ensemble as described above. The implementation is lightweight (3 config floats + ~20 lines of code) and the similarity score is already computed in the pipeline.
