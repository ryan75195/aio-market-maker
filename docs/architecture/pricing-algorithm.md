# Pricing Algorithm

How the system estimates the market value of an active eBay listing from comparable sold listings.

## Overview

A single SQL CTE (Common Table Expression) powers all pricing — the list view, detail view, and dashboard aggregates. Both `/api/listings/active` and `/api/listings/{id}` produce identical numbers for the same listing.

```
ListingRelationships (classifier output)
    |
    v
RawComps          Find all comparable sold listings
    |
    v
RawCompPrices     Compute IQR percentiles (Q1, Q3) per active listing
    |
    v
CleanedComps      Remove price outliers outside Tukey's fences
    |
    v
Aggregated        Weighted average, profit, confidence score, days-to-sell
    |
    v
FilteredPredictions   Join with median, apply min-comps and profit > 0 filters
```

## Stage 1: Find Comparables (RawComps)

Looks at `ListingRelationships` where `IsComparable = 1`. Since relationships are stored with `ListingIdA < ListingIdB`, the CTE uses a `UNION ALL` to check both directions — the active listing could be on either side.

Optional filters applied at this stage:
- **Condition match**: only include comps with the same condition (New, Used, etc.)
- **Single listing filter**: when computing a detail view, restricts to one active listing

## Stage 2: IQR Outlier Removal (RawCompPrices + CleanedComps)

### Why

Comparable sold listings sometimes include bundles, accessories-only listings, or wrong variants that the classifier didn't catch. These produce prices far from the true market value and skew the average.

### How

Uses Tukey's Fences (Interquartile Range method):

1. Compute Q1 (25th percentile) and Q3 (75th percentile) of sold prices using `PERCENTILE_CONT` window functions
2. Calculate IQR = Q3 - Q1
3. Define fences: Lower = Q1 - 1.5 * IQR, Upper = Q3 + 1.5 * IQR
4. Exclude any comp whose sold price falls outside these fences

**Edge cases:**
- Fewer than 4 comps: IQR is skipped (too few data points to determine outliers)
- IQR = 0 (all prices identical): IQR is skipped (nothing to remove)

The 1.5 multiplier is the standard statistical convention (Tukey, 1977). Configurable via `PricingOptions.IqrMultiplier`.

### Example

Sold prices: [£50, £55, £60, £65, £200]

- Q1 = £52.50, Q3 = £62.50, IQR = £10
- Lower fence = £52.50 - £15 = £37.50
- Upper fence = £62.50 + £15 = £77.50
- £200 is above £77.50 -> removed
- Result: [£50, £55, £60, £65], OutliersRemoved = 1

## Stage 3: Weighted Average (Aggregated)

Each cleaned comp gets a weight based on two factors multiplied together:

### Classifier confidence weight

```
weight_confidence = confidence ^ power
```

Where `confidence` is `ISNULL(ClassifierConfidence, SimilarityScore)` — uses the ensemble classifier confidence if available, falls back to raw vector similarity for pre-migration data. Default `power = 2.0`.

| Confidence | Weight (power=2) |
|-----------|-----------------|
| 0.99 | 0.98 |
| 0.90 | 0.81 |
| 0.70 | 0.49 |
| 0.50 | 0.25 |

High-confidence comps have ~4x the influence of borderline ones.

### Recency weight

```
weight_recency = exp(-days_since_sold / half_life)
```

Default `half_life = 30 days`. Exponential decay means recent sales dominate.

| Days since sold | Weight |
|----------------|--------|
| 0 | 1.00 |
| 15 | 0.61 |
| 30 | 0.37 |
| 60 | 0.14 |
| 120 | 0.02 |

If a comp has no sold date (`EndDateUtc IS NULL`), recency weight defaults to 1.0.

### Combined weight

```
weight = confidence^power * exp(-days / half_life)
```

A comp sold yesterday with 0.99 confidence gets weight ~0.98. A comp sold 90 days ago with 0.60 confidence gets weight ~0.36 * 0.05 = ~0.018. The recent, high-confidence comp has 54x more influence.

### Weighted average

```
AverageSoldPrice = SUM(price * weight) / SUM(weight)
```

## Stage 4: Confidence Score

A 0-1 number indicating how reliable the price estimate is. Three weighted components:

### Sample size factor (30%)

```
1 - exp(-count / target)
```

Default `target = 20`. Exponential saturation — diminishing returns as count grows.

| Comps | Factor |
|-------|--------|
| 3 | 0.14 |
| 5 | 0.22 |
| 10 | 0.39 |
| 20 | 0.63 |
| 40 | 0.86 |

### Classifier confidence factor (40%)

Average `ISNULL(ClassifierConfidence, SimilarityScore)` across all cleaned comps. If the model is confident these are truly the same item (avg 0.95), this contributes 0.38 to the score. If borderline (avg 0.55), it contributes 0.22.

### Price consistency factor (30%)

```
max(0, 1 - coefficient_of_variation)
```

Where CV = stddev / mean. If all comps sold at similar prices (CV near 0), factor approaches 1. If prices are scattered (CV > 1), factor is 0.

### Combined

```
confidence = 0.30 * sample_factor + 0.40 * avg_classifier_confidence + 0.30 * consistency_factor
```

### Example

15 comps, avg classifier confidence 0.92, CV of 0.15:
- Sample: 0.30 * (1 - exp(-15/20)) = 0.30 * 0.53 = 0.16
- Classifier: 0.40 * 0.92 = 0.37
- Consistency: 0.30 * (1 - 0.15) = 0.26
- **Confidence = 0.79**

## Stage 5: Median and Profit

**Median**: Computed via `PERCENTILE_CONT(0.5)` on cleaned comps in a separate CTE (window functions can't mix with GROUP BY).

**Profit**:
```
profit = AverageSoldPrice * (1 - feePercent/100) - ListedPrice - ShippingCost
```

Where `feePercent` defaults to 13.25% (eBay FVF ~12.9% + Managed Payments ~0.35%).

Only listings with `profit > 0` and `count >= minComps` appear in results.

## Configuration

All parameters are in `PricingOptions`, bound from `appsettings.json` under the `Pricing` section:

| Parameter | Default | Controls |
|-----------|---------|----------|
| `IqrMultiplier` | 1.5 | Outlier sensitivity. Higher = more lenient |
| `ConfidenceWeightPower` | 2.0 | Exponent on classifier confidence. Higher = more emphasis on high-confidence comps |
| `RecencyHalfLifeDays` | 30.0 | Exponential decay half-life. Lower = more emphasis on recent sales |
| `ConfidenceSampleTarget` | 20 | Target comp count for sample size factor |
| `SampleSizeWeight` | 0.3 | Weight of sample size in confidence score |
| `ClassifierConfidenceWeight` | 0.4 | Weight of classifier confidence in confidence score |
| `ConsistencyWeight` | 0.3 | Weight of price consistency in confidence score |
| `FeePercent` | 13.25 | eBay fees deducted from profit |
| `MinComps` | 3 | Minimum comps to show a listing as an opportunity |

The frontend settings page exposes `FeePercent`, `MinComps`, `PriceBand`, and `MatchCondition` as query parameters. The remaining parameters are server-side only.

## Key Files

| File | Purpose |
|------|---------|
| `Core/Services/ListingPredictionService.cs` | SQL CTE builder + query execution |
| `Core/Services/PricingOptions.cs` | Configuration model |
| `Core/Services/PricingCalculator.cs` | Standalone C# calculator (used by CLI `pricing` command only) |
| `Core/Data/Models/ListingRelationship.cs` | `ClassifierConfidence` column |
| `Tests.Integration/Services/ListingPredictionService_PricingIntegrationTests.cs` | 7 integration tests against LocalDB |
| `Tests.Unit/Services/PricingCalculator_UnitTests.cs` | 24 unit tests for the standalone calculator |

## Accuracy Limitations

The pricing algorithm's accuracy ceiling is set by **comp quality**, not the math. The biggest remaining improvement lever is the variant classifier — every false positive (wrong variant rated 0.95) gets amplified by the confidence weighting, and every false negative (correct comp missed) reduces the sample size.

For listings where `ClassifierConfidence` is NULL (pre-migration data), the system falls back to `SimilarityScore` (raw vector cosine similarity). This is a weaker signal. Pricing quality improves as new ETL runs populate real classifier confidence values.
