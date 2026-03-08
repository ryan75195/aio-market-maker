# LLM Taxonomy Refinement Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enhance the statistical taxonomy pipeline with a single LLM call that refines axes (labels, cleans values, merges, ranks importance, suggests missing values), then feeds the refined schema back into the statistical pipeline for re-assignment.

**Architecture:** Statistical pipeline runs first (unchanged), produces raw axes. A new `ITaxonomyRefiner` sends axes + product name + sample titles to gpt-5-mini via structured output. The LLM returns a delta (add/remove values, merge/drop axes, name, importance). A pure `ApplyRefinement` method applies deltas. The statistical pipeline then re-assigns all listings with the refined axes.

**Tech Stack:** OpenAI SDK (`OpenAI.Chat.ChatClient`), JSON schema structured output (`ChatResponseFormat.CreateJsonSchemaFormat`), .NET 8, NUnit, TDD

---

### Task 1: Add Importance column to TaxonomyAxes

**Files:**
- Create: `AIOMarketMaker.Core/Data/Migrations/SqlServer/049_AddTaxonomyAxisImportance.sql`
- Modify: `AIOMarketMaker.Core/Data/Models/TaxonomyAxis.cs`
- Modify: `AIOMarketMaker.Core/Data/EtlDbContext.cs` (if needed for column config)

**Step 1: Create migration SQL**

```sql
-- Migration: 049_AddTaxonomyAxisImportance
-- Description: Adds Importance column to TaxonomyAxes for LLM-ranked price relevance
-- Date: 2026-03-08

IF NOT EXISTS (
    SELECT * FROM sys.columns
    WHERE object_id = OBJECT_ID('TaxonomyAxes') AND name = 'Importance'
)
BEGIN
    ALTER TABLE TaxonomyAxes ADD Importance INT NULL;
END
```

**Step 2: Add property to EF entity**

In `AIOMarketMaker.Core/Data/Models/TaxonomyAxis.cs`, add:
```csharp
public int? Importance { get; set; }
```

**Step 3: Rebuild Core and run migration**

Run: `dotnet build AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

Run: `dotnet run --project AIOMarketMaker.Console -- migrate`
Expected: Migration 049 applied

**Step 4: Commit**

```bash
git add AIOMarketMaker.Core/Data/Migrations/SqlServer/049_AddTaxonomyAxisImportance.sql \
       AIOMarketMaker.Core/Data/Models/TaxonomyAxis.cs
git commit -m "feat: add Importance column to TaxonomyAxes"
```

---

### Task 2: Define TaxonomyRefinement records and ITaxonomyRefiner interface

**Files:**
- Create: `AIOMarketMaker.Core/Services/Taxonomy/ITaxonomyRefiner.cs`

**Step 1: Write the failing test**

Create `AIOMarketMaker.Tests.Unit/Taxonomy/ApplyRefinementTests.cs`:

```csharp
using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class ApplyRefinementTests
{
    [Test]
    public void Should_rename_axis()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 25) }),
            }),
        };

        var refinement = new TaxonomyRefinement(
            Axes: new[]
            {
                new RefinedAxis("Axis 0", "Edition", 5,
                    RemoveValues: Array.Empty<string>(),
                    AddValues: Array.Empty<string>())
            },
            MergeAxes: Array.Empty<AxisMerge>(),
            DropAxes: Array.Empty<string>());

        var result = TaxonomyService.ApplyRefinement(axes, refinement);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Edition"));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~ApplyRefinementTests" --no-restore -v n`
Expected: FAIL — `TaxonomyRefinement` type does not exist

**Step 3: Create the interface file with records**

Create `AIOMarketMaker.Core/Services/Taxonomy/ITaxonomyRefiner.cs`:

```csharp
namespace AIOMarketMaker.Core.Services.Taxonomy;

public record RefinedAxis(
    string Original,
    string Name,
    int Importance,
    IEnumerable<string> RemoveValues,
    IEnumerable<string> AddValues);

public record AxisMerge(string Keep, string Absorb);

public record TaxonomyRefinement(
    IEnumerable<RefinedAxis> Axes,
    IEnumerable<AxisMerge> MergeAxes,
    IEnumerable<string> DropAxes);

public interface ITaxonomyRefiner
{
    Task<TaxonomyRefinement> Refine(
        IEnumerable<Axis> axes,
        string productName,
        IEnumerable<string> sampleTitles,
        CancellationToken ct = default);
}
```

**Step 4: Run test to verify it still fails (now for missing ApplyRefinement)**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~ApplyRefinementTests" --no-restore -v n`
Expected: FAIL — `ApplyRefinement` method does not exist on `TaxonomyService`

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/Taxonomy/ITaxonomyRefiner.cs \
       AIOMarketMaker.Tests.Unit/Taxonomy/ApplyRefinementTests.cs
git commit -m "feat: add TaxonomyRefinement records and ITaxonomyRefiner interface"
```

---

### Task 3: Implement ApplyRefinement (pure function, TDD)

**Files:**
- Modify: `AIOMarketMaker.Core/Services/Taxonomy/TaxonomyService.cs`
- Modify: `AIOMarketMaker.Tests.Unit/Taxonomy/ApplyRefinementTests.cs`

This task adds all unit tests for `ApplyRefinement` and implements the method. The tests from Task 2 already exist; add more and implement.

**Step 1: Add all failing tests**

Add to `ApplyRefinementTests.cs`:

```csharp
[Test]
public void Should_drop_axis()
{
    var axes = new List<Axis>
    {
        new("Axis 0", new[]
        {
            new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 25) }),
        }),
        new("Axis 7", new[]
        {
            new AxisValue("bundle", new[] { new Ngram("bundle", new[] { "bundle" }, 20) }),
            new AxisValue("condition", new[] { new Ngram("condition", new[] { "condition" }, 15) }),
        }),
    };

    var refinement = new TaxonomyRefinement(
        Axes: new[]
        {
            new RefinedAxis("Axis 0", "Edition", 5,
                Array.Empty<string>(), Array.Empty<string>())
        },
        MergeAxes: Array.Empty<AxisMerge>(),
        DropAxes: new[] { "Axis 7" });

    var result = TaxonomyService.ApplyRefinement(axes, refinement);

    Assert.That(result, Has.Count.EqualTo(1));
    Assert.That(result[0].Name, Is.EqualTo("Edition"));
}

[Test]
public void Should_remove_values_from_axis()
{
    var axes = new List<Axis>
    {
        new("Axis 0", new[]
        {
            new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 25) }),
            new AxisValue("portal", new[] { new Ngram("portal", new[] { "portal" }, 10) }),
        }),
    };

    var refinement = new TaxonomyRefinement(
        Axes: new[]
        {
            new RefinedAxis("Axis 0", "Edition", 5,
                RemoveValues: new[] { "portal" },
                AddValues: Array.Empty<string>())
        },
        MergeAxes: Array.Empty<AxisMerge>(),
        DropAxes: Array.Empty<string>());

    var result = TaxonomyService.ApplyRefinement(axes, refinement);

    var labels = result[0].Values.Select(v => v.Label).ToList();
    Assert.That(labels, Has.Count.EqualTo(2));
    Assert.That(labels, Does.Not.Contain("portal"));
}

[Test]
public void Should_add_values_to_axis()
{
    var axes = new List<Axis>
    {
        new("Axis 0", new[]
        {
            new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 25) }),
        }),
    };

    var refinement = new TaxonomyRefinement(
        Axes: new[]
        {
            new RefinedAxis("Axis 0", "Edition", 5,
                RemoveValues: Array.Empty<string>(),
                AddValues: new[] { "slim" })
        },
        MergeAxes: Array.Empty<AxisMerge>(),
        DropAxes: Array.Empty<string>());

    var result = TaxonomyService.ApplyRefinement(axes, refinement);

    var labels = result[0].Values.Select(v => v.Label).ToList();
    Assert.That(labels, Has.Count.EqualTo(3));
    Assert.That(labels, Does.Contain("slim"));
}

[Test]
public void Should_merge_axes()
{
    var axes = new List<Axis>
    {
        new("Axis 0", new[]
        {
            new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
        }),
        new("Axis 3", new[]
        {
            new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 25) }),
        }),
    };

    var refinement = new TaxonomyRefinement(
        Axes: new[]
        {
            new RefinedAxis("Axis 0", "Edition", 5,
                Array.Empty<string>(), Array.Empty<string>())
        },
        MergeAxes: new[] { new AxisMerge("Axis 0", "Axis 3") },
        DropAxes: Array.Empty<string>());

    var result = TaxonomyService.ApplyRefinement(axes, refinement);

    Assert.That(result, Has.Count.EqualTo(1));
    var labels = result[0].Values.Select(v => v.Label).ToList();
    Assert.That(labels, Does.Contain("digital"));
    Assert.That(labels, Does.Contain("disc"));
}

[Test]
public void Should_set_importance_on_axis()
{
    var axes = new List<Axis>
    {
        new("Axis 0", new[]
        {
            new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 25) }),
        }),
    };

    var refinement = new TaxonomyRefinement(
        Axes: new[]
        {
            new RefinedAxis("Axis 0", "Edition", 5,
                Array.Empty<string>(), Array.Empty<string>())
        },
        MergeAxes: Array.Empty<AxisMerge>(),
        DropAxes: Array.Empty<string>());

    var result = TaxonomyService.ApplyRefinement(axes, refinement);

    Assert.That(result[0].Importance, Is.EqualTo(5));
}

[Test]
public void Should_return_axes_unchanged_when_refinement_is_empty()
{
    var axes = new List<Axis>
    {
        new("Axis 0", new[]
        {
            new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 25) }),
        }),
    };

    var refinement = new TaxonomyRefinement(
        Axes: Array.Empty<RefinedAxis>(),
        MergeAxes: Array.Empty<AxisMerge>(),
        DropAxes: Array.Empty<string>());

    var result = TaxonomyService.ApplyRefinement(axes, refinement);

    Assert.That(result, Has.Count.EqualTo(1));
    Assert.That(result[0].Name, Is.EqualTo("Axis 0"));
    Assert.That(result[0].Values.Count(), Is.EqualTo(2));
}
```

**Step 2: Run tests to verify they all fail**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~ApplyRefinementTests" --no-restore -v n`
Expected: FAIL — `ApplyRefinement` does not exist

**Step 3: Implement ApplyRefinement**

Note: The `Axis` record needs an `Importance` property. Update `ITaxonomyService.cs`:

```csharp
public record Axis(string Name, IEnumerable<AxisValue> Values, int? Importance = null);
```

Add to `TaxonomyService.cs`:

```csharp
internal static List<Axis> ApplyRefinement(
    IEnumerable<Axis> axes, TaxonomyRefinement refinement)
{
    var axisList = axes.ToList();
    var dropSet = new HashSet<string>(refinement.DropAxes, StringComparer.OrdinalIgnoreCase);

    // Apply merges: move values from absorbed axis into keep axis
    foreach (var merge in refinement.MergeAxes)
    {
        var keepIdx = axisList.FindIndex(a => a.Name == merge.Keep);
        var absorbIdx = axisList.FindIndex(a => a.Name == merge.Absorb);
        if (keepIdx >= 0 && absorbIdx >= 0)
        {
            var merged = axisList[keepIdx].Values.Concat(axisList[absorbIdx].Values);
            axisList[keepIdx] = axisList[keepIdx] with { Values = merged.ToList() };
            dropSet.Add(merge.Absorb);
        }
    }

    // Remove dropped axes
    axisList.RemoveAll(a => dropSet.Contains(a.Name));

    // Apply per-axis refinements
    var refinedAxes = refinement.Axes.ToDictionary(
        r => r.Original, r => r, StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < axisList.Count; i++)
    {
        if (!refinedAxes.TryGetValue(axisList[i].Name, out var refined))
        {
            continue;
        }

        var values = axisList[i].Values.ToList();

        // Remove values
        var removeSet = new HashSet<string>(
            refined.RemoveValues, StringComparer.OrdinalIgnoreCase);
        values.RemoveAll(v => removeSet.Contains(v.Label));

        // Add values
        foreach (var addLabel in refined.AddValues)
        {
            if (values.All(v => !v.Label.Equals(addLabel, StringComparison.OrdinalIgnoreCase)))
            {
                values.Add(new AxisValue(addLabel,
                    new[] { new Ngram(addLabel, new[] { addLabel }, 0) }));
            }
        }

        axisList[i] = new Axis(refined.Name, values, refined.Importance);
    }

    return axisList;
}
```

**Step 4: Run tests to verify they all pass**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~ApplyRefinementTests" --no-restore -v n`
Expected: PASS — all 7 tests green

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/Taxonomy/TaxonomyService.cs \
       AIOMarketMaker.Core/Services/Taxonomy/ITaxonomyService.cs \
       AIOMarketMaker.Tests.Unit/Taxonomy/ApplyRefinementTests.cs
git commit -m "feat: implement ApplyRefinement with full unit test coverage"
```

---

### Task 4: Implement LlmTaxonomyRefiner

**Files:**
- Create: `AIOMarketMaker.Core/Services/Taxonomy/LlmTaxonomyRefiner.cs`

**Step 1: Write the failing integration test**

Create `AIOMarketMaker.Tests.Integration/Taxonomy/LlmTaxonomyRefiner_IntegrationTests.cs`:

```csharp
using AIOMarketMaker.Core.Services.Taxonomy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using OpenAI.Chat;

namespace AIOMarketMaker.Tests.Integration.Taxonomy;

[TestFixture]
[Category("Integration")]
[Explicit("Requires valid OpenAI API key in local.settings.json")]
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
            Assert.Ignore("OpenAi:ApiKey not found in local.settings.json");
        }

        var model = configuration.GetValue<string>("OpenAi:TaxonomyModel") ?? "gpt-5-mini";
        var client = new ChatClient(model, apiKey);
        var logger = new Mock<ILogger<LlmTaxonomyRefiner>>();

        _refiner = new LlmTaxonomyRefiner(client, logger.Object);
    }

    [Test]
    public async Task Should_label_ps5_axes_and_clean_mixed_values()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 100) }),
                new AxisValue("disk", new[] { new Ngram("disk", new[] { "disk" }, 80) }),
                new AxisValue("portal", new[] { new Ngram("portal", new[] { "portal" }, 30) }),
            }),
            new("Axis 7", new[]
            {
                new AxisValue("bundle", new[] { new Ngram("bundle", new[] { "bundle" }, 60) }),
                new AxisValue("condition", new[] { new Ngram("condition", new[] { "condition" }, 40) }),
            }),
            new("Axis 9", new[]
            {
                new AxisValue("black", new[] { new Ngram("black", new[] { "black" }, 50) }),
                new AxisValue("white", new[] { new Ngram("white", new[] { "white" }, 70) }),
            }),
        };

        var sampleTitles = new[]
        {
            "Sony PlayStation 5 PS5 Disc Edition Console - White",
            "PS5 Digital Edition 825GB - Black",
            "PlayStation 5 Console Disc Version Bundle With Extra Controller",
            "PS5 Slim Digital Edition 1TB Console New Sealed",
            "Sony PS5 Portal Remote Player Handheld",
            "PlayStation 5 Disc Edition Used Good Condition",
            "PS5 Digital Console Only No Controller White",
            "Sony PlayStation 5 Disc 825GB Bundle 2 Games",
            "PS5 Slim Disc Edition Console White Brand New",
            "PlayStation 5 PS5 Console Digital Version Sealed Box",
        };

        var result = await _refiner.Refine(axes, "PlayStation 5 Console", sampleTitles, CancellationToken.None);

        // Schema compliance
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Axes, Is.Not.Empty);

        // All refined axes should reference valid originals
        var originalNames = axes.Select(a => a.Name).ToHashSet();
        foreach (var refined in result.Axes)
        {
            Assert.That(originalNames, Does.Contain(refined.Original),
                $"Refined axis references unknown original: {refined.Original}");
            Assert.That(refined.Name, Is.Not.Empty, "Axis name should not be empty");
            Assert.That(refined.Importance, Is.InRange(1, 5),
                $"Importance should be 1-5, got {refined.Importance} for {refined.Name}");
        }

        // Mixed axis (bundle + condition) should be dropped or have values removed
        var axis7Refined = result.Axes.FirstOrDefault(a => a.Original == "Axis 7");
        var axis7Dropped = result.DropAxes.Contains("Axis 7");
        Assert.That(axis7Refined?.RemoveValues.Any() == true || axis7Dropped,
            "Axis 7 (bundle, condition) should be cleaned or dropped — these are unrelated concepts");

        // Color axis should get a meaningful name
        var colorAxis = result.Axes.FirstOrDefault(a => a.Original == "Axis 9");
        Assert.That(colorAxis, Is.Not.Null, "Color axis should be refined");
        Assert.That(colorAxis!.Name.ToLower(), Does.Contain("colo").Or.Contain("colour"),
            $"Color axis should be named 'Color' or similar, got '{colorAxis.Name}'");
    }

    [Test]
    public async Task Should_return_valid_json_schema_compliant_response()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("new", new[] { new Ngram("new", new[] { "new" }, 50) }),
                new AxisValue("used", new[] { new Ngram("used", new[] { "used" }, 40) }),
                new AxisValue("refurbished", new[] { new Ngram("refurbished", new[] { "refurbished" }, 20) }),
            }),
        };

        var sampleTitles = new[]
        {
            "iPhone 15 Pro Max 256GB Black - New",
            "iPhone 15 Pro Max 128GB Used Good Condition",
            "Apple iPhone 15 Pro Max Refurbished Grade A",
        };

        var result = await _refiner.Refine(axes, "iPhone 15 Pro Max", sampleTitles, CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.DropAxes, Is.Not.Null);
        Assert.That(result.MergeAxes, Is.Not.Null);

        foreach (var axis in result.Axes)
        {
            Assert.That(axis.RemoveValues, Is.Not.Null);
            Assert.That(axis.AddValues, Is.Not.Null);
        }
    }

    [Test]
    public async Task Should_suggest_additional_values_for_coverage()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 100) }),
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 80) }),
            }),
        };

        var sampleTitles = new[]
        {
            "Sony PlayStation 5 PS5 Disc Edition Console - White",
            "PS5 Digital Edition 825GB",
            "PS5 Slim Digital Edition 1TB",
            "PlayStation 5 Disc Version Bundle",
            "PS5 Slim Disc Edition Console",
        };

        var result = await _refiner.Refine(axes, "PlayStation 5 Console", sampleTitles, CancellationToken.None);

        var editionAxis = result.Axes.FirstOrDefault(a => a.Original == "Axis 0");
        Assert.That(editionAxis, Is.Not.Null);

        // LLM should notice "slim" appears in titles but isn't in the axis
        Assert.That(editionAxis!.AddValues, Is.Not.Empty,
            "Should suggest at least one additional value from sample titles");
    }

    [Test]
    public async Task Should_rank_edition_higher_than_color_for_ps5()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 100) }),
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 80) }),
            }),
            new("Axis 9", new[]
            {
                new AxisValue("black", new[] { new Ngram("black", new[] { "black" }, 50) }),
                new AxisValue("white", new[] { new Ngram("white", new[] { "white" }, 70) }),
            }),
        };

        var sampleTitles = new[]
        {
            "Sony PlayStation 5 PS5 Disc Edition Console - White",
            "PS5 Digital Edition 825GB - Black",
            "PlayStation 5 Disc Console White",
        };

        var result = await _refiner.Refine(axes, "PlayStation 5 Console", sampleTitles, CancellationToken.None);

        var editionAxis = result.Axes.FirstOrDefault(a => a.Original == "Axis 0");
        var colorAxis = result.Axes.FirstOrDefault(a => a.Original == "Axis 9");

        Assert.That(editionAxis, Is.Not.Null);
        Assert.That(colorAxis, Is.Not.Null);
        Assert.That(editionAxis!.Importance, Is.GreaterThan(colorAxis!.Importance),
            $"Edition (importance={editionAxis.Importance}) should rank higher than Color (importance={colorAxis.Importance})");
    }

    [Test]
    public async Task Should_handle_camera_specific_axes()
    {
        var axes = new List<Axis>
        {
            new("Axis 2", new[]
            {
                new AxisValue("body", new[] { new Ngram("body", new[] { "body" }, 50) }),
                new AxisValue("stm", new[] { new Ngram("stm", new[] { "stm" }, 30) }),
                new AxisValue("usm", new[] { new Ngram("usm", new[] { "usm" }, 20) }),
            }),
            new("Axis 14", new[]
            {
                new AxisValue("lens", new[] { new Ngram("lens", new[] { "lens" }, 60) }),
                new AxisValue("shutter", new[] { new Ngram("shutter", new[] { "shutter" }, 15) }),
            }),
        };

        var sampleTitles = new[]
        {
            "Canon EOS R6 Mark II Camera Body Only",
            "Canon EOS R6 24-105mm STM Lens Kit",
            "Canon EOS R6 Body + RF 50mm USM Lens",
            "Canon EOS R6 Mirrorless Camera Shutter Count 5000",
        };

        var result = await _refiner.Refine(axes, "Canon EOS R6 Camera", sampleTitles, CancellationToken.None);

        Assert.That(result, Is.Not.Null);

        // All axes should get meaningful names (not "Axis 2")
        foreach (var axis in result.Axes)
        {
            Assert.That(axis.Name, Does.Not.StartWith("Axis"),
                $"Axis '{axis.Original}' should get a descriptive name, not '{axis.Name}'");
        }
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker.Tests.Integration/AIOMarketMaker.Tests.Integration.csproj --filter "FullyQualifiedName~LlmTaxonomyRefiner" -v n`
Expected: FAIL — `LlmTaxonomyRefiner` class does not exist

**Step 3: Implement LlmTaxonomyRefiner**

Create `AIOMarketMaker.Core/Services/Taxonomy/LlmTaxonomyRefiner.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace AIOMarketMaker.Core.Services.Taxonomy;

public class LlmTaxonomyRefiner : ITaxonomyRefiner
{
    private readonly ChatClient _client;
    private readonly ILogger<LlmTaxonomyRefiner> _logger;

    private static readonly BinaryData RefinementSchema = BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "axes": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "original": { "type": "string" },
                            "name": { "type": "string" },
                            "importance": { "type": "integer" },
                            "remove_values": {
                                "type": "array",
                                "items": { "type": "string" }
                            },
                            "add_values": {
                                "type": "array",
                                "items": { "type": "string" }
                            }
                        },
                        "required": ["original", "name", "importance", "remove_values", "add_values"],
                        "additionalProperties": false
                    }
                },
                "merge_axes": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "keep": { "type": "string" },
                            "absorb": { "type": "string" }
                        },
                        "required": ["keep", "absorb"],
                        "additionalProperties": false
                    }
                },
                "drop_axes": {
                    "type": "array",
                    "items": { "type": "string" }
                }
            },
            "required": ["axes", "merge_axes", "drop_axes"],
            "additionalProperties": false
        }
        """);

    private static readonly ChatResponseFormat RefinementFormat =
        ChatResponseFormat.CreateJsonSchemaFormat(
            "taxonomy_refinement", RefinementSchema, jsonSchemaIsStrict: true);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public LlmTaxonomyRefiner(ChatClient client, ILogger<LlmTaxonomyRefiner> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<TaxonomyRefinement> Refine(
        IEnumerable<Axis> axes,
        string productName,
        IEnumerable<string> sampleTitles,
        CancellationToken ct = default)
    {
        var axisList = axes.ToList();
        var prompt = BuildPrompt(axisList, productName, sampleTitles);

        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(
                "You are a product taxonomy expert. You refine automatically-discovered " +
                "product variant axes from eBay listing titles. Return only the JSON delta."),
            ChatMessage.CreateUserMessage(prompt),
        };

        var options = new ChatCompletionOptions
        {
            ResponseFormat = RefinementFormat,
        };

        var completion = await _client.CompleteChatAsync(messages, options, ct);
        var json = completion.Value.Content[0].Text;

        _logger.LogDebug("LLM taxonomy refinement response: {Json}", json);

        var response = JsonSerializer.Deserialize<LlmRefinementResponse>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize LLM response");

        return MapToRefinement(response);
    }

    private static string BuildPrompt(
        List<Axis> axes, string productName, IEnumerable<string> sampleTitles)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Product: {productName}");
        sb.AppendLine();
        sb.AppendLine("## Current axes (auto-discovered from listing titles)");
        sb.AppendLine();

        foreach (var axis in axes)
        {
            var values = axis.Values.Select(v => v.Label);
            sb.AppendLine($"- {axis.Name}: {string.Join(", ", values)}");
        }

        sb.AppendLine();
        sb.AppendLine("## Sample listing titles");
        sb.AppendLine();

        foreach (var title in sampleTitles.Take(20))
        {
            sb.AppendLine($"- {title}");
        }

        sb.AppendLine();
        sb.AppendLine("## Instructions");
        sb.AppendLine();
        sb.AppendLine("Refine these axes. For each axis:");
        sb.AppendLine("1. Give it a short, descriptive name (e.g., 'Edition', 'Color', 'Condition')");
        sb.AppendLine("2. Rate its importance for pricing (1=irrelevant, 5=major price driver)");
        sb.AppendLine("3. List any values to REMOVE (don't belong in this axis)");
        sb.AppendLine("4. List any values to ADD (visible in titles but missing from axis)");
        sb.AppendLine();
        sb.AppendLine("Also specify:");
        sb.AppendLine("- Axes to DROP entirely (junk, noise, or mixed unrelated concepts)");
        sb.AppendLine("- Axes to MERGE (same concept split across axes; specify which to keep and which to absorb)");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Only add values you can see evidence for in the sample titles");
        sb.AppendLine("- Drop axes where values are from unrelated categories mixed together");
        sb.AppendLine("- Keep add_values and remove_values as short lowercase strings matching title tokens");
        sb.AppendLine("- Every surviving axis must have an entry in the axes array");

        return sb.ToString();
    }

    private static TaxonomyRefinement MapToRefinement(LlmRefinementResponse response)
    {
        var axes = response.Axes.Select(a => new RefinedAxis(
            a.Original, a.Name, a.Importance,
            a.RemoveValues, a.AddValues));

        var merges = response.MergeAxes.Select(m => new AxisMerge(m.Keep, m.Absorb));

        return new TaxonomyRefinement(axes, merges, response.DropAxes);
    }

    // Internal DTO for JSON deserialization (snake_case)
    private record LlmRefinementResponse(
        [property: JsonPropertyName("axes")] LlmRefinedAxis[] Axes,
        [property: JsonPropertyName("merge_axes")] LlmAxisMerge[] MergeAxes,
        [property: JsonPropertyName("drop_axes")] string[] DropAxes);

    private record LlmRefinedAxis(
        [property: JsonPropertyName("original")] string Original,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("importance")] int Importance,
        [property: JsonPropertyName("remove_values")] string[] RemoveValues,
        [property: JsonPropertyName("add_values")] string[] AddValues);

    private record LlmAxisMerge(
        [property: JsonPropertyName("keep")] string Keep,
        [property: JsonPropertyName("absorb")] string Absorb);
}
```

**Step 4: Run integration tests, iterate on prompt until passing**

Run: `dotnet test AIOMarketMaker.Tests.Integration/AIOMarketMaker.Tests.Integration.csproj --filter "FullyQualifiedName~LlmTaxonomyRefiner" -v n`

If tests fail due to LLM output quality, adjust the prompt in `BuildPrompt` and re-run. Common adjustments:
- Add few-shot examples to the prompt
- Strengthen the instruction about mixed-concept axes
- Clarify what "importance" means with examples
- Add explicit instruction about color axis naming

Iterate until all 6 integration tests pass.

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/Taxonomy/LlmTaxonomyRefiner.cs \
       AIOMarketMaker.Tests.Integration/Taxonomy/LlmTaxonomyRefiner_IntegrationTests.cs
git commit -m "feat: implement LlmTaxonomyRefiner with structured output"
```

---

### Task 5: Wire LLM refinement into TaxonomyService.Generate

**Files:**
- Modify: `AIOMarketMaker.Core/Services/Taxonomy/TaxonomyService.cs`
- Modify: `AIOMarketMaker.Core/Services/Taxonomy/ITaxonomyService.cs`

**Step 1: Write the failing test**

Add to `AIOMarketMaker.Tests.Unit/Taxonomy/TaxonomyServiceTests.cs` (or create new file):

```csharp
// In existing TaxonomyServiceTests.cs, add:

[Test]
public async Task Should_apply_refinement_when_refiner_is_provided()
{
    // Arrange: set up a mock refiner that renames axes
    var mockRefiner = new Mock<ITaxonomyRefiner>();
    mockRefiner.Setup(r => r.Refine(
            It.IsAny<IEnumerable<Axis>>(),
            It.IsAny<string>(),
            It.IsAny<IEnumerable<string>>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(new TaxonomyRefinement(
            Axes: new[]
            {
                new RefinedAxis("Axis 0", "TestLabel", 3,
                    Array.Empty<string>(), Array.Empty<string>())
            },
            MergeAxes: Array.Empty<AxisMerge>(),
            DropAxes: Array.Empty<string>()));

    // ... (use existing test setup pattern from TaxonomyServiceTests.cs
    // to create a TaxonomyService with the mock refiner injected)
}
```

Note: The exact test depends on the existing TaxonomyServiceTests setup. The key assertion is that when a refiner is injected, the output axes have the LLM-provided names and importance values.

**Step 2: Update Generate signature**

In `ITaxonomyService.cs`, update:
```csharp
public interface ITaxonomyService
{
    Task<TaxonomyResult> Generate(
        IEnumerable<string> titles,
        string? productName = null,
        CancellationToken ct = default);
}
```

**Step 3: Update TaxonomyService constructor and Generate**

Add `ITaxonomyRefiner?` as an optional constructor parameter:

```csharp
private readonly ITaxonomyRefiner? _refiner;

public TaxonomyService(
    INgramExtractor extractor,
    IMutualExclusivityAnalyzer analyzer,
    ICommunityDetector detector,
    IEmbeddingService embeddingService,
    ITaxonomyRefiner? refiner = null)
{
    _extractor = extractor;
    _analyzer = analyzer;
    _detector = detector;
    _embeddingService = embeddingService;
    _refiner = refiner;
}
```

In `Generate`, after `PostProcess` and before `AssignListings`:

```csharp
axes = PostProcess(axes, matchSetLookup, valueEmbeddings);

if (_refiner != null && productName != null)
{
    try
    {
        var sampleTitles = titleList.Take(20);
        var refinement = await _refiner.Refine(axes, productName, sampleTitles, ct);
        axes = ApplyRefinement(axes, refinement);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "LLM taxonomy refinement failed, using unrefined axes");
    }
}

var assignments = AssignListings(titleList, axes, matchSetLookup);
```

Note: Add `ILogger<TaxonomyService>` to constructor if not already present.

**Step 4: Run all taxonomy tests**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~Taxonomy" --no-restore -v n`
Expected: All existing tests pass (refiner is null in existing tests, so behavior unchanged)

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/Taxonomy/TaxonomyService.cs \
       AIOMarketMaker.Core/Services/Taxonomy/ITaxonomyService.cs \
       AIOMarketMaker.Tests.Unit/Taxonomy/TaxonomyServiceTests.cs
git commit -m "feat: wire LLM refinement into TaxonomyService.Generate"
```

---

### Task 6: Wire DI and update TaxonomyPostJobStage

**Files:**
- Modify: `AIOMarketMaker.Api/Program.cs`
- Modify: `AIOMarketMaker.Console/Startup.cs`
- Modify: `AIOMarketMaker.Core/Services/Pipeline/TaxonomyPostJobStage.cs`
- Modify: `AIOMarketMaker.Core/Services/Taxonomy/TaxonomyPersistenceService.cs`

**Step 1: Register LlmTaxonomyRefiner in DI**

In `Program.cs`, add after existing ChatClient registrations:

```csharp
builder.Services.AddSingleton<ITaxonomyRefiner>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var apiKey = config["OpenAi:ApiKey"]
        ?? throw new InvalidOperationException("OpenAi:ApiKey is not configured");
    var model = config["OpenAi:TaxonomyModel"] ?? "gpt-5-mini";
    var client = new ChatClient(model, apiKey);
    var logger = sp.GetRequiredService<ILogger<LlmTaxonomyRefiner>>();
    return new LlmTaxonomyRefiner(client, logger);
});
```

Do the same in `Startup.cs` for Console.

**Step 2: Update TaxonomyPostJobStage to pass SearchTerm**

In `TaxonomyPostJobStage.Execute`, change:
```csharp
var result = await taxonomyService.Generate(titles, ct);
```
to:
```csharp
var result = await taxonomyService.Generate(titles, context.SearchTerm, ct);
```

**Step 3: Update TaxonomyPersistenceService to save Importance**

In the axis persistence loop, add:
```csharp
Importance = axis.Importance
```
to the `TaxonomyAxis` entity creation.

**Step 4: Build and verify**

Run: `dotnet build AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~Taxonomy" --no-restore -v n`
Expected: All tests pass

**Step 5: Commit**

```bash
git add AIOMarketMaker.Api/Program.cs \
       AIOMarketMaker.Console/Startup.cs \
       AIOMarketMaker.Core/Services/Pipeline/TaxonomyPostJobStage.cs \
       AIOMarketMaker.Core/Services/Taxonomy/TaxonomyPersistenceService.cs
git commit -m "feat: wire LlmTaxonomyRefiner into DI and pipeline"
```

---

### Task 7: End-to-end validation with backfill

**Step 1: Run taxonomy on a single job to validate**

Run: `dotnet run --project AIOMarketMaker.Console -- taxonomy 1029`
Expected: Axes have real names (not "Axis 0"), importance values set, improved coverage

**Step 2: Verify axis quality in DB**

```bash
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "
SELECT ta.Name, ta.Importance,
       STRING_AGG(tv.Label, ', ') WITHIN GROUP (ORDER BY tv.Label) AS Vals
FROM TaxonomyRuns tr
JOIN TaxonomyAxes ta ON ta.TaxonomyRunId = tr.Id
JOIN TaxonomyAxisValues tv ON tv.TaxonomyAxisId = ta.Id
WHERE tr.ScrapeJobId = 1029
GROUP BY ta.Name, ta.Importance
ORDER BY ta.Importance DESC
" -y 0
```

Expected: Named axes ordered by importance, no mixed-concept axes

**Step 3: If quality is good, backfill all jobs**

Run: `dotnet run --project AIOMarketMaker.Console -- backfill-taxonomy`

**Step 4: Commit any prompt adjustments**

If the prompt needed tuning during validation, commit:
```bash
git add AIOMarketMaker.Core/Services/Taxonomy/LlmTaxonomyRefiner.cs
git commit -m "tune: refine LLM taxonomy prompt based on backfill results"
```
