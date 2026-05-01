using AIOMarketMaker.Core.Services.Taxonomy;
using AIOMarketMaker.ML.Services;

namespace AIOMarketMaker.Tests.Unit.ML;

[TestFixture]
[Category("Unit")]
public class ExtractionModelRunnerTests
{
    [Test]
    public void FormatPrompt_Should_include_all_axes_and_title()
    {
        var skeleton = new ExtractionSkeleton(new[]
        {
            new SkeletonAxis("storage", "Storage capacity", new[] { "128gb", "256gb", "512gb", "1tb" }),
            new SkeletonAxis("color", "Device color", new[] { "black", "white", "blue" }),
        });

        var prompt = ExtractionModelRunner.FormatPrompt("iPhone 15 Pro Max 256GB Black Unlocked", skeleton);

        Assert.Multiple(() =>
        {
            Assert.That(prompt, Does.Contain("storage"));
            Assert.That(prompt, Does.Contain("128gb, 256gb, 512gb, 1tb"));
            Assert.That(prompt, Does.Contain("color"));
            Assert.That(prompt, Does.Contain("black, white, blue"));
            Assert.That(prompt, Does.Contain("iPhone 15 Pro Max 256GB Black Unlocked"));
        });
    }

    [Test]
    public void ParseExtraction_Should_return_dict_with_values_and_nulls()
    {
        var json = """{"storage": "256gb", "color": "black", "carrier": null}""";
        var result = ExtractionModelRunner.ParseExtraction(json);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!["storage"], Is.EqualTo("256gb"));
            Assert.That(result["color"], Is.EqualTo("black"));
            Assert.That(result["carrier"], Is.Null);
        });
    }

    [Test]
    public void ParseExtraction_Should_return_null_for_invalid_json()
    {
        var result = ExtractionModelRunner.ParseExtraction("not json at all");
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ParseExtraction_Should_return_null_when_all_values_null()
    {
        var json = """{"storage": null, "color": null}""";
        var result = ExtractionModelRunner.ParseExtraction(json);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ParseExtraction_Should_handle_json_with_markdown_fencing()
    {
        var json = "```json\n{\"storage\": \"256gb\"}\n```";
        var result = ExtractionModelRunner.ParseExtraction(json);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!["storage"], Is.EqualTo("256gb"));
    }

    [Test]
    public void FormatChatPrompt_Should_use_chatml_format()
    {
        var skeleton = new ExtractionSkeleton(new[]
        {
            new SkeletonAxis("storage", "Storage capacity", new[] { "128gb", "256gb" }),
        });

        var chatPrompt = ExtractionModelRunner.FormatChatPrompt(
            "iPhone 256GB", skeleton);

        Assert.Multiple(() =>
        {
            Assert.That(chatPrompt, Does.Contain("<|im_start|>system"));
            Assert.That(chatPrompt, Does.Contain("<|im_start|>user"));
            Assert.That(chatPrompt, Does.Contain("<|im_start|>assistant"));
            Assert.That(chatPrompt, Does.EndWith("<|im_start|>assistant\n"));
        });
    }
}
