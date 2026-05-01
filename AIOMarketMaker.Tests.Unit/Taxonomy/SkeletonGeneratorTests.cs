using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class SkeletonGeneratorTests
{
    [Test]
    public void ParseSkeletonResponse_Should_parse_valid_json()
    {
        var json = """
        {
            "axes": [
                {
                    "name": "storage",
                    "description": "Storage capacity",
                    "values": ["128gb", "256gb", "512gb"]
                },
                {
                    "name": "color",
                    "description": "Device color",
                    "values": ["black", "white"]
                }
            ]
        }
        """;

        var skeleton = OpenAiSkeletonGenerator.ParseSkeletonResponse(json);

        Assert.Multiple(() =>
        {
            Assert.That(skeleton.Axes.Count(), Is.EqualTo(2));
            var axes = skeleton.Axes.ToList();
            Assert.That(axes[0].Name, Is.EqualTo("storage"));
            Assert.That(axes[0].Values.Count(), Is.EqualTo(3));
            Assert.That(axes[1].Name, Is.EqualTo("color"));
        });
    }

    [Test]
    public void ParseSkeletonResponse_Should_handle_extraction_hints()
    {
        var json = """
        {
            "axes": [
                {
                    "name": "reference",
                    "description": "Model number",
                    "values": ["16610", "114060"],
                    "extraction_hints": "4-6 digit number"
                }
            ]
        }
        """;

        var skeleton = OpenAiSkeletonGenerator.ParseSkeletonResponse(json);
        var axis = skeleton.Axes.First();

        Assert.That(axis.Name, Is.EqualTo("reference"));
        Assert.That(axis.Values.Count(), Is.EqualTo(2));
    }
}
