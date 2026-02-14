using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using System.Diagnostics;

namespace AIOMarketMaker.Tests.Integration;

[TestFixture]
[Category("Integration")]
[Explicit("Requires ONNX model files at E:/Dev/ml-training/variant-classifier/model_v6_onnx/")]
public class OnnxVariantClassifier_IntegrationTests
{
    private const string ModelDir = "E:/Dev/ml-training/variant-classifier/model_v6_onnx";

    private static OnnxVariantClassifier _classifier = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var config = new OnnxClassifierConfig(
            ModelPath: Path.Combine(ModelDir, "model.onnx"),
            VocabPath: Path.Combine(ModelDir, "vocab.json"),
            MergesPath: Path.Combine(ModelDir, "merges.txt"));

        _classifier = new OnnxVariantClassifier(config, Mock.Of<ILogger<OnnxVariantClassifier>>());
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _classifier.Dispose();
    }

    // --- Same product pairs (should classify as comparable) ---

    [TestCaseSource(nameof(SameProductPairs))]
    public async Task Should_classify_same_product_as_comparable(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.True, $"Expected comparable: {description}");
            Assert.That(result.Confidence, Is.GreaterThan(0.80f), $"Expected high confidence: {description}");
            Assert.That(result.Confidence, Is.GreaterThan(0.50f), $"Should have decisive confidence: {description}");
        });

        TestContext.WriteLine($"[SAME] {description}: confidence={result.Confidence:F4}");
    }

    private static IEnumerable<TestCaseData> SameProductPairs()
    {
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Dyson V15 Detect Absolute Cordless Vacuum",
                "Cordless vacuum cleaner with laser dust detection and LCD screen",
                "Dyson V15 Detect Absolute Cordless Vacuum Cleaner",
                "Brand new Dyson V15 Detect Absolute with laser illuminate technology"),
            "Dyson V15 — same model, different wording"
        ).SetDescription("Same Dyson vacuum, slightly different titles and descriptions");

        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple iPhone 15 Pro Max 256GB Natural Titanium",
                "Apple iPhone 15 Pro Max smartphone with 256GB storage in Natural Titanium finish",
                "iPhone 15 Pro Max 256GB - Natural Titanium - Unlocked",
                "Brand new iPhone 15 Pro Max 256GB Natural Titanium, factory unlocked"),
            "iPhone 15 Pro Max 256GB — same product, seller wording differs"
        ).SetDescription("Same iPhone model/storage/color, different listing style");

        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Sony PlayStation 5 Slim Disc Edition Console",
                "PS5 Slim with disc drive, includes DualSense controller",
                "PS5 Slim Disc Edition - Sony PlayStation 5 Console",
                "Sony PlayStation 5 Slim disc edition, brand new sealed"),
            "PS5 Slim Disc — abbreviations vs full name"
        ).SetDescription("Same PS5 variant with different naming conventions");

        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Samsung Galaxy S24 Ultra 512GB Titanium Black Unlocked",
                "Samsung Galaxy S24 Ultra with 512GB storage, titanium black, unlocked",
                "Galaxy S24 Ultra 512GB - Titanium Black - Factory Unlocked",
                "Brand new Samsung S24 Ultra 512GB titanium black unlocked smartphone"),
            "Galaxy S24 Ultra 512GB — same spec, different format"
        ).SetDescription("Same Samsung phone with identical specs differently worded");
    }

    // --- Different product pairs (should classify as not comparable) ---

    [TestCaseSource(nameof(DifferentProductPairs))]
    public async Task Should_classify_different_products_as_not_comparable(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.False, $"Expected not comparable: {description}");
            Assert.That(result.Confidence, Is.GreaterThan(0.80f), $"Expected high confidence: {description}");
            Assert.That(result.Confidence, Is.GreaterThan(0.50f), $"Should have decisive confidence: {description}");
        });

        TestContext.WriteLine($"[DIFF] {description}: confidence={result.Confidence:F4}");
    }

    private static IEnumerable<TestCaseData> DifferentProductPairs()
    {
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Sony PlayStation 5 Slim Disc Edition",
                "PS5 Slim console with disc drive",
                "Sony PlayStation 5 Slim Digital Edition",
                "PS5 Slim digital console, no disc drive"),
            "PS5 Disc vs Digital — different variant"
        ).SetDescription("Same product line but different variants (disc vs digital)");

        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple iPhone 15 Pro 256GB",
                "iPhone 15 Pro with 256GB storage",
                "Apple iPhone 15 Plus 128GB",
                "iPhone 15 Plus with 128GB storage"),
            "iPhone 15 Pro vs Plus — different model and storage"
        ).SetDescription("Different iPhone model and storage capacity");

        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple iPad Air 11-inch M2 256GB WiFi",
                "iPad Air 11 inch with M2 chip, 256GB, WiFi only",
                "Apple iPad Pro 12.9-inch M2 512GB WiFi + Cellular",
                "iPad Pro 12.9 inch M2 chip, 512GB, WiFi and cellular"),
            "iPad Air 11\" vs iPad Pro 12.9\" — completely different models"
        ).SetDescription("Different iPad lines, sizes, storage, connectivity");

        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Dyson V15 Detect Absolute Cordless Vacuum",
                "Cordless vacuum with laser dust detection",
                "Dyson Airwrap Complete Long Styler",
                "Hair styler with multiple attachments"),
            "Dyson vacuum vs Dyson hair styler — different product category"
        ).SetDescription("Same brand but completely different product categories");
    }

    // --- Similar-but-different variant pairs ---

    [TestCaseSource(nameof(SimilarButDifferentPairs))]
    public async Task Should_distinguish_similar_but_different_variants(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        Assert.That(result.IsComparable, Is.False, $"Expected not comparable: {description}");

        TestContext.WriteLine($"[VARIANT] {description}: isComparable={result.IsComparable}, confidence={result.Confidence:F4}");
    }

    private static IEnumerable<TestCaseData> SimilarButDifferentPairs()
    {
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Samsung Galaxy S24 Ultra 256GB",
                "Samsung Galaxy S24 Ultra smartphone with 256GB storage",
                "Samsung Galaxy S24 Ultra 512GB",
                "Samsung Galaxy S24 Ultra smartphone with 512GB storage"),
            "Galaxy S24 Ultra 256GB vs 512GB — different storage"
        ).SetDescription("Same phone model but different storage capacity");

        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple MacBook Pro 14-inch M3 Pro 18GB 512GB",
                "MacBook Pro 14 inch with M3 Pro chip, 18GB RAM, 512GB SSD",
                "Apple MacBook Pro 14-inch M3 Max 36GB 1TB",
                "MacBook Pro 14 inch with M3 Max chip, 36GB RAM, 1TB SSD"),
            "MacBook Pro M3 Pro vs M3 Max — different chip/RAM/storage"
        ).SetDescription("Same laptop form factor but different tier configuration");

        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nintendo Switch OLED Model White",
                "Nintendo Switch OLED console with white Joy-Con controllers",
                "Nintendo Switch Lite Turquoise",
                "Nintendo Switch Lite handheld console in turquoise"),
            "Switch OLED vs Switch Lite — different console variant"
        ).SetDescription("Same brand/line but fundamentally different hardware");
    }

    // --- Empty description edge case ---

    [Test]
    public async Task Should_handle_empty_descriptions()
    {
        var pair = new ClassifyPairRequest(
            "Sony PlayStation 5 Slim Disc Edition",
            "",
            "PS5 Slim Disc Edition Console",
            "");

        var results = await _classifier.Classify([pair]);
        var result = results[0];

        Assert.Multiple(() =>
        {
            Assert.That(result.Confidence, Is.GreaterThan(0f), "Should produce a valid confidence");
            Assert.That(result.Confidence, Is.LessThanOrEqualTo(1f), "Confidence should be <= 1.0");
        });

        TestContext.WriteLine($"[EMPTY DESC] PS5 title-only: isComparable={result.IsComparable}, confidence={result.Confidence:F4}");
    }

    // --- Performance test ---

    [Test]
    public async Task Should_classify_within_acceptable_latency()
    {
        var pair = new ClassifyPairRequest(
            "Test Product A",
            "Description for product A",
            "Test Product B",
            "Description for product B");

        // Warm up (first inference can be slow due to CUDA kernel compilation)
        await _classifier.Classify([pair]);

        // Measure 10 inferences
        var sw = Stopwatch.StartNew();
        const int iterations = 10;
        for (var i = 0; i < iterations; i++)
        {
            await _classifier.Classify([pair]);
        }
        sw.Stop();

        var avgMs = sw.ElapsedMilliseconds / (double)iterations;
        TestContext.WriteLine($"Average latency: {avgMs:F1} ms/pair over {iterations} iterations");

        // GPU should be < 50ms, CPU < 1500ms — use generous upper bound
        Assert.That(avgMs, Is.LessThan(1500), "Average inference should be under 1500ms (CPU ceiling)");
    }

    // --- Batch classification test ---

    [Test]
    public async Task Should_classify_multiple_pairs_in_single_call()
    {
        var pairs = new[]
        {
            new ClassifyPairRequest(
                "Sony WH-1000XM5 Wireless Headphones",
                "Premium noise cancelling wireless headphones",
                "Sony WH-1000XM5 Headphones Black",
                "Sony wireless noise cancelling headphones in black"),
            new ClassifyPairRequest(
                "Sony WH-1000XM5 Wireless Headphones",
                "Premium noise cancelling wireless headphones",
                "Apple AirPods Max Silver",
                "Apple over-ear headphones in silver finish")
        };

        var results = await _classifier.Classify(pairs);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results[0].IsComparable, Is.True, "Same Sony headphones should be comparable");
            Assert.That(results[1].IsComparable, Is.False, "Sony vs Apple headphones should not be comparable");
        });

        TestContext.WriteLine($"Pair 1 (same): confidence={results[0].Confidence:F4}");
        TestContext.WriteLine($"Pair 2 (diff): confidence={results[1].Confidence:F4}");
    }

    // --- Health check ---

    [Test]
    public async Task Should_report_healthy_when_model_loaded()
    {
        var healthy = await _classifier.IsHealthy();
        Assert.That(healthy, Is.True);
    }
}
