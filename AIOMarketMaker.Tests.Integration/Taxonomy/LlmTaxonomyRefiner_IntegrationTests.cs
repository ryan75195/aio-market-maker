using AIOMarketMaker.Core.Services.Taxonomy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using OpenAI.Chat;

namespace AIOMarketMaker.Tests.Integration.Taxonomy;

[TestFixture]
[Category("Integration")]
[Explicit("Requires valid OpenAI API key")]
[NonParallelizable]
public class LlmTaxonomyRefiner_IntegrationTests
{
    private LlmTaxonomyRefiner _refiner = null!;

    [SetUp]
    public void Setup()
    {
        var configPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "AIOMarketMaker.Console", "local.settings.json");

        if (!File.Exists(configPath))
        {
            Assert.Ignore($"local.settings.json not found at {Path.GetFullPath(configPath)}");
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: false)
            .Build();

        var apiKey = configuration.GetValue<string>("OpenAi:ApiKey");
        if (string.IsNullOrEmpty(apiKey))
        {
            Assert.Ignore("OpenAi:ApiKey not found");
        }

        var model = configuration.GetValue<string>("OpenAi:TaxonomyModel") ?? "gpt-4o-mini";
        var client = new ChatClient(model, apiKey);
        var logger = new Mock<ILogger<LlmTaxonomyRefiner>>();
        _refiner = new LlmTaxonomyRefiner(client, logger.Object);
    }

    [Test]
    public async Task Should_label_ps5_axes_and_clean_mixed_values()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new List<AxisValue>
            {
                MakeAxisValue("digital"),
                MakeAxisValue("disk"),
                MakeAxisValue("portal")
            }),
            new("Axis 7", new List<AxisValue>
            {
                MakeAxisValue("bundle"),
                MakeAxisValue("condition")
            }),
            new("Axis 9", new List<AxisValue>
            {
                MakeAxisValue("black"),
                MakeAxisValue("white")
            })
        };

        var titles = new List<string>
        {
            "Sony PlayStation 5 PS5 Digital Edition Console - White",
            "PS5 Disc Edition Console Bundle with Extra Controller - Black",
            "PlayStation 5 PS5 Disk Edition 825GB White Console",
            "PS5 Digital Edition Slim Console White 1TB",
            "Sony PS5 Portal Remote Player - White",
            "PlayStation 5 Console Disc Version Black 1TB Slim",
            "PS5 Digital Edition Console Bundle 2 Controllers White",
            "Sony PlayStation 5 Disc Edition 825GB - Good Condition",
            "PS5 Console Digital Edition White - Used Like New",
            "PlayStation 5 PS5 Slim Disc Edition 1TB Black Console"
        };

        var result = await _refiner.Refine(axes, "PlayStation 5", titles);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Axes, Is.Not.Null);

            var refinedAxes = result.Axes.ToList();
            var dropAxes = result.DropAxes.ToList();

            // All refined axes should reference valid originals
            var validOriginals = new HashSet<string> { "Axis 0", "Axis 7", "Axis 9" };
            foreach (var axis in refinedAxes)
            {
                Assert.That(validOriginals, Does.Contain(axis.Original),
                    $"Axis original '{axis.Original}' not in input axes");
                Assert.That(axis.Name, Is.Not.Empty,
                    $"Axis '{axis.Original}' has empty name");
                Assert.That(axis.Importance, Is.InRange(1, 5),
                    $"Axis '{axis.Original}' importance {axis.Importance} out of range");
            }

            // Axis 7 mixes concepts (bundle + condition) - should be dropped or cleaned
            var axis7Refined = refinedAxes.FirstOrDefault(a => a.Original == "Axis 7");
            var axis7Dropped = dropAxes.Contains("Axis 7");
            Assert.That(axis7Refined == null || axis7Refined.RemoveValues.Any() || axis7Dropped, Is.True,
                "Axis 7 mixes 'bundle' and 'condition' - should be dropped or have values removed");

            // Axis 9 (black/white) should get a name containing "colo" (color/colour/colors)
            var axis9Refined = refinedAxes.FirstOrDefault(a => a.Original == "Axis 9");
            if (axis9Refined != null)
            {
                Assert.That(axis9Refined.Name.ToLowerInvariant(), Does.Contain("colo"),
                    $"Axis 9 named '{axis9Refined.Name}' - expected name containing 'colo' (color/colour)");
            }
        });

        LogResult(result);
    }

    [Test]
    public async Task Should_return_valid_schema_compliant_response()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new List<AxisValue>
            {
                MakeAxisValue("new"),
                MakeAxisValue("used"),
                MakeAxisValue("refurbished")
            })
        };

        var titles = new List<string>
        {
            "Apple iPhone 15 Pro Max 256GB - New Sealed",
            "iPhone 15 Pro Max 256GB Used Good Condition",
            "Apple iPhone 15 Pro Max 256GB Refurbished",
            "iPhone 15 Pro Max 256GB Brand New in Box",
            "Apple iPhone 15 Pro Max 256GB - Used Like New"
        };

        var result = await _refiner.Refine(axes, "iPhone 15 Pro Max 256GB", titles);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Axes, Is.Not.Null);
            Assert.That(result.MergeAxes, Is.Not.Null);
            Assert.That(result.DropAxes, Is.Not.Null);

            var refinedAxes = result.Axes.ToList();
            foreach (var axis in refinedAxes)
            {
                Assert.That(axis.Original, Is.Not.Null.And.Not.Empty);
                Assert.That(axis.Name, Is.Not.Null.And.Not.Empty);
                Assert.That(axis.Importance, Is.InRange(1, 5));
                Assert.That(axis.RemoveValues, Is.Not.Null);
                Assert.That(axis.AddValues, Is.Not.Null);
            }
        });

        LogResult(result);
    }

    [Test]
    public async Task Should_suggest_additional_values_for_coverage()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new List<AxisValue>
            {
                MakeAxisValue("digital"),
                MakeAxisValue("disc")
            })
        };

        var titles = new List<string>
        {
            "Sony PS5 Digital Edition Console White",
            "PlayStation 5 Disc Edition 825GB Console",
            "PS5 Slim Digital Edition 1TB Console White",
            "Sony PlayStation 5 Slim Disc Console 1TB",
            "PS5 Digital Edition Slim Console - New",
            "PlayStation 5 PS5 Slim 1TB Disc Edition",
            "Sony PS5 Slim Digital 1TB White Console",
            "PS5 Console Disc Edition 825GB Black"
        };

        var result = await _refiner.Refine(axes, "PlayStation 5", titles);

        var axis0 = result.Axes.FirstOrDefault(a => a.Original == "Axis 0");
        Assert.That(axis0, Is.Not.Null, "Axis 0 should survive refinement");

        // Titles mention "slim" which is missing from the axis values
        var addValues = axis0!.AddValues.ToList();
        TestContext.WriteLine($"Add values: [{string.Join(", ", addValues)}]");
        Assert.That(addValues, Is.Not.Empty,
            "Should suggest additional values visible in titles (e.g. 'slim')");

        LogResult(result);
    }

    [Test]
    public async Task Should_rank_edition_higher_than_color_for_ps5()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new List<AxisValue>
            {
                MakeAxisValue("digital"),
                MakeAxisValue("disc"),
                MakeAxisValue("slim")
            }),
            new("Axis 1", new List<AxisValue>
            {
                MakeAxisValue("black"),
                MakeAxisValue("white")
            })
        };

        var titles = new List<string>
        {
            "Sony PS5 Digital Edition Console White",
            "PlayStation 5 Disc Edition 825GB Console Black",
            "PS5 Slim Digital Edition 1TB Console White",
            "Sony PlayStation 5 Slim Disc Console 1TB Black",
            "PS5 Digital Edition Console - White",
            "PlayStation 5 PS5 Disc Edition Black 825GB"
        };

        var result = await _refiner.Refine(axes, "PlayStation 5", titles);

        var editionAxis = result.Axes.FirstOrDefault(a => a.Original == "Axis 0");
        var colorAxis = result.Axes.FirstOrDefault(a => a.Original == "Axis 1");

        Assert.Multiple(() =>
        {
            Assert.That(editionAxis, Is.Not.Null, "Edition axis (Axis 0) should survive");
            Assert.That(colorAxis, Is.Not.Null, "Color axis (Axis 1) should survive");
            Assert.That(editionAxis!.Importance, Is.GreaterThan(colorAxis!.Importance),
                $"Edition importance ({editionAxis.Importance}) should be greater than color importance ({colorAxis.Importance}) for PS5");
        });

        LogResult(result);
    }

    [Test]
    public async Task Should_handle_camera_specific_axes()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new List<AxisValue>
            {
                MakeAxisValue("body"),
                MakeAxisValue("stm"),
                MakeAxisValue("usm")
            }),
            new("Axis 1", new List<AxisValue>
            {
                MakeAxisValue("lens"),
                MakeAxisValue("shutter")
            })
        };

        var titles = new List<string>
        {
            "Canon EOS R6 Mark II Body Only - Excellent Condition",
            "Canon EOS R6 II + RF 24-105mm f/4L IS USM Lens Kit",
            "Canon R6 Mark II Mirrorless Camera Body Low Shutter Count",
            "Canon EOS R6 II with RF 50mm f/1.8 STM Lens Bundle",
            "Canon R6 Mark II Body Only 24.2MP 4K 40fps",
            "Canon EOS R6 II + RF 24-70mm f/2.8L IS USM Professional Kit",
            "Canon R6 II Mirrorless Camera Lens Kit RF 35mm STM",
            "Canon EOS R6 Mark II Body 2500 Shutter Count Mint"
        };

        var result = await _refiner.Refine(axes, "Canon EOS R6 Mark II", titles);

        var refinedAxes = result.Axes.ToList();
        Assert.Multiple(() =>
        {
            foreach (var axis in refinedAxes)
            {
                Assert.That(axis.Name.ToLowerInvariant(), Does.Not.StartWith("axis"),
                    $"Axis '{axis.Original}' should have a descriptive name, got '{axis.Name}'");
                Assert.That(axis.Name, Is.Not.Empty,
                    $"Axis '{axis.Original}' has empty name");
            }
        });

        LogResult(result);
    }

    [Test]
    public async Task Should_return_valid_refinement_for_simple_condition_axis()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new List<AxisValue>
            {
                MakeAxisValue("new"),
                MakeAxisValue("used"),
                MakeAxisValue("refurbished")
            })
        };

        var titles = new List<string>
        {
            "Nintendo Switch OLED Console White - New",
            "Nintendo Switch OLED 64GB Used Good Condition",
            "Switch OLED Console Refurbished - Like New",
            "Nintendo Switch OLED White Joy-Con New Sealed",
            "Nintendo Switch OLED 64GB - Used Fair Condition"
        };

        var result = await _refiner.Refine(axes, "Nintendo Switch OLED", titles);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);

            var axis0 = result.Axes.FirstOrDefault(a => a.Original == "Axis 0");
            Assert.That(axis0, Is.Not.Null, "Axis 0 should survive refinement");
            Assert.That(axis0!.Name, Is.Not.Empty, "Axis should get a meaningful name");
            Assert.That(axis0.Name.ToLowerInvariant(), Does.Not.StartWith("axis"),
                $"Name should be descriptive, not '{axis0.Name}'");
            Assert.That(axis0.Importance, Is.InRange(1, 5),
                $"Importance {axis0.Importance} out of range");
        });

        LogResult(result);
    }

    private static AxisValue MakeAxisValue(string label)
    {
        return new AxisValue(label, new[] { new Ngram(label, new[] { label }, 1) });
    }

    private static void LogResult(TaxonomyRefinement result)
    {
        TestContext.WriteLine("=== Refinement Result ===");
        foreach (var axis in result.Axes)
        {
            TestContext.WriteLine(
                $"  {axis.Original} -> \"{axis.Name}\" (importance={axis.Importance}) " +
                $"remove=[{string.Join(", ", axis.RemoveValues)}] " +
                $"add=[{string.Join(", ", axis.AddValues)}]");
        }

        foreach (var merge in result.MergeAxes)
        {
            TestContext.WriteLine($"  MERGE: keep={merge.Keep}, absorb={merge.Absorb}");
        }

        if (result.DropAxes.Any())
        {
            TestContext.WriteLine($"  DROP: [{string.Join(", ", result.DropAxes)}]");
        }
    }
}
