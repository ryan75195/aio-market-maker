using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class LlmVariantClassifier_UnitTests
{
    private static readonly ClassifyPairRequest SamplePair = new(
        "Dyson V15 Detect Absolute", "Cordless vacuum",
        "Dyson V15 Detect Absolute", "Cordless vacuum cleaner");

    private static LlmVariantClassifier CreateClassifier(
        IChatClient client, int maxRetries = 3)
    {
        var config = new LlmClassifierConfig(MaxConcurrency: 50, MaxRetries: maxRetries);
        var logger = Mock.Of<ILogger<LlmVariantClassifier>>();
        return new LlmVariantClassifier(client, config, logger);
    }

    private static Mock<IChatClient> MockChatClient(ClassifierResponse response)
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.CompleteChat<ClassifierResponse>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        return mock;
    }

    private static Mock<IChatClient> MockChatClientNull()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.CompleteChat<ClassifierResponse>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClassifierResponse?)null);
        return mock;
    }


    [Test]
    public void Should_build_prompt_with_titles_and_descriptions()
    {
        var pair = new ClassifyPairRequest(
            TitleA: "Dyson V15 Detect Absolute",
            DescriptionA: "Brand new cordless vacuum",
            TitleB: "Dyson V15 Detect Complete",
            DescriptionB: "Refurbished cordless vacuum");

        var prompt = LlmVariantClassifier.BuildUserPrompt(pair);

        Assert.Multiple(() =>
        {
            Assert.That(prompt, Does.Contain("Dyson V15 Detect Absolute"));
            Assert.That(prompt, Does.Contain("Dyson V15 Detect Complete"));
            Assert.That(prompt, Does.Contain("Brand new cordless vacuum"));
            Assert.That(prompt, Does.Contain("Refurbished cordless vacuum"));
        });
    }

    [Test]
    public void Should_truncate_long_descriptions_in_prompt()
    {
        var longDesc = new string('x', 5000);
        var pair = new ClassifyPairRequest("Title A", longDesc, "Title B", "Short desc");

        var prompt = LlmVariantClassifier.BuildUserPrompt(pair);

        Assert.That(prompt.Length, Is.LessThan(5000));
    }


    [Test]
    public async Task Should_return_empty_results_for_empty_input()
    {
        var classifier = CreateClassifier(Mock.Of<IChatClient>());

        var results = await classifier.Classify(Array.Empty<ClassifyPairRequest>());

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task Should_classify_pair_as_comparable_when_verdict_is_same()
    {
        var client = MockChatClient(new ClassifierResponse("Identical product", Verdict.Same));
        var classifier = CreateClassifier(client.Object);

        var results = await classifier.Classify(new[] { SamplePair });

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].IsComparable, Is.True);
            Assert.That(results[0].Confidence, Is.EqualTo(1.0f));
        });
    }

    [Test]
    public async Task Should_classify_pair_as_not_comparable_when_verdict_is_different()
    {
        var client = MockChatClient(new ClassifierResponse("Different storage", Verdict.Different));
        var classifier = CreateClassifier(client.Object);

        var results = await classifier.Classify(new[] { SamplePair });

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].IsComparable, Is.False);
            Assert.That(results[0].Confidence, Is.EqualTo(1.0f));
        });
    }

    [Test]
    public async Task Should_classify_multiple_pairs_with_mixed_results()
    {
        var callCount = 0;
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.CompleteChat<ClassifierResponse>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var index = Interlocked.Increment(ref callCount);
                return index % 2 == 1
                    ? new ClassifierResponse("Match", Verdict.Same)
                    : new ClassifierResponse("No match", Verdict.Different);
            });

        var classifier = CreateClassifier(mock.Object);
        var pairs = new[] { SamplePair, SamplePair, SamplePair };

        var results = await classifier.Classify(pairs);

        Assert.That(results, Has.Count.EqualTo(3));
        Assert.That(results.Any(r => r.IsComparable), Is.True);
        Assert.That(results.Any(r => !r.IsComparable), Is.True);
    }

    [Test]
    public async Task Should_retry_on_transient_failure_and_return_result()
    {
        var callCount = 0;
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.CompleteChat<ClassifierResponse>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    throw new HttpRequestException("Service unavailable");
                }
                return new ClassifierResponse("Match", Verdict.Same);
            });

        var classifier = CreateClassifier(mock.Object, maxRetries: 3);

        var results = await classifier.Classify(new[] { SamplePair });

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].IsComparable, Is.True);
        });
    }

    [Test]
    public async Task Should_return_fallback_when_all_retries_exhausted()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.CompleteChat<ClassifierResponse>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Service unavailable"));

        var classifier = CreateClassifier(mock.Object, maxRetries: 1);

        var results = await classifier.Classify(new[] { SamplePair });

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].IsComparable, Is.False);
            Assert.That(results[0].Confidence, Is.EqualTo(0.0f));
        });
    }

    [Test]
    public async Task Should_return_fallback_when_structured_response_is_null()
    {
        var client = MockChatClientNull();
        var classifier = CreateClassifier(client.Object);

        var results = await classifier.Classify(new[] { SamplePair });

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].IsComparable, Is.False);
            Assert.That(results[0].Confidence, Is.EqualTo(0.0f));
        });
    }

    [Test]
    public async Task Should_classify_as_not_comparable_with_low_confidence_when_uncertain()
    {
        var client = MockChatClient(new ClassifierResponse("Insufficient detail to determine", Verdict.Uncertain));
        var classifier = CreateClassifier(client.Object);

        var results = await classifier.Classify(new[] { SamplePair });

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].IsComparable, Is.False);
            Assert.That(results[0].Confidence, Is.LessThan(1.0f));
            Assert.That(results[0].Confidence, Is.GreaterThan(0.0f));
        });
    }

    [Test]
    public void Should_propagate_cancellation_not_swallow_it()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.CompleteChat<ClassifierResponse>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var classifier = CreateClassifier(mock.Object);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(
            () => classifier.Classify(new[] { SamplePair }, cts.Token));
    }
}
