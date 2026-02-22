using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using System.Diagnostics;

namespace AIOMarketMaker.Tests.Integration;

[TestFixture]
[Category("Integration")]
[Explicit("Requires ONNX model files at E:/Dev/ml-training/variant-classifier/v8/onnx/")]
public class VariantModelRunner_IntegrationTests
{
    private const string ModelDir = "E:/Dev/ml-training/variant-classifier/v8/onnx";

    private static VariantModelRunner _classifier = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var config = new OnnxClassifierConfig(
            ModelPath: Path.Combine(ModelDir, "model.onnx"),
            VocabPath: Path.Combine(ModelDir, "vocab.json"),
            MergesPath: Path.Combine(ModelDir, "merges.txt"));

        _classifier = new VariantModelRunner(config, Mock.Of<ILogger<VariantModelRunner>>());
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
                "Sony PlayStation 5 Slim Disc Edition 1TB Console CFI-2015",
                "PS5 Slim 1TB console with 4K Blu-ray disc drive, DualSense wireless controller, HDMI 2.1 cable, 825GB SSD, brand new in box",
                "PS5 Slim Disc Edition - Sony PlayStation 5 1TB Console CFI-2015",
                "Sony PlayStation 5 Slim disc edition 1TB with disc drive, includes DualSense controller and all accessories, factory sealed"),
            "PS5 Slim 1TB Disc CFI-2015 — abbreviations vs full name"
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

    // --- V7 hard-negative regression tests (real training pairs) ---
    // These use real eBay listing pairs from the training data to prevent
    // regressions in categories that v7 specifically improved.

    [TestCaseSource(nameof(V7HardNegativePairs))]
    public async Task Should_reject_hard_negatives_with_subtle_spec_differences(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        Assert.That(result.IsComparable, Is.False, $"Expected not comparable: {description}");

        TestContext.WriteLine($"[HARD-NEG] {description}: confidence={result.Confidence:F4}");
    }

    private static IEnumerable<TestCaseData> V7HardNegativePairs()
    {
        // Cartier — different size and gold type (SM WG 17 vs New mode PG 16)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "CARTIER Love bracelet SM WG 17 17 Women bracelet",
                "Cartier's iconic LOVE bracelet, SM size, white gold, size 17",
                "CARTIER Love bracelet New mode PG 16 16 Women bracelet",
                "The Love bracelet is Cartier's iconic jewelry, new model in pink gold, size 16"),
            "Cartier Love SM WG 17 vs New mode PG 16 — different size/gold/model"
        ).SetDescription("Real pair: different Cartier size, metal, model");

        // Omega Seamaster — different calibres (503 vs 491) in same-era watches
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Omega Seamaster automatic - 1959 - B47",
                "Omega Seamaster automatic stainless steel, cal.503, ref 2849 14 SC, serial 1697, 1959, case size 34 mm",
                "Omega Seamaster From 1959",
                "Omega Seamaster Wristwatch from 1959. Swiss 19 jewelled automatic movement, calibre 491, serial number 16540980. Just had a full service"),
            "Omega Seamaster 1959 cal.503 vs cal.491 — different calibre"
        ).SetDescription("Real pair: same era Seamasters with different movements");

        // Pandora — different model numbers and clasp designs
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "GENUINE PANDORA Moments Rose in Bloom Snake Chain Bracelet 593211C00",
                "GENUINE PANDORA Moments Rose in Bloom Clasp Snake Chain Bracelet, Product number: 593211C00, 925 ALE Sterling Silver Hallmark",
                "GENUINE PANDORA x DISNEY Stitch Clasp Snake Chain Bracelet 593738C01",
                "GENUINE PANDORA x DISNEY Stitch Clasp Snake Chain Bracelet, Product number: 593738C01, 925 ALE Sterling Silver Hallmark & Enamel"),
            "Pandora 593211C00 Rose in Bloom vs 593738C01 Stitch — different models"
        ).SetDescription("Real pair: different Pandora bracelet models/clasp designs");

        // Mac Mini — M1 vs M4, completely different generations
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "M1 Mac Mini 16GB RAM 512GB SSD. Excellent Condition. Pristine",
                "M1 Mac Mini 16GB RAM 512GB SSD, Excellent Condition. Latest OS installed, iCloud unlocked. Comes with power cable.",
                "Apple Mac Mini 2025 May - M4 + Warranty, 1TB SSD 24GB RAM,10 Core CPU 10Core GPU",
                "Apple Mac mini (M4, 2025) - 24GB RAM, 1TB SSD - Under Apple Warranty. Chip: Apple M4, CPU: 10-core, GPU: 10-core, Memory: 24GB unified"),
            "Mac Mini M1 16GB/512GB vs M4 24GB/1TB — different generation and specs"
        ).SetDescription("Real pair: different Mac Mini generations, RAM, storage");

        // Rolex Submariner — Date vs No Date (126610LN vs 124060)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Rolex 126610LN Submariner Date 41mm 2025 Full Set Watch",
                "Brand new and unworn 2025 Rolex 126610LN Submariner Date 41mm Watch with original Rolex box",
                "Rolex 124060 Submariner No Date 41mm 2020 Full Set Watch",
                "Rolex 124060 Submariner No Date 41mm 2020 Full Set Watch with original Rolex box, outer sleeve"),
            "Rolex Submariner Date 126610LN vs No Date 124060 — different ref"
        ).SetDescription("Real pair: Submariner Date vs No Date, different references");

        // Nike Dunk Low — different style codes and colorways
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nike Dunk Low Maroon Yellow Size 11 DD1391-701",
                "Nike Dunk Low Maroon Yellow Size 11 DD1391-701",
                "Nike Dunk Low Reverse Goldenrod FZ4618-001 Pre-Owned Size US 11 UK 10 Euro 45",
                "Nike Dunk Low Reverse Goldenrod, featuring a striking colorway. Style code FZ4618-001"),
            "Nike Dunk Low DD1391-701 vs FZ4618-001 — different colorways"
        ).SetDescription("Real pair: same silhouette/size but different style codes");

        // TaylorMade Stealth 2 — HD vs non-HD, different handedness and flex
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Taylormade Stealth 2 HD Driver / 10.5 Degree / Stiff Flex HZRDUS Smoke Yellow 60",
                "Stealth 2 HD, Right-Handed, 10.5 degree loft, Stiff flex, Project X HZRDUS Smoke Yellow 60 shaft",
                "Taylormade Stealth 2 Driver / 10.5 Degree / Regular Flex Fujikura TR Red 5",
                "Stealth 2 (non-HD), Left-Handed, 10.5 degree loft, Regular flex, Fujikura Ventus TR Red 5 shaft"),
            "TaylorMade Stealth 2 HD RH Stiff vs Stealth 2 LH Regular — different model/hand/flex"
        ).SetDescription("Real pair: HD vs non-HD, different handedness and shaft flex");

        // TaylorMade Stealth 2 — HD vs non-HD same specs (HD is a different club with draw-biased design)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Taylormade Stealth 2 HD Driver 9 Degree",
                "TaylorMade Stealth 2 HD Driver, 9 degrees loft, stiff graphite shaft, right-handed",
                "Taylormade Stealth 2 Driver / 9 Degree / Stiff Flex Fujikura Ventus TR Red 5",
                "Stealth 2 Driver, Right-Handed, 9 degree loft, Stiff flex, Fujikura Ventus TR Red 5 shaft"),
            "TaylorMade Stealth 2 HD vs Stealth 2 9° Stiff RH — HD is different model"
        ).SetDescription("HD (High Draw) has different head geometry and CG vs standard Stealth 2");

        // Moissanite Ring — different cuts and settings (marquise 3-stone vs cushion halo)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "3 Ctw Marquise Cut Moissanite Three Stone Engagement Ring 14K White Gold Plated",
                "3 Ctw Marquise Cut Moissanite Three Stone Engagement Ring in 14K White Gold Plated, all ring sizes available",
                "3.20 TCW Cushion Cut Moissanite Halo Engagement Ring In 14K White Gold Plated",
                "3.20 TCW Cushion Cut Moissanite Halo Engagement Ring In 14K White Gold Plated, all ring sizes available"),
            "Moissanite 3ct Marquise 3-stone vs 3.2ct Cushion Halo — different cut/setting"
        ).SetDescription("Real pair: different cut shapes and ring settings");
    }

    // --- V7 true-positive regression tests (real training pairs) ---
    // Pairs that should be classified as comparable despite cosmetic differences.

    [TestCaseSource(nameof(V7TruePositivePairs))]
    public async Task Should_accept_comparable_pairs_from_training_data(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        Assert.That(result.IsComparable, Is.True, $"Expected comparable: {description}");

        TestContext.WriteLine($"[TRUE-POS] {description}: confidence={result.Confidence:F4}");
    }

    private static IEnumerable<TestCaseData> V7TruePositivePairs()
    {
        // Cartier Love — same size 17, different gold colors (pink vs yellow)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Cartier Love Small Bracelet 18K Pink Gold 17cm",
                "Cartier Love Small Bracelet 18K Pink Gold size 17",
                "Cartier Love Bracelet, Small Model, 18ct Yellow Gold, Size 17,",
                "Classic Cartier Love Bracelet Small, Size 17 18ct Yellow Gold. Used with some signs of wear"),
            "Cartier Love Small 17 Pink Gold vs Yellow Gold — color variant"
        ).SetDescription("Real pair: same Cartier model/size, different gold color");

        // Omega Seamaster Aqua Terra — same ref family, different dial colors
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "OMEGA Seamaster Aqua Terra Green - 220.10.41.21.10.001",
                "Omega Seamaster Aqua Terra with rich green teak-pattern dial and polished steel case, 41mm",
                "OMEGA Seamaster Aqua Terra Black Watch 150M 41mm 220.10.41.21.01.001 RRP \u00a35,900!",
                "OMEGA Seamaster Aqua Terra 41mm in black. Same reference family 220.10.41.21, 150M water resistance"),
            "Omega Aqua Terra 41mm Green vs Black — dial color variant"
        ).SetDescription("Real pair: same AT ref family, different dial colors");

        // Pandora Angel Wings — same charm, different colors (pink vs blue)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Pink Angel Wings Baby Memorial Bead Charm S925 Genuine Sterling Silver",
                "Sterling Silver 925 Angel Wings Baby Memorial bead charm, high quality, hallmarked",
                "Blue Angel Wings Baby Memorial Bead Charm S925 Genuine Sterling Silver",
                "Sterling Silver 925 Angel Wings Baby Memorial bead charm, high quality, hallmarked"),
            "Pandora Angel Wings S925 Pink vs Blue — color variant"
        ).SetDescription("Real pair: same charm model, different bead color");

        // Mac Mini — same M4/16GB/256GB spec, different condition
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple Mac Mini 2024 M4 Chip 10Core CPU 10-Core GPU 16GB RAM 256GB SSD Brand New",
                "Brand New and Sealed under 2 years warranty",
                "Apple Mac Mini M4 16GB RAM, 256GB SSD, Boxed - Great Condition",
                "Bought in April 2025 and has received minimal use. The unit itself looks great"),
            "Mac Mini M4 16GB/256GB New vs Used — same spec, different condition"
        ).SetDescription("Real pair: identical Mac Mini specs, new vs used");

        // Rolex Submariner Date 126610LN — same ref, different years
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Rolex 126610LN Submariner Date 41mm 2025 Full Set Watch",
                "Brand new and unworn 2025 Rolex 126610LN Submariner Date 41mm Watch with original Rolex box",
                "Rolex Submariner Date 126610LN (2023) +Box & Papers",
                "Rolex Submariner-Date, model reference M126610LN-0001. 41mm diameter, cal.3235 movement"),
            "Rolex Submariner 126610LN 2025 vs 2023 — same ref, different year"
        ).SetDescription("Real pair: same reference, different production years");

        // Nike Dunk Low — same colorway and size
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nike dunks Low Blue size 6",
                "Worn a handful of times, you can tell by the wear on the sole and condition of the trainer",
                "Nike Dunk Low Blue Coast UK6",
                "Great Condition and barely worn"),
            "Nike Dunk Low Blue Size 6 — same colorway/size, different condition"
        ).SetDescription("Real pair: same Nike Dunk Low color and size");

        // Moissanite Ring — same 1CT round, same size 6.5
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "1CT Round Brilliant Affordable Moissanite Engagement Ring - Size 6.5",
                "1CT Moissanite Engagement Ring, Size 6.5, 925 sterling silver, featuring a round white/colorless main stone",
                "Moissanite Engagement Ring 1 CT Round Cut 925 Size 6.5 With Cert And Pouch!",
                "1 CT round cut white Moissanite engagement ring set in 925 sterling silver, size 6.5, includes certificate and pouch"),
            "Moissanite 1CT Round 6.5 925 — same spec, different seller"
        ).SetDescription("Real pair: identical moissanite ring specs");

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

    // --- V8 weak-category hard-negative regression tests ---
    // These target categories where v8 test F1 < 0.80, using realistic
    // eBay listing pairs that should NOT be comparable.

    [TestCaseSource(nameof(V8WeakCategoryHardNegativePairs))]
    public async Task Should_reject_v8_weak_category_hard_negatives(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        Assert.That(result.IsComparable, Is.False, $"Expected not comparable: {description}");

        TestContext.WriteLine($"[V8-HARD-NEG] {description}: confidence={result.Confidence:F4}");
    }

    private static IEnumerable<TestCaseData> V8WeakCategoryHardNegativePairs()
    {
        // Dyson V15 — V15 Detect vs V12 Detect Slim (different motor, suction, battery)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Dyson V15 Detect Absolute Cordless Vacuum Cleaner",
                "Dyson V15 Detect Absolute with laser dust detection, piezo sensor, LCD screen, up to 60 min runtime",
                "Dyson V12 Detect Slim Absolute Cordless Vacuum Cleaner",
                "Dyson V12 Detect Slim with laser dust detection, lightweight design, 45 min runtime"),
            "Dyson V15 vs V12 — different model (bigger motor vs slim)"
        ).SetDescription("Different Dyson models despite similar Detect branding");

        // Dyson V15 — V15 Detect vs V15 Detect Complete (different bundle — this tests attachment-only diffs)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Dyson V15 Detect Absolute Cordless Vacuum 2023",
                "Dyson V15 Detect Absolute cordless vacuum, includes motorbar cleaner head, anti-tangle hair screw tool",
                "Dyson V15 Detect Complete Extra Cordless Vacuum 2024",
                "Dyson V15 Detect Complete Extra with submarine wet roller head, extra tools and dock"),
            "Dyson V15 Detect Absolute vs Complete Extra — different SKU/bundle"
        ).SetDescription("Same motor but different product SKU with different accessories");

        // Nintendo Switch OLED — OLED vs V2 (different hardware generation)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nintendo Switch OLED Model White Set",
                "Nintendo Switch OLED model with 7-inch OLED screen, wide adjustable stand, enhanced audio, 64GB storage",
                "Nintendo Switch V2 Console Neon Blue/Red HAC-001(-01)",
                "Nintendo Switch V2 with improved battery life, 6.2-inch LCD screen, 32GB storage, neon Joy-Cons"),
            "Switch OLED vs V2 — different hardware generation"
        ).SetDescription("OLED has bigger/better screen, more storage, different stand");

        // Nintendo Switch OLED — OLED model vs Switch OLED with different bundle (Mario Kart)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nintendo Switch OLED Model White Joy-Con Console",
                "Brand new Nintendo Switch OLED console with white Joy-Con controllers, dock, HDMI cable",
                "Nintendo Switch OLED Mario Kart 8 Deluxe Bundle Neon Red/Blue",
                "Nintendo Switch OLED Mario Kart 8 Deluxe Bundle with neon red/blue Joy-Cons, includes game download code"),
            "Switch OLED White standalone vs Mario Kart Bundle Neon — different SKU/color"
        ).SetDescription("Different bundles and Joy-Con colors");

        // Specialized Tarmac — SL6 vs SL7 (different generation)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Specialized S-Works Tarmac SL7 Dura-Ace Di2 Size 56",
                "Specialized S-Works Tarmac SL7 with Shimano Dura-Ace Di2, carbon wheels, size 56cm, 2022 model",
                "Specialized Tarmac SL6 Expert Ultegra Di2 Size 54",
                "Specialized Tarmac SL6 Expert with Shimano Ultegra Di2, DT Swiss wheels, size 54cm, 2020 model"),
            "Tarmac SL7 S-Works 56 vs SL6 Expert 54 — different gen/tier/size"
        ).SetDescription("Different generation, build tier, frame size");

        // Trek Domane — SL5 vs SLR7 (different tier entirely)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Trek Domane SL 5 Road Bike 2023 Size 56cm Shimano 105",
                "Trek Domane SL 5, 500 Series OCLV carbon frame, Shimano 105 R7000, size 56cm, disc brakes",
                "Trek Domane SLR 7 eTap Road Bike 2023 Size 52cm SRAM Force",
                "Trek Domane SLR 7 eTap, 800 Series OCLV carbon frame, SRAM Force eTap AXS, size 52cm, disc brakes"),
            "Trek Domane SL5 56cm 105 vs SLR7 52cm Force — different tier/size/groupset"
        ).SetDescription("Different carbon tier, groupset, frame size");

        // Vintage Levis 501 — 1960s Big E vs 1990s Made in USA (completely different era/value)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Vintage Levis 501 Big E Selvedge Jeans 1960s W32 L30 Redline",
                "Rare 1960s Levi's 501 Big E with selvedge denim, redline pocket, hidden rivet era, single stitch back pocket",
                "Vintage Levis 501 Made in USA 1993 W32 L32 Dark Wash",
                "Levi's 501 Made in USA from 1993, dark indigo wash, copper rivets, leather patch, button fly"),
            "Levis 501 1960s Big E vs 1993 MiUSA — vastly different era/value"
        ).SetDescription("Same waist but decades apart, different construction details");

        // Vintage Levis 501 — different waist sizes (W30 vs W36)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Vintage Levis 501 Jeans W30 L32 Light Wash Made in USA",
                "Vintage Levi's 501 button fly jeans, waist 30, length 32, light stonewash, made in USA",
                "Vintage Levis 501 Jeans W36 L30 Dark Wash Made in USA",
                "Vintage Levi's 501 button fly jeans, waist 36, length 30, dark indigo wash, made in USA"),
            "Levis 501 W30 light vs W36 dark — different size and wash"
        ).SetDescription("Different waist sizes and wash colors");

        // Yamaha P-125 vs P-145 (successor model, different features)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Yamaha P-125a Digital Piano Black 88 Key Weighted",
                "Yamaha P-125a 88-key weighted action digital piano in black, GHS keyboard, 24 voices, built-in speakers",
                "Yamaha P-145 Digital Piano Black 88 Key Weighted",
                "Yamaha P-145 88-key weighted action digital piano in black, GHS keyboard, improved sound engine, USB-C"),
            "Yamaha P-125a vs P-145 — different model (predecessor vs successor)"
        ).SetDescription("Different piano models despite very similar names");

        // Steelcase Leap V1 vs V2 (different mechanism and design)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Steelcase Leap V1 Office Chair Black Fabric Adjustable Arms",
                "Steelcase Leap V1 ergonomic office chair, black fabric, adjustable arms, lumbar support, original casters",
                "Steelcase Leap V2 Office Chair Black Fabric 4D Arms Headrest",
                "Steelcase Leap V2 ergonomic office chair, black fabric, 4D adjustable arms, lumbar and headrest, improved LiveBack technology"),
            "Steelcase Leap V1 vs V2 — different version (mechanism redesign)"
        ).SetDescription("Different chair versions with different back technology");
    }

    // --- V8 weak-category true-positive regression tests ---
    // Pairs from weak categories that SHOULD be classified as comparable.

    [TestCaseSource(nameof(V8WeakCategoryTruePositivePairs))]
    public async Task Should_accept_v8_weak_category_true_positives(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        Assert.That(result.IsComparable, Is.True, $"Expected comparable: {description}");

        TestContext.WriteLine($"[V8-TRUE-POS] {description}: confidence={result.Confidence:F4}");
    }

    private static IEnumerable<TestCaseData> V8WeakCategoryTruePositivePairs()
    {
        // Dyson V15 — same V15 Detect Absolute, different condition
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Dyson V15 Detect Absolute Cordless Vacuum - Brand New Sealed",
                "Brand new sealed Dyson V15 Detect Absolute vacuum with all accessories included",
                "Dyson V15 Detect Absolute Cordless Vacuum - Excellent Used",
                "Dyson V15 Detect Absolute in excellent condition, fully working, includes original charger and accessories"),
            "Dyson V15 Detect Absolute — same model, new vs used"
        ).SetDescription("Same Dyson V15 model, different condition");

        // Nintendo Switch OLED — same console, White vs Neon (same hardware)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nintendo Switch OLED Model White Set Console",
                "Nintendo Switch OLED model with white Joy-Con, 64GB, 7-inch OLED display, enhanced audio",
                "Nintendo Switch OLED Neon Red Blue Joy-Con Console",
                "Nintendo Switch OLED model with neon red/blue Joy-Con controllers, 64GB, 7-inch OLED screen"),
            "Switch OLED White vs Neon — same hardware, color variant only"
        ).SetDescription("Identical console hardware, different Joy-Con color");

        // Specialized Tarmac — same model/size, different year/color
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Specialized Tarmac SL7 Expert Size 54 Ultegra Di2 2023",
                "Specialized Tarmac SL7 Expert, Shimano Ultegra Di2, 54cm, Roval C38 wheels, midnight blue",
                "Specialized Tarmac SL7 Expert 54cm Ultegra Di2 2024 Black",
                "Specialized Tarmac SL7 Expert, Shimano Ultegra Di2, size 54, DT Swiss wheels, gloss black, 2024"),
            "Tarmac SL7 Expert 54 Di2 2023 vs 2024 — same build, different year"
        ).SetDescription("Same model/size/groupset, different color year");

        // Trek Domane — same SL5 and size, different color
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Trek Domane SL 5 2023 Road Bike Size 54cm Red",
                "Trek Domane SL 5, Shimano 105, size 54cm, carbon frame, disc brakes, crimson red",
                "Trek Domane SL 5 2023 54cm Road Bike Blue",
                "Trek Domane SL5 road bike, Shimano 105 groupset, 54cm frame, carbon, disc brakes, arctic blue"),
            "Trek Domane SL5 54cm Red vs Blue — color variant only"
        ).SetDescription("Same Trek Domane model/size, different paint");

        // Vintage Levis 501 — same era, same size, different condition
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Vintage Levis 501 Jeans W33 L32 Made in USA 1990s Medium Wash",
                "Authentic vintage Levi's 501 from the 1990s, medium stonewash, made in USA, button fly, good vintage condition",
                "Vintage Levis 501 W33 L32 Made in USA 90s Stonewash",
                "Vintage Levi's 501 jeans, 1990s, W33 L32, stonewash, USA made, some natural fading and distress"),
            "Levis 501 W33 90s MiUSA — same size/era, different seller descriptions"
        ).SetDescription("Same vintage 501 specs, typical seller variation");

        // Yamaha P-125 — same model, black vs white (color variant)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Yamaha P-125a Digital Piano 88 Key Weighted Black",
                "Yamaha P-125a portable digital piano, 88 weighted keys, GHS action, 24 voices, black finish",
                "Yamaha P-125a Digital Piano 88 Key Weighted White",
                "Yamaha P-125a digital piano in white, 88 GHS weighted keys, built-in speakers, USB connectivity"),
            "Yamaha P-125a Black vs White — color variant only"
        ).SetDescription("Same piano model, different color finish");

        // Steelcase Leap V2 — same version, different fabric color
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Steelcase Leap V2 Ergonomic Office Chair Black Fabric",
                "Steelcase Leap V2 in black fabric, fully adjustable arms, lumbar support, LiveBack technology",
                "Steelcase Leap V2 Office Chair Grey Fabric Adjustable",
                "Steelcase Leap V2 ergonomic chair, grey fabric upholstery, adjustable arms, tilt limiter, excellent condition"),
            "Steelcase Leap V2 Black vs Grey — fabric color variant"
        ).SetDescription("Same chair version, different upholstery color");
    }

    // --- Spot-check real database pairs against v8 ---

    [Test]
    public async Task Spot_check_real_listing_pairs()
    {
        var pairs = new (ClassifyPairRequest Pair, string Label)[]
        {
            // iPhone 15 Pro Max 512GB Black vs 512GB Black (same phone, same storage, same color)
            (new ClassifyPairRequest(
                "Apple iPhone 15 Pro Max A3106 512GB 8GB Smartphone Black Titanium Unlocked GOOD",
                "Fast shipping from UK warehouse. Refurbished iPhone 15 Pro Max 512GB Black Titanium.",
                "Apple iPhone 15 Pro Max 512GB Unlocked Black Titanium - UK Model - GOOD B+",
                "iPhone 15 Pro Max 512GB Black Titanium, UK model, unlocked, good condition."),
             "iPhone 15 Pro Max 512GB Black vs 512GB Black — SHOULD match"),

            // iPhone 15 Pro Max 512GB vs 256GB Blue (different storage AND color)
            (new ClassifyPairRequest(
                "Apple iPhone 15 Pro Max A3106 512GB 8GB Smartphone Black Titanium Unlocked GOOD",
                "Fast shipping from UK warehouse. Refurbished iPhone 15 Pro Max 512GB Black Titanium.",
                "Apple iPhone 15 Pro Max 256GB Blue - Unlocked - 100% Battery Health - Excellent",
                "iPhone 15 Pro Max 256GB Blue, unlocked, 100% battery health."),
             "iPhone 15 Pro Max 512GB Black vs 256GB Blue — SHOULD NOT match"),

            // Canada Goose Langford XL vs XXL (different size — should match for pricing)
            (new ClassifyPairRequest(
                "Canada Goose Langford Parka Mens Size XL with Fur Hood",
                "Canada Goose Langford Parka for men in size XL with fur hood, polyester material.",
                "BLACK CANADA GOOSE LANGFORD XXL PARKA JACKET",
                "Black Canada Goose Langford XXL Parka Jacket, Arctic tech material by Canada Goose."),
             "Canada Goose Langford XL vs XXL — size variant"),

            // Harry Potter Half-Blood Prince 1st edition — same book
            (new ClassifyPairRequest(
                "First Edition Harry Potter and the Half-Blood Prince by J. K. Rowling 2005",
                "Harry Potter and the half blood prince 2005 Hardback",
                "1st First Edition Harry Potter And The Half-Blood Prince July 2005 Excellent",
                "First Edition of Harry Potter and the Half-Blood Prince by J.K. Rowling, Scholastic 2005, hardcover."),
             "Harry Potter HBP 1st ed 2005 — SHOULD match"),

            // Yamaha P45B — same piano, different condition
            (new ClassifyPairRequest(
                "Yamaha P45B Weighted Action Digital Piano, 88 Key - Black",
                "Bought and used maybe 1/2 times! Perfect condition. Will come with stand and seat.",
                "Yamaha P45B Weighted Action Digital Piano, 88 Key - Black",
                "Excellent used condition. Works perfectly. Complete package with stand, seat, foot pedal."),
             "Yamaha P45B Black — SHOULD match (same piano)"),

            // DeWalt DCD805 vs DCD796 (different model numbers)
            (new ClassifyPairRequest(
                "DEWALT DCD805 20V Cordless Combi Hammer, Electric Brushless Drill (No Battery).",
                "DeWalt DCD805 20V cordless combi hammer drill, brushless, body only, no battery.",
                "Dewalt DCD796N 18v XR Brushless 2-Speed Combi Drill - Body Only",
                "DeWalt DCD796N 18V XR brushless 2-speed combi drill, body only."),
             "DeWalt DCD805 vs DCD796 — SHOULD NOT match (different models)"),

            // DeWalt DCD805 vs DCD805 (same model — 18V and 20V MAX are same voltage)
            (new ClassifyPairRequest(
                "DEWALT DCD805 20V Cordless Combi Hammer, Electric Brushless Drill (No Battery).",
                "DeWalt DCD805 20V cordless combi hammer drill, brushless, body only, no battery.",
                "DEWALT DCD805 Cordless Combi Hammer Drill Brushless 18V XR Power Tool Driver",
                "DeWalt DCD805 cordless combi hammer drill, brushless, 18V XR, body only."),
             "DeWalt DCD805 vs DCD805 — SHOULD match (same model, 18V=20V MAX)"),

            // WRONG: Rolex Submariner Hulk matched to Omega Seamaster (different brand!)
            (new ClassifyPairRequest(
                "Rolex Submariner Date 116610LV Hulk 40mm Unworn 2015 Complete Set",
                "Rolex Submariner Date 116610LV Hulk, green dial and bezel, 40mm, unworn, 2015, complete set.",
                "OMEGA Seamaster 120M Chronometer Automatic Mens Watch 2501.80 Polished",
                "Omega Seamaster 120M Chronometer automatic men's watch, ref 2501.80, polished."),
             "Rolex Sub Hulk vs Omega Seamaster — SHOULD NOT match (different brand!)"),

            // WRONG: Rolex Bamford vs Omega Seamaster 600
            (new ClassifyPairRequest(
                "Rolex x Bamford Submariner Date | 2010 | DLC Black | Box & Papers | Ref. 16610",
                "Rolex Bamford Submariner Date, DLC black coating, 2010, box and papers.",
                "Omega Seamaster 600 | 34mm | Gold Plated | Manual Wind",
                "Omega Seamaster 600, 34mm, gold plated, manual wind."),
             "Rolex Bamford Sub vs Omega Seamaster 600 — SHOULD NOT match"),

            // WRONG: Jordan 1 High OG Shattered Backboard vs Union LA Chicago Shadow
            (new ClassifyPairRequest(
                "Jordan 1 Retro High OG, Shattered Backboard - UK9 / US10, DZ5485-008 Authentic",
                "Air Jordan 1 Retro High OG Shattered Backboard, UK9 / US10, style DZ5485-008, authentic.",
                "Air Jordan 1 Union LA Chicago Shadow UK 9.5 Deadstock HV8563-600 Authentic",
                "Air Jordan 1 Union LA Chicago Shadow, UK 9.5, deadstock, style HV8563-600, authentic."),
             "AJ1 Shattered Backboard vs Union LA — SHOULD NOT match (different colorways/collabs)"),

            // Jordan 1 Mid different colorways same size
            (new ClassifyPairRequest(
                "Nike Air Jordan 1 Mid SE 'Elephant Toe' Black White Mens DM1200-016 SIZE 12",
                "Air Jordan 1 Mid SE Elephant Toe, black white, DM1200-016, size 12.",
                "Nike Air Jordans 1 Mid Mens Sneakers Sz 12 White Leather Basketball Shoes",
                "Nike Air Jordan 1 Mid, size 12, white leather, basketball shoes."),
             "AJ1 Mid Elephant Toe vs White — different colorways"),
        };

        foreach (var (pair, label) in pairs)
        {
            var results = await _classifier.Classify([pair]);
            var r = results[0];
            TestContext.WriteLine($"[SPOT] {label}: isComparable={r.IsComparable}, confidence={r.Confidence:F4}");
        }
    }

    // --- Seller boilerplate test (verifies description truncation prevents false matches) ---

    [Test]
    public async Task Should_reject_cross_category_pairs_with_shared_seller_template()
    {
        // Real eBay pairs from same reseller — descriptions start with product info
        // then devolve into identical "Huge Discounts Quality Products..." template.
        // Without truncation, model sees 90% identical text and says comparable.
        var pairs = new (ClassifyPairRequest Pair, string Label)[]
        {
            (new ClassifyPairRequest(
                "KitchenAid Artisan Stand Mixer 5.6L 11 Speeds Bowl Lift Style Steel Bowl C Grade",
                "KitchenAid Artisan Stand Mixer 5.6L 11 Speeds Bowl Lift Style Steel Bowl C Grade Product Information KitchenAid Artisan Stand Mixer 5.6L 11 Speeds Bowl Lift Huge Discounts Quality Products Save Seller Contact us Menu Computing Laptops Desktops & Servers Accessories Monitors Printers & Scanners",
                "Bose QuietComfort Wireless Headphones Noise Cancellation 888507-0200 C Grade",
                "Bose QuietComfort Wireless Headphones Noise Cancellation 888507-0200 C Grade Product Information Bose QuietComfort Wireless Headphones Noise Cancellation Huge Discounts Quality Products Save Seller Contact us Menu Computing Laptops Desktops & Servers Accessories Monitors Printers & Scanners"),
             "KitchenAid Mixer vs Bose Headphones (same seller template)"),

            (new ClassifyPairRequest(
                "Sony Alpha A7 IV Full Frame Digital Camera Body",
                "Sony Alpha A7 IV Full Frame Digital Camera Body Product Information High resolution stills and 4K video Huge Discounts Quality Products Save Seller Contact us Menu Computing Laptops Desktops",
                "DJI Mini 5 Pro Drone Fly More Combo with DJI RC 2 Controller",
                "DJI Mini 5 Pro Drone Fly More Combo RC 2 Product Information Lightweight foldable drone with 4K camera Huge Discounts Quality Products Save Seller Contact us Menu Computing Laptops Desktops"),
             "Sony Camera vs DJI Drone (same seller template)"),
        };

        foreach (var (pair, label) in pairs)
        {
            var results = await _classifier.Classify([pair]);
            var r = results[0];
            Assert.That(r.IsComparable, Is.False, $"Cross-category pair should NOT be comparable: {label}");
            TestContext.WriteLine($"[BOILERPLATE] {label}: isComparable={r.IsComparable}, confidence={r.Confidence:F4}");
        }
    }

    // --- Health check ---

    [Test]
    public async Task Should_report_healthy_when_model_loaded()
    {
        var healthy = await _classifier.IsHealthy();
        Assert.That(healthy, Is.True);
    }

    // --- Ensemble calibration (end-to-end with real model) ---

    [Test]
    public async Task Should_produce_calibrated_confidence_with_ensemble()
    {
        var ensemble = new EnsembleConfig(
            LogitWeight: 2.4910f,
            SimilarityWeight: 0.4324f,
            Intercept: -2.6254f);

        var classifier = new VariantClassifier(
            _classifier,
            ensemble,
            Mock.Of<ILogger<VariantClassifier>>());

        var pair = new ClassifyPairRequest(
            "Dyson V15 Detect Absolute Cordless Vacuum",
            "Brand new Dyson V15 with laser dust detection",
            "Dyson V15 Detect Absolute Cordless Vacuum Cleaner",
            "Dyson V15 Detect Absolute - new in box",
            SimilarityScore: 0.92f);

        var results = await classifier.Classify([pair]);
        var result = results[0];

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.True);
            Assert.That(result.Confidence, Is.GreaterThan(0.95f),
                "High logit diff + high similarity should produce high calibrated confidence");
            Assert.That(result.LogitDiff, Is.Not.Null,
                "LogitDiff should be populated from model runner");
        });
    }
}
