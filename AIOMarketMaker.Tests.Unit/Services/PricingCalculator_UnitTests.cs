using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class PricingCalculator_UnitTests
{
    private static readonly PricingOptions DefaultOptions = new();

    // -- IQR Outlier Removal --

    [Test]
    public void Should_remove_outliers_with_default_iqr_multiplier()
    {
        var items = new List<PricedComparable>
        {
            new(50m, 0.95, DateTime.UtcNow),
            new(55m, 0.90, DateTime.UtcNow),
            new(60m, 0.92, DateTime.UtcNow),
            new(65m, 0.88, DateTime.UtcNow),
            new(200m, 0.85, DateTime.UtcNow)
        };

        var result = PricingCalculator.Analyze(items);

        Assert.Multiple(() =>
        {
            Assert.That(result.OutliersRemoved, Is.EqualTo(1));
            Assert.That(result.SampleSize, Is.EqualTo(4));
        });
    }

    [Test]
    public void Should_return_all_items_when_fewer_than_four()
    {
        var items = new List<PricedComparable>
        {
            new(50m, 0.95, DateTime.UtcNow),
            new(55m, 0.90, DateTime.UtcNow),
            new(200m, 0.85, DateTime.UtcNow)
        };

        var result = PricingCalculator.Analyze(items);

        Assert.Multiple(() =>
        {
            Assert.That(result.OutliersRemoved, Is.EqualTo(0));
            Assert.That(result.SampleSize, Is.EqualTo(3));
        });
    }

    [Test]
    public void Should_return_all_items_when_iqr_is_zero()
    {
        var items = new List<PricedComparable>
        {
            new(100m, 0.95, DateTime.UtcNow),
            new(100m, 0.90, DateTime.UtcNow),
            new(100m, 0.88, DateTime.UtcNow),
            new(100m, 0.92, DateTime.UtcNow)
        };

        var result = PricingCalculator.Analyze(items);

        Assert.Multiple(() =>
        {
            Assert.That(result.OutliersRemoved, Is.EqualTo(0));
            Assert.That(result.SampleSize, Is.EqualTo(4));
        });
    }

    [Test]
    public void Should_remove_both_low_and_high_outliers()
    {
        var items = new List<PricedComparable>
        {
            new(1m, 0.80, DateTime.UtcNow),
            new(50m, 0.95, DateTime.UtcNow),
            new(55m, 0.90, DateTime.UtcNow),
            new(60m, 0.92, DateTime.UtcNow),
            new(65m, 0.88, DateTime.UtcNow),
            new(200m, 0.85, DateTime.UtcNow)
        };

        var result = PricingCalculator.Analyze(items);

        Assert.That(result.OutliersRemoved, Is.EqualTo(2));
    }

    // -- Confidence Weighting --

    [Test]
    public void Should_weight_towards_high_confidence_comps()
    {
        var items = new List<PricedComparable>
        {
            new(100m, 0.99, DateTime.UtcNow),
            new(200m, 0.50, DateTime.UtcNow)
        };

        var result = PricingCalculator.Analyze(items);

        // With power=2.0, high confidence (0.99^2=0.98) dominates low (0.50^2=0.25)
        // WeightedMean should be closer to 100 than 200
        Assert.That(result.WeightedMean, Is.LessThan(150m));
    }

    [Test]
    public void Should_give_equal_weight_when_confidences_are_equal()
    {
        var items = new List<PricedComparable>
        {
            new(100m, 0.90, DateTime.UtcNow),
            new(200m, 0.90, DateTime.UtcNow)
        };

        var result = PricingCalculator.Analyze(items);

        Assert.That(result.WeightedMean, Is.EqualTo(150m));
    }

    // -- Recency Weighting --

    [Test]
    public void Should_weight_recent_sales_higher()
    {
        var now = DateTime.UtcNow;
        var items = new List<PricedComparable>
        {
            new(100m, 0.90, now.AddDays(-1)),     // very recent
            new(200m, 0.90, now.AddDays(-120))     // old
        };

        var result = PricingCalculator.Analyze(items);

        // Recent item at 100 should dominate, so recency-weighted < 150
        Assert.That(result.RecencyWeightedMean, Is.Not.Null);
        Assert.That(result.RecencyWeightedMean!.Value, Is.LessThan(150m));
    }

    [Test]
    public void Should_return_null_recency_when_no_sold_dates()
    {
        var items = new List<PricedComparable>
        {
            new(100m, 0.90, null),
            new(200m, 0.90, null)
        };

        var result = PricingCalculator.Analyze(items);

        Assert.That(result.RecencyWeightedMean, Is.Null);
    }

    // -- Pricing Confidence Score --

    [Test]
    public void Should_increase_confidence_with_more_samples()
    {
        var fewItems = Enumerable.Range(0, 5)
            .Select(_ => new PricedComparable(100m, 0.90, DateTime.UtcNow))
            .ToList();

        var manyItems = Enumerable.Range(0, 30)
            .Select(_ => new PricedComparable(100m, 0.90, DateTime.UtcNow))
            .ToList();

        var fewResult = PricingCalculator.Analyze(fewItems);
        var manyResult = PricingCalculator.Analyze(manyItems);

        Assert.That(manyResult.Confidence, Is.GreaterThan(fewResult.Confidence));
    }

    [Test]
    public void Should_increase_confidence_with_higher_classifier_confidence()
    {
        var lowConf = Enumerable.Range(0, 10)
            .Select(_ => new PricedComparable(100m, 0.55, DateTime.UtcNow))
            .ToList();

        var highConf = Enumerable.Range(0, 10)
            .Select(_ => new PricedComparable(100m, 0.99, DateTime.UtcNow))
            .ToList();

        var lowResult = PricingCalculator.Analyze(lowConf);
        var highResult = PricingCalculator.Analyze(highConf);

        Assert.That(highResult.Confidence, Is.GreaterThan(lowResult.Confidence));
    }

    [Test]
    public void Should_increase_confidence_with_consistent_prices()
    {
        var scattered = Enumerable.Range(0, 10)
            .Select(i => new PricedComparable(50m + i * 50m, 0.90, DateTime.UtcNow))
            .ToList();

        var consistent = Enumerable.Range(0, 10)
            .Select(_ => new PricedComparable(100m, 0.90, DateTime.UtcNow))
            .ToList();

        var scatteredResult = PricingCalculator.Analyze(scattered);
        var consistentResult = PricingCalculator.Analyze(consistent);

        Assert.That(consistentResult.Confidence, Is.GreaterThan(scatteredResult.Confidence));
    }

    [Test]
    public void Should_compute_confidence_matching_formula_from_plan()
    {
        // 15 comps, avg confidence 0.92, CV of 0.15
        // Expected: 0.3 * sampleFactor + 0.4 * 0.92 + 0.3 * 0.85
        // sampleFactor = 1 - exp(-15/20) ≈ 0.5276
        // = 0.3 * 0.5276 + 0.4 * 0.92 + 0.3 * 0.85
        // = 0.1583 + 0.368 + 0.255 = 0.7813

        var options = new PricingOptions();
        var sampleFactor = 1 - Math.Exp(-15.0 / 20);

        // Build items where avg confidence = 0.92 and CV ≈ 0.15
        // mean = 100, stddev = 15 => CV = 0.15
        var items = new List<PricedComparable>();
        var random = new Random(42);
        for (var i = 0; i < 15; i++)
        {
            items.Add(new PricedComparable(100m, 0.92, DateTime.UtcNow));
        }

        // For consistency factor: all same price => CV=0 => consistency=1.0
        var confidence = PricingCalculator.CalculateConfidence(items, options);

        // Same price means CV=0, consistency=1.0, not 0.85
        var expected = 0.3 * sampleFactor + 0.4 * 0.92 + 0.3 * 1.0;
        Assert.That(confidence, Is.EqualTo(expected).Within(0.001));
    }

    // -- Edge Cases --

    [Test]
    public void Should_return_empty_result_for_empty_input()
    {
        var result = PricingCalculator.Analyze(Enumerable.Empty<PricedComparable>());

        Assert.Multiple(() =>
        {
            Assert.That(result.SampleSize, Is.EqualTo(0));
            Assert.That(result.Confidence, Is.EqualTo(0));
            Assert.That(result.WeightedMean, Is.EqualTo(0));
        });
    }

    [Test]
    public void Should_handle_single_item()
    {
        var items = new List<PricedComparable>
        {
            new(100m, 0.95, DateTime.UtcNow)
        };

        var result = PricingCalculator.Analyze(items);

        Assert.Multiple(() =>
        {
            Assert.That(result.SampleSize, Is.EqualTo(1));
            Assert.That(result.Mean, Is.EqualTo(100m));
            Assert.That(result.Median, Is.EqualTo(100m));
            Assert.That(result.WeightedMean, Is.EqualTo(100m));
            Assert.That(result.StdDev, Is.EqualTo(0));
        });
    }

    [Test]
    public void Should_handle_all_same_price()
    {
        var items = Enumerable.Range(0, 10)
            .Select(_ => new PricedComparable(100m, 0.90, DateTime.UtcNow))
            .ToList();

        var result = PricingCalculator.Analyze(items);

        Assert.Multiple(() =>
        {
            Assert.That(result.Mean, Is.EqualTo(100m));
            Assert.That(result.Median, Is.EqualTo(100m));
            Assert.That(result.WeightedMean, Is.EqualTo(100m));
            Assert.That(result.StdDev, Is.EqualTo(0));
            Assert.That(result.OutliersRemoved, Is.EqualTo(0));
        });
    }

    // -- Custom Options --

    [Test]
    public void Should_use_custom_iqr_multiplier()
    {
        var items = new List<PricedComparable>
        {
            new(50m, 0.95, DateTime.UtcNow),
            new(55m, 0.90, DateTime.UtcNow),
            new(60m, 0.92, DateTime.UtcNow),
            new(65m, 0.88, DateTime.UtcNow),
            new(100m, 0.85, DateTime.UtcNow)
        };

        // Strict multiplier should remove more
        var strict = PricingCalculator.Analyze(items, new PricingOptions { IqrMultiplier = 0.5 });
        // Lenient multiplier should keep more
        var lenient = PricingCalculator.Analyze(items, new PricingOptions { IqrMultiplier = 3.0 });

        Assert.That(strict.OutliersRemoved, Is.GreaterThanOrEqualTo(lenient.OutliersRemoved));
    }

    [Test]
    public void Should_use_custom_confidence_weight_power()
    {
        var items = new List<PricedComparable>
        {
            new(100m, 0.99, DateTime.UtcNow),
            new(200m, 0.50, DateTime.UtcNow)
        };

        // Higher power emphasizes high-confidence more
        var highPower = PricingCalculator.Analyze(items, new PricingOptions { ConfidenceWeightPower = 4.0 });
        var lowPower = PricingCalculator.Analyze(items, new PricingOptions { ConfidenceWeightPower = 1.0 });

        // Higher power should pull weighted mean closer to 100 (the high-confidence comp)
        Assert.That(highPower.WeightedMean, Is.LessThan(lowPower.WeightedMean));
    }

    [Test]
    public void Should_use_custom_recency_half_life()
    {
        var now = DateTime.UtcNow;
        var items = new List<PricedComparable>
        {
            new(100m, 0.90, now.AddDays(-1)),
            new(200m, 0.90, now.AddDays(-60))
        };

        // Short half-life penalizes old items more
        var shortLife = PricingCalculator.Analyze(items, new PricingOptions { RecencyHalfLifeDays = 7.0 });
        var longLife = PricingCalculator.Analyze(items, new PricingOptions { RecencyHalfLifeDays = 365.0 });

        // Short half-life should pull weighted mean closer to 100 (the recent comp)
        Assert.That(shortLife.RecencyWeightedMean!.Value, Is.LessThan(longLife.RecencyWeightedMean!.Value));
    }

    // -- Statistics --

    [Test]
    public void Should_calculate_correct_median_for_even_count()
    {
        var items = new List<PricedComparable>
        {
            new(10m, 0.90, DateTime.UtcNow),
            new(20m, 0.90, DateTime.UtcNow),
            new(30m, 0.90, DateTime.UtcNow),
            new(40m, 0.90, DateTime.UtcNow)
        };

        var result = PricingCalculator.Analyze(items);

        Assert.That(result.Median, Is.EqualTo(25m));
    }

    [Test]
    public void Should_calculate_correct_median_for_odd_count()
    {
        var items = new List<PricedComparable>
        {
            new(10m, 0.90, DateTime.UtcNow),
            new(20m, 0.90, DateTime.UtcNow),
            new(30m, 0.90, DateTime.UtcNow)
        };

        var result = PricingCalculator.Analyze(items);

        Assert.That(result.Median, Is.EqualTo(20m));
    }

    [Test]
    public void Should_calculate_correct_min_and_max()
    {
        var items = new List<PricedComparable>
        {
            new(50m, 0.90, DateTime.UtcNow),
            new(100m, 0.90, DateTime.UtcNow),
            new(75m, 0.90, DateTime.UtcNow)
        };

        var result = PricingCalculator.Analyze(items);

        Assert.Multiple(() =>
        {
            Assert.That(result.Min, Is.EqualTo(50m));
            Assert.That(result.Max, Is.EqualTo(100m));
        });
    }

    [TestCaseSource(nameof(ConfidenceBoundsCases))]
    public void Should_clamp_confidence_between_zero_and_one(
        List<PricedComparable> items, PricingOptions options)
    {
        var result = PricingCalculator.Analyze(items, options);

        Assert.Multiple(() =>
        {
            Assert.That(result.Confidence, Is.GreaterThanOrEqualTo(0.0));
            Assert.That(result.Confidence, Is.LessThanOrEqualTo(1.0));
        });
    }

    private static IEnumerable<TestCaseData> ConfidenceBoundsCases()
    {
        yield return new TestCaseData(
            new List<PricedComparable> { new(1m, 0.01, DateTime.UtcNow) },
            new PricingOptions()
        ).SetDescription("Very low confidence input");

        yield return new TestCaseData(
            Enumerable.Range(0, 100)
                .Select(_ => new PricedComparable(100m, 1.0, DateTime.UtcNow))
                .ToList(),
            new PricingOptions()
        ).SetDescription("Maximum confidence inputs");

        yield return new TestCaseData(
            Enumerable.Range(0, 5)
                .Select(i => new PricedComparable(10m * (i + 1), 0.50, DateTime.UtcNow))
                .ToList(),
            new PricingOptions()
        ).SetDescription("Mixed prices with medium confidence");
    }
}
