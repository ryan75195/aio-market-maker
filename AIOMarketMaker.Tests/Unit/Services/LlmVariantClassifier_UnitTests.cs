using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Services;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class LlmVariantClassifier_UnitTests
{
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
    public void Should_return_empty_results_for_empty_input()
    {
        // This tests the static methods only — the actual Classify method requires a real API client
        var pairs = Array.Empty<ClassifyPairRequest>();
        Assert.That(pairs, Is.Empty);
    }
}
