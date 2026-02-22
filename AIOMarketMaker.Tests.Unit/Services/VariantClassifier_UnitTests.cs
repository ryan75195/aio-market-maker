using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class VariantClassifier_UnitTests
{
    private static readonly EnsembleConfig Ensemble = new(
        LogitWeight: 2.4910f,
        SimilarityWeight: 0.4324f,
        Intercept: -2.6254f);

    [Test]
    public async Task Should_apply_ensemble_calibration_when_similarity_score_present()
    {
        // logitDiff=5.632, similarity=0.92
        // score = 2.4910*5.632 + 0.4324*0.92 + (-2.6254) = 14.0293 + 0.3978 - 2.6254 = 11.8017
        // sigmoid(11.8017) ≈ 0.99999
        var runner = CreateMockRunner(logitDiff: 5.632f);
        var classifier = new VariantClassifier(runner, Ensemble, Mock.Of<ILogger<VariantClassifier>>());

        var pairs = new[]
        {
            new ClassifyPairRequest("A", "descA", "B", "descB", SimilarityScore: 0.92f)
        };

        var results = await classifier.Classify(pairs);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].IsComparable, Is.True);
            Assert.That(results[0].Confidence, Is.EqualTo(0.99999f).Within(0.001f));
            Assert.That(results[0].LogitDiff, Is.EqualTo(5.632f));
        });
    }

    [Test]
    public async Task Should_fall_back_to_raw_result_when_similarity_score_missing()
    {
        var runner = CreateMockRunner(logitDiff: 5.632f);
        var classifier = new VariantClassifier(runner, Ensemble, Mock.Of<ILogger<VariantClassifier>>());

        var pairs = new[]
        {
            new ClassifyPairRequest("A", "descA", "B", "descB", SimilarityScore: null)
        };

        var results = await classifier.Classify(pairs);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            // Should be raw softmax result, not ensemble-calibrated
            var probs = VariantModelRunner.Softmax([-5.632f / 2, 5.632f / 2]);
            Assert.That(results[0].Confidence, Is.EqualTo(probs.Max()).Within(0.0001f));
            Assert.That(results[0].IsComparable, Is.True);
        });
    }

    [Test]
    public async Task Should_classify_as_not_comparable_when_ensemble_score_below_half()
    {
        // logitDiff=0.5, similarity=0.72
        // score = 2.4910*0.5 + 0.4324*0.72 + (-2.6254) = 1.2455 + 0.3113 - 2.6254 = -1.0686
        // sigmoid(-1.0686) ≈ 0.256
        var runner = CreateMockRunner(logitDiff: 0.5f);
        var classifier = new VariantClassifier(runner, Ensemble, Mock.Of<ILogger<VariantClassifier>>());

        var pairs = new[]
        {
            new ClassifyPairRequest("A", "descA", "B", "descB", SimilarityScore: 0.72f)
        };

        var results = await classifier.Classify(pairs);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].IsComparable, Is.False);
            Assert.That(results[0].Confidence, Is.EqualTo(0.256f).Within(0.02f));
        });
    }

    [Test]
    public async Task Should_handle_batch_with_mixed_similarity_scores()
    {
        // Pair 0: has similarity → ensemble calibration
        // logitDiff=1.5, similarity=0.85
        // score = 2.4910*1.5 + 0.4324*0.85 + (-2.6254) = 3.7365 + 0.3675 - 2.6254 = 1.4786
        // sigmoid(1.4786) ≈ 0.814

        // Pair 1: no similarity → raw passthrough
        var runner = CreateMockRunner(logitDiff: 1.5f, count: 2);
        var classifier = new VariantClassifier(runner, Ensemble, Mock.Of<ILogger<VariantClassifier>>());

        var pairs = new[]
        {
            new ClassifyPairRequest("A1", "d1", "B1", "d1", SimilarityScore: 0.85f),
            new ClassifyPairRequest("A2", "d2", "B2", "d2", SimilarityScore: null)
        };

        var results = await classifier.Classify(pairs);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(2));

            // First: ensemble-calibrated
            Assert.That(results[0].IsComparable, Is.True);
            Assert.That(results[0].Confidence, Is.EqualTo(0.814f).Within(0.02f));

            // Second: raw softmax passthrough
            var probs = VariantModelRunner.Softmax([-1.5f / 2, 1.5f / 2]);
            Assert.That(results[1].Confidence, Is.EqualTo(probs.Max()).Within(0.0001f));
        });
    }

    [Test]
    public async Task Should_return_empty_for_empty_input()
    {
        var runner = CreateMockRunner(logitDiff: 1.0f);
        var classifier = new VariantClassifier(runner, Ensemble, Mock.Of<ILogger<VariantClassifier>>());

        var results = await classifier.Classify(Array.Empty<ClassifyPairRequest>());

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task Should_delegate_health_check_to_model_runner()
    {
        var mock = new Mock<IVariantModelRunner>();
        mock.Setup(m => m.IsHealthy(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var classifier = new VariantClassifier(mock.Object, Ensemble, Mock.Of<ILogger<VariantClassifier>>());

        var healthy = await classifier.IsHealthy();

        Assert.Multiple(() =>
        {
            Assert.That(healthy, Is.True);
            mock.Verify(m => m.IsHealthy(It.IsAny<CancellationToken>()), Times.Once);
        });
    }

    private static IVariantModelRunner CreateMockRunner(float logitDiff, int count = 1)
    {
        // Compute softmax from symmetric logits for the raw result
        var probs = VariantModelRunner.Softmax([-logitDiff / 2, logitDiff / 2]);
        var isComparable = probs[1] > probs[0];
        var confidence = probs.Max();
        var result = new PairResult(isComparable, confidence, LogitDiff: logitDiff);

        var mock = new Mock<IVariantModelRunner>();
        mock.Setup(m => m.Classify(It.IsAny<IEnumerable<ClassifyPairRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Repeat(result, count).ToList());
        mock.Setup(m => m.IsHealthy(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return mock.Object;
    }
}
