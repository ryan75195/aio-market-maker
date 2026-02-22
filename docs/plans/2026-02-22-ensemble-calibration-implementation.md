# Ensemble Calibration Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Integrate the logistic regression ensemble layer that combines ONNX cross-encoder logits with OpenAI embedding similarity scores to produce calibrated confidence scores.

**Architecture:** Three-layer classifier: `VariantModelRunner` (dumb ONNX wrapper returning raw logits) → `VariantClassifier` (applies LR ensemble calibration, implements `IVariantClassifierClient`) → downstream consumers like `ComparablesEtlService` which just call `Classify()`. The similarity score already exists in the pipeline but is discarded before classification — we thread it through.

**Tech Stack:** .NET 8.0, ONNX Runtime, NUnit/Moq

---

### Task 1: Add `LogitDiff` to `PairResult` and `SimilarityScore` to `ClassifyPairRequest`

**Files:**
- Modify: `AIOMarketMaker.Core/Services/IVariantClassifierClient.cs:12-18`

**Step 1: Update the DTOs**

Add optional fields with defaults so existing callers don't break:

```csharp
public record ClassifyPairRequest(
    string TitleA,
    string DescriptionA,
    string TitleB,
    string DescriptionB,
    float? SimilarityScore = null);

public record PairResult(bool IsComparable, float Confidence, string? Reason = null, float? LogitDiff = null);
```

**Step 2: Build the solution to verify no compile errors**

Run: `dotnet build AIOMarketMaker.sln`
Expected: Build succeeds — both new fields have defaults so all existing callers compile.

**Step 3: Run existing unit tests to verify nothing broke**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj`
Expected: All existing tests pass unchanged.

**Step 4: Commit**

```bash
git add AIOMarketMaker.Core/Services/IVariantClassifierClient.cs
git commit -m "feat: add LogitDiff and SimilarityScore fields to classifier DTOs"
```

---

### Task 2: Rename `OnnxVariantClassifier` → `VariantModelRunner` and return logit diff

**Files:**
- Modify: `AIOMarketMaker.ML/Services/OnnxVariantClassifier.cs` (rename file to `VariantModelRunner.cs`)
- Modify: `AIOMarketMaker.Tests.Unit/Services/OnnxVariantClassifier_UnitTests.cs` (rename to `VariantModelRunner_UnitTests.cs`)
- Modify: `AIOMarketMaker.Tests.Integration/OnnxVariantClassifier_IntegrationTests.cs` (rename to `VariantModelRunner_IntegrationTests.cs`)
- Modify: DI registrations in `AIOMarketMaker.Etl/Program.cs:206-213` and `AIOMarketMaker.Api/Program.cs:125-132`

**Step 1: Rename the class and file**

Rename `OnnxVariantClassifier` → `VariantModelRunner` throughout. The class no longer implements `IVariantClassifierClient` — it becomes an internal service with the same `Classify` method signature but without the interface.

In `VariantModelRunner.cs` (renamed from `OnnxVariantClassifier.cs`):

```csharp
public class VariantModelRunner : IDisposable
```

Remove `: IVariantClassifierClient` from the class declaration. The method signatures stay the same.

**Step 2: Update `Classify` to populate `LogitDiff` in results**

In `VariantModelRunner.cs`, update the results loop (lines 127-134) to compute and include `LogitDiff`:

```csharp
var results = new List<PairResult>(pairList.Count);
for (var i = 0; i < pairList.Count; i++)
{
    var logits = new float[] { logitsTensor[i, 0], logitsTensor[i, 1] };
    var probs = Softmax(logits);
    var isComparable = probs[1] > probs[0];
    var confidence = probs.Max();
    var logitDiff = logits[1] - logits[0];
    results.Add(new PairResult(isComparable, confidence, LogitDiff: logitDiff));
}
```

**Step 3: Update DI registrations**

Both `Program.cs` files currently register:
```csharp
services.AddSingleton<IVariantClassifierClient, OnnxVariantClassifier>();
```

Change to register `VariantModelRunner` as itself (not as the interface — the new `VariantClassifier` will implement the interface):
```csharp
services.AddSingleton<VariantModelRunner>();
```

Don't register `IVariantClassifierClient` yet — that's Task 3.

**Step 4: Update test files**

Rename test class and all references from `OnnxVariantClassifier` → `VariantModelRunner`:
- `OnnxVariantClassifier_UnitTests` → `VariantModelRunner_UnitTests`
- `OnnxVariantClassifier_IntegrationTests` → `VariantModelRunner_IntegrationTests`

Static method calls like `OnnxVariantClassifier.Softmax(...)` → `VariantModelRunner.Softmax(...)`.
Constructor calls: `new OnnxVariantClassifier(config, logger)` → `new VariantModelRunner(config, logger)`.

**Step 5: Build and test**

Run: `dotnet build AIOMarketMaker.sln`
Expected: Build succeeds. (Note: will fail until Task 3 adds the `IVariantClassifierClient` registration. If needed, temporarily add `services.AddSingleton<IVariantClassifierClient, VariantModelRunner>();` alias — but since Task 3 follows immediately, you can batch these.)

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj`
Expected: All unit tests pass.

**Step 6: Commit**

```bash
git add -A
git commit -m "refactor: rename OnnxVariantClassifier to VariantModelRunner, return LogitDiff"
```

---

### Task 3: Create `VariantClassifier` with ensemble calibration

**Files:**
- Create: `AIOMarketMaker.ML/Services/VariantClassifier.cs`
- Modify: `AIOMarketMaker.Etl/Program.cs:206-213` (DI)
- Modify: `AIOMarketMaker.Api/Program.cs:125-132` (DI)
- Modify: `AIOMarketMaker.Api/appsettings.json:26-33` (config)

**Step 1: Write the failing test**

Create `AIOMarketMaker.Tests.Unit/Services/VariantClassifier_UnitTests.cs`:

```csharp
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
    private static readonly EnsembleConfig DefaultConfig = new(
        LogitWeight: 2.4910f,
        SimilarityWeight: 0.4324f,
        Intercept: -2.6254f);

    [Test]
    public async Task Should_apply_ensemble_calibration_when_similarity_score_present()
    {
        // Arrange: mock model runner returns known logit diff
        var logitDiff = 5.632f; // strong match signal
        var similarity = 0.92f;
        var modelRunner = MockModelRunner(logitDiff);
        var classifier = new VariantClassifier(modelRunner, DefaultConfig, Mock.Of<ILogger<VariantClassifier>>());

        var request = new ClassifyPairRequest("Title A", "Desc A", "Title B", "Desc B", similarity);

        // Act
        var results = await classifier.Classify([request]);

        // Assert: sigmoid(2.4910 * 5.632 + 0.4324 * 0.92 + (-2.6254))
        // = sigmoid(14.029 + 0.398 - 2.625) = sigmoid(11.802) ≈ 0.99999
        var result = results[0];
        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.True);
            Assert.That(result.Confidence, Is.GreaterThan(0.99f));
            Assert.That(result.LogitDiff, Is.EqualTo(logitDiff).Within(0.001f));
        });
    }

    [Test]
    public async Task Should_fall_back_to_model_runner_when_similarity_score_missing()
    {
        // Arrange: no similarity score
        var logitDiff = 2.0f;
        var modelRunner = MockModelRunner(logitDiff);
        var classifier = new VariantClassifier(modelRunner, DefaultConfig, Mock.Of<ILogger<VariantClassifier>>());

        var request = new ClassifyPairRequest("Title A", "Desc A", "Title B", "Desc B");

        // Act
        var results = await classifier.Classify([request]);

        // Assert: falls back to raw softmax — probs from logits [0-logitDiff/2, logitDiff/2]
        // Model runner returns (true, softmax_confidence, logitDiff)
        var result = results[0];
        Assert.That(result.IsComparable, Is.True);
        Assert.That(result.LogitDiff, Is.EqualTo(logitDiff).Within(0.001f));
    }

    [Test]
    public async Task Should_classify_as_not_comparable_when_ensemble_score_below_threshold()
    {
        // Arrange: weak logit diff + low similarity → not comparable
        var logitDiff = 0.5f;
        var similarity = 0.72f;
        var modelRunner = MockModelRunner(logitDiff);
        var classifier = new VariantClassifier(modelRunner, DefaultConfig, Mock.Of<ILogger<VariantClassifier>>());

        var request = new ClassifyPairRequest("Title A", "Desc A", "Title B", "Desc B", similarity);

        // Act
        var results = await classifier.Classify([request]);

        // Assert: sigmoid(2.4910 * 0.5 + 0.4324 * 0.72 + (-2.6254))
        // = sigmoid(1.2455 + 0.3113 - 2.6254) = sigmoid(-1.0686) ≈ 0.256
        var result = results[0];
        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.False);
            Assert.That(result.Confidence, Is.LessThan(0.5f));
        });
    }

    [Test]
    public async Task Should_produce_calibrated_confidence_in_midrange()
    {
        // Arrange: moderate logit diff + moderate similarity
        var logitDiff = 1.5f;
        var similarity = 0.85f;
        var modelRunner = MockModelRunner(logitDiff);
        var classifier = new VariantClassifier(modelRunner, DefaultConfig, Mock.Of<ILogger<VariantClassifier>>());

        var request = new ClassifyPairRequest("Title A", "Desc A", "Title B", "Desc B", similarity);

        // Act
        var results = await classifier.Classify([request]);

        // Assert: sigmoid(2.4910 * 1.5 + 0.4324 * 0.85 + (-2.6254))
        // = sigmoid(3.7365 + 0.3675 - 2.6254) = sigmoid(1.4786) ≈ 0.814
        var result = results[0];
        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.True);
            Assert.That(result.Confidence, Is.InRange(0.78f, 0.85f));
        });
    }

    [Test]
    public async Task Should_delegate_health_check_to_model_runner()
    {
        var modelRunner = new Mock<VariantModelRunner>() { CallBase = false };
        // VariantModelRunner requires constructor args — use a different approach:
        // We'll test this in integration. Skip for unit test.
    }

    [Test]
    public async Task Should_handle_batch_with_mixed_similarity_scores()
    {
        // Arrange: two requests — one with similarity, one without
        var logitDiff = 3.0f;
        var modelRunner = MockModelRunner(logitDiff, count: 2);
        var classifier = new VariantClassifier(modelRunner, DefaultConfig, Mock.Of<ILogger<VariantClassifier>>());

        var requests = new[]
        {
            new ClassifyPairRequest("A1", "D1", "B1", "D1", 0.90f),
            new ClassifyPairRequest("A2", "D2", "B2", "D2") // no similarity
        };

        // Act
        var results = await classifier.Classify(requests);

        // Assert: first gets ensemble, second gets fallback
        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results[0].LogitDiff, Is.EqualTo(logitDiff).Within(0.001f));
            Assert.That(results[1].LogitDiff, Is.EqualTo(logitDiff).Within(0.001f));
        });
    }

    private static VariantModelRunner MockModelRunner(float logitDiff, int count = 1)
    {
        // We can't easily mock VariantModelRunner (requires ONNX model file).
        // Instead, create a FakeModelRunner for testing.
        // This will be resolved in the implementation — see note below.
        throw new NotImplementedException("Implement with interface or test double");
    }
}
```

**Important design note:** `VariantModelRunner` has a constructor that loads an ONNX model file, making it hard to mock in unit tests. Two options:

**Option A (recommended):** Extract a minimal internal interface `IVariantModelRunner` that `VariantModelRunner` implements and `VariantClassifier` depends on. This is purely for testability — it's not part of the public API.

```csharp
// Internal to ML project — not visible to downstream
internal interface IVariantModelRunner
{
    Task<IReadOnlyList<PairResult>> Classify(IEnumerable<ClassifyPairRequest> pairs, CancellationToken ct = default);
    Task<bool> IsHealthy(CancellationToken ct = default);
}
```

**Option B:** Make `Classify` virtual on `VariantModelRunner` and use Moq. This is simpler but couples the test to the concrete class.

Go with Option A. The test file above should use `Mock<IVariantModelRunner>` instead of trying to mock the concrete class. Here's the corrected `MockModelRunner`:

```csharp
private static IVariantModelRunner MockModelRunner(float logitDiff, int count = 1)
{
    var softmax = VariantModelRunner.Softmax([-logitDiff / 2, logitDiff / 2]);
    var isComparable = softmax[1] > softmax[0];
    var confidence = softmax.Max();
    var result = new PairResult(isComparable, confidence, LogitDiff: logitDiff);

    var mock = new Mock<IVariantModelRunner>();
    mock.Setup(m => m.Classify(It.IsAny<IEnumerable<ClassifyPairRequest>>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(Enumerable.Repeat(result, count).ToList());
    mock.Setup(m => m.IsHealthy(It.IsAny<CancellationToken>()))
        .ReturnsAsync(true);
    return mock.Object;
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test AIOMarketMaker.Tests.Unit --filter "FullyQualifiedName~VariantClassifier_UnitTests" -v n`
Expected: FAIL — `VariantClassifier` class doesn't exist yet.

**Step 3: Create the `EnsembleConfig` record and `IVariantModelRunner` interface**

In `AIOMarketMaker.ML/Services/VariantModelRunner.cs`, add above the class:

```csharp
public record EnsembleConfig(
    float LogitWeight,
    float SimilarityWeight,
    float Intercept);

internal interface IVariantModelRunner
{
    Task<IReadOnlyList<PairResult>> Classify(IEnumerable<ClassifyPairRequest> pairs, CancellationToken ct = default);
    Task<bool> IsHealthy(CancellationToken ct = default);
}
```

Update class declaration:

```csharp
public class VariantModelRunner : IVariantModelRunner, IDisposable
```

Make `IVariantModelRunner` visible to test project by adding to `AIOMarketMaker.ML.csproj`:

```xml
<ItemGroup>
    <InternalsVisibleTo Include="AIOMarketMaker.Tests.Unit" />
</ItemGroup>
```

**Step 4: Create `VariantClassifier.cs`**

Create `AIOMarketMaker.ML/Services/VariantClassifier.cs`:

```csharp
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.ML.Services;

public class VariantClassifier : IVariantClassifierClient
{
    private readonly IVariantModelRunner _modelRunner;
    private readonly EnsembleConfig? _ensemble;
    private readonly ILogger<VariantClassifier> _logger;

    public VariantClassifier(
        IVariantModelRunner modelRunner,
        EnsembleConfig? ensemble,
        ILogger<VariantClassifier> logger)
    {
        _modelRunner = modelRunner;
        _ensemble = ensemble;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PairResult>> Classify(
        IEnumerable<ClassifyPairRequest> pairs,
        CancellationToken ct = default)
    {
        var pairList = pairs.ToList();
        if (pairList.Count == 0)
        {
            return Array.Empty<PairResult>();
        }

        var rawResults = await _modelRunner.Classify(pairList, ct);
        var calibrated = new List<PairResult>(pairList.Count);

        for (var i = 0; i < pairList.Count; i++)
        {
            var raw = rawResults[i];
            var request = pairList[i];

            if (_ensemble is not null && request.SimilarityScore.HasValue && raw.LogitDiff.HasValue)
            {
                var score = _ensemble.LogitWeight * raw.LogitDiff.Value
                          + _ensemble.SimilarityWeight * request.SimilarityScore.Value
                          + _ensemble.Intercept;
                var confidence = Sigmoid(score);
                var isComparable = confidence > 0.5f;
                calibrated.Add(new PairResult(isComparable, confidence, raw.Reason, raw.LogitDiff));
            }
            else
            {
                calibrated.Add(raw);
            }
        }

        return calibrated;
    }

    public Task<bool> IsHealthy(CancellationToken ct = default)
    {
        return _modelRunner.IsHealthy(ct);
    }

    private static float Sigmoid(float x)
    {
        return 1.0f / (1.0f + MathF.Exp(-x));
    }
}
```

**Step 5: Update DI registrations in both Program.cs files**

In `AIOMarketMaker.Etl/Program.cs` (lines 212-213), replace:

```csharp
services.AddSingleton(classifierConfig);
services.AddSingleton<IVariantClassifierClient, OnnxVariantClassifier>();
```

With:

```csharp
services.AddSingleton(classifierConfig);
services.AddSingleton<VariantModelRunner>();
services.AddSingleton<IVariantModelRunner>(sp => sp.GetRequiredService<VariantModelRunner>());

var ensembleConfig = new EnsembleConfig(
    LogitWeight: configuration.GetValue<float>("VariantClassifier:Ensemble:LogitWeight", 0),
    SimilarityWeight: configuration.GetValue<float>("VariantClassifier:Ensemble:SimilarityWeight", 0),
    Intercept: configuration.GetValue<float>("VariantClassifier:Ensemble:Intercept", 0));
if (ensembleConfig.LogitWeight != 0)
{
    services.AddSingleton(ensembleConfig);
}
services.AddSingleton<IVariantClassifierClient, VariantClassifier>();
```

Same pattern in `AIOMarketMaker.Api/Program.cs` (lines 131-132), using `builder.Services` instead of `services`.

**Step 6: Add ensemble config to appsettings.json**

In `AIOMarketMaker.Api/appsettings.json`, add inside the `VariantClassifier` section (after line 32):

```json
"VariantClassifier": {
    "ModelPath": "E:/Dev/ml-training/variant-classifier/v10/onnx/model.onnx",
    "VocabPath": "E:/Dev/ml-training/variant-classifier/v10/onnx/vocab.json",
    "MergesPath": "E:/Dev/ml-training/variant-classifier/v10/onnx/merges.txt",
    "MaxLength": 512,
    "ConfidenceThreshold": 0.80,
    "EnableGptFallback": false,
    "Ensemble": {
        "LogitWeight": 2.4910,
        "SimilarityWeight": 0.4324,
        "Intercept": -2.6254
    }
}
```

**Step 7: Run tests**

Run: `dotnet build AIOMarketMaker.sln`
Expected: Build succeeds.

Run: `dotnet test AIOMarketMaker.Tests.Unit --filter "FullyQualifiedName~VariantClassifier_UnitTests" -v n`
Expected: All new tests pass.

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj`
Expected: All unit tests pass (new + existing).

**Step 8: Commit**

```bash
git add -A
git commit -m "feat: add VariantClassifier ensemble calibration layer"
```

---

### Task 4: Thread similarity scores through `ComparablesEtlService`

**Files:**
- Modify: `AIOMarketMaker.Core/Services/ComparablesEtlService.cs:189-196`

**Step 1: Update the `ClassifyPairRequest` construction**

In `ComparablesEtlService.cs`, change lines 189-196 where requests are built:

```csharp
// Before
var requests = batch.Select(p =>
{
    var a = allListingsById[p.CanonicalKey.Item1];
    var b = allListingsById[p.CanonicalKey.Item2];
    return new ClassifyPairRequest(
        a.Title ?? "", a.Description ?? "",
        b.Title ?? "", b.Description ?? "");
}).ToList();

// After
var requests = batch.Select(p =>
{
    var a = allListingsById[p.CanonicalKey.Item1];
    var b = allListingsById[p.CanonicalKey.Item2];
    return new ClassifyPairRequest(
        a.Title ?? "", a.Description ?? "",
        b.Title ?? "", b.Description ?? "",
        p.Score);
}).ToList();
```

This is the only production code change needed — `CandidatePair` already carries `Score` from the vector search.

**Step 2: Build and test**

Run: `dotnet build AIOMarketMaker.sln`
Expected: Build succeeds.

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj`
Expected: All tests pass.

**Step 3: Commit**

```bash
git add AIOMarketMaker.Core/Services/ComparablesEtlService.cs
git commit -m "feat: pass similarity scores through to classifier for ensemble calibration"
```

---

### Task 5: Update integration tests to use new class names

**Files:**
- Modify: `AIOMarketMaker.Tests.Integration/OnnxVariantClassifier_IntegrationTests.cs` (already renamed in Task 2)

**Step 1: Verify integration tests still pass with renamed classes**

The integration tests create `VariantModelRunner` directly (renamed from `OnnxVariantClassifier`). They should still work since `VariantModelRunner` has the same `Classify` method.

Run: `dotnet test AIOMarketMaker.Tests.Integration --filter "FullyQualifiedName~VariantModelRunner_IntegrationTests" -v n`
Expected: All integration tests pass (these use the real ONNX model so will only pass on a machine with the model file).

**Step 2: Add an integration test for the full ensemble flow**

Add to the integration test file:

```csharp
[Test]
public async Task Should_produce_calibrated_confidence_with_ensemble()
{
    var ensemble = new EnsembleConfig(
        LogitWeight: 2.4910f,
        SimilarityWeight: 0.4324f,
        Intercept: -2.6254f);

    var classifier = new VariantClassifier(
        _classifier, // VariantModelRunner instance from [OneTimeSetUp]
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
        Assert.That(result.Confidence, Is.GreaterThan(0.95f), "High logit diff + high similarity should produce high calibrated confidence");
        Assert.That(result.LogitDiff, Is.Not.Null, "LogitDiff should be populated from model runner");
    });
}
```

**Step 3: Commit**

```bash
git add -A
git commit -m "test: add ensemble integration test with real ONNX model"
```

---

### Task 6: Clean up and verify full test suite

**Step 1: Run the full unit test suite**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj -v n`
Expected: All tests pass.

**Step 2: Run the full solution build**

Run: `dotnet build AIOMarketMaker.sln`
Expected: Clean build with no warnings related to our changes.

**Step 3: Search for any remaining references to `OnnxVariantClassifier`**

Search the codebase for any lingering references to the old class name. Update any found (excluding git history, investigation docs, and the experiment Python script).

**Step 4: Final commit if any cleanup was needed**

```bash
git add -A
git commit -m "chore: clean up remaining OnnxVariantClassifier references"
```
