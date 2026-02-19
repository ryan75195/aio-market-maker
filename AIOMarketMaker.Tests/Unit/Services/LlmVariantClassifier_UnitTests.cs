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

    private static Mock<IChatClient> MockChatClient(string response)
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.CompleteChat(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        return mock;
    }


    [Test]
    public void Should_parse_comparable_json_response()
    {
        var result = LlmVariantClassifier.ParseResponse(
            """{"verdict": "same", "reason": "Both are Dyson V15 Detect Absolute cordless vacuums"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.True);
            Assert.That(result.Confidence, Is.EqualTo(1.0f));
        });
    }

    [Test]
    public void Should_parse_not_comparable_json_response()
    {
        var result = LlmVariantClassifier.ParseResponse(
            """{"verdict": "different", "reason": "Different storage: 128GB vs 256GB"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.False);
            Assert.That(result.Confidence, Is.EqualTo(1.0f));
        });
    }

    [Test]
    public void Should_return_low_confidence_for_unparseable_response()
    {
        var result = LlmVariantClassifier.ParseResponse("I think they are the same product");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.False);
            Assert.That(result.Confidence, Is.EqualTo(0.0f));
        });
    }

    [Test]
    public void Should_handle_json_wrapped_in_markdown_code_block()
    {
        var result = LlmVariantClassifier.ParseResponse(
            """
            ```json
            {"verdict": "same", "reason": "Identical product"}
            ```
            """);

        Assert.That(result.IsComparable, Is.True);
    }

    [TestCase("same", true)]
    [TestCase("SAME", true)]
    [TestCase("Same", true)]
    [TestCase("different", false)]
    [TestCase("DIFFERENT", false)]
    [TestCase("Different", false)]
    public void Should_handle_case_insensitive_verdict(string verdict, bool expectedComparable)
    {
        var result = LlmVariantClassifier.ParseResponse(
            $$"""{"verdict": "{{verdict}}", "reason": "test"}""");

        Assert.That(result.IsComparable, Is.EqualTo(expectedComparable));
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
    public async Task Should_classify_pair_as_comparable_when_llm_returns_same()
    {
        var client = MockChatClient("""{"verdict": "same", "reason": "Identical product"}""");
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
    public async Task Should_classify_pair_as_not_comparable_when_llm_returns_different()
    {
        var client = MockChatClient("""{"verdict": "different", "reason": "Different storage"}""");
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
        mock.Setup(c => c.CompleteChat(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var index = Interlocked.Increment(ref callCount);
                return index % 2 == 1
                    ? """{"verdict": "same", "reason": "Match"}"""
                    : """{"verdict": "different", "reason": "No match"}""";
            });

        var classifier = CreateClassifier(mock.Object);
        var pairs = new[] { SamplePair, SamplePair, SamplePair };

        var results = await classifier.Classify(pairs);

        Assert.That(results, Has.Count.EqualTo(3));
        // At least one same and one different (order depends on task scheduling)
        Assert.That(results.Any(r => r.IsComparable), Is.True);
        Assert.That(results.Any(r => !r.IsComparable), Is.True);
    }

    [Test]
    public async Task Should_retry_on_transient_failure_and_return_result()
    {
        var callCount = 0;
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.CompleteChat(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    throw new HttpRequestException("Service unavailable");
                }
                return """{"verdict": "same", "reason": "Match"}""";
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
        mock.Setup(c => c.CompleteChat(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
    public async Task Should_return_fallback_when_llm_returns_unparseable_response()
    {
        var client = MockChatClient("I think they are the same product");
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
    public void Should_propagate_cancellation_not_swallow_it()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.CompleteChat(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var classifier = CreateClassifier(mock.Object);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(
            () => classifier.Classify(new[] { SamplePair }, cts.Token));
    }

}
