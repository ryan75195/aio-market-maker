# ONNX Variant Classifier Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the Python FastAPI variant classifier sidecar with native .NET ONNX Runtime inference using GPU acceleration.

**Architecture:** New `OnnxVariantClassifier` implements existing `IVariantClassifierClient` interface using ONNX Runtime + CodeGenTokenizer. Registered as singleton in DI, replaces the HTTP-based `VariantClassifierClient`. `ModelFirstComparisonService` continues to wrap it with GPT fallback — no changes needed upstream.

**Tech Stack:** Microsoft.ML.OnnxRuntime.Gpu 1.24.1, Microsoft.ML.Tokenizers 2.0.0 (CodeGenTokenizer), .NET 8.0, NUnit + Moq

**Design doc:** `docs/plans/2026-02-14-onnx-variant-classifier-design.md`

---

## Prerequisites

Before starting, ensure these model files exist at `AIOMarketMaker/models/variant-classifier/`:

```
models/variant-classifier/
  model.onnx    (1.36 GB - from E:/Dev/ml-training/variant-classifier/model_v6_onnx/)
  vocab.json    (from E:/Dev/ml-training/variant-classifier/model_v6_onnx/)
  merges.txt    (from E:/Dev/ml-training/variant-classifier/model_v6_onnx/)
```

---

### Task 1: Add NuGet packages and gitignore model files

**Files:**
- Modify: `AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
- Modify: `.gitignore`

**Step 1: Add NuGet packages to Core**

```bash
cd AIOMarketMaker/AIOMarketMaker.Core
dotnet add package Microsoft.ML.OnnxRuntime.Gpu --version 1.24.1
dotnet add package Microsoft.ML.Tokenizers --version 2.0.0
```

**Step 2: Add model directory to .gitignore**

Append to `AIOMarketMaker/.gitignore`:

```
# ONNX model files (too large for git)
models/
```

**Step 3: Create the model directory and copy files**

```bash
mkdir -p AIOMarketMaker/models/variant-classifier
cp E:/Dev/ml-training/variant-classifier/model_v6_onnx/model.onnx AIOMarketMaker/models/variant-classifier/
cp E:/Dev/ml-training/variant-classifier/model_v6_onnx/vocab.json AIOMarketMaker/models/variant-classifier/
cp E:/Dev/ml-training/variant-classifier/model_v6_onnx/merges.txt AIOMarketMaker/models/variant-classifier/
```

**Step 4: Verify build**

```bash
cd AIOMarketMaker
dotnet build AIOMarketMaker.sln
```

Expected: Build succeeds with 0 errors.

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/AIOMarketMaker.Core.csproj .gitignore
git commit -m "chore: add ONNX Runtime GPU and ML.Tokenizers packages, gitignore model files"
```

---

### Task 2: Write OnnxVariantClassifier with tokenization tests

**Files:**
- Create: `AIOMarketMaker.Core/Services/OnnxVariantClassifier.cs`
- Create: `AIOMarketMaker.Tests/Unit/Services/OnnxVariantClassifier_UnitTests.cs`

**Step 1: Write the failing tokenization tests**

These tests verify the tokenizer produces correct RoBERTa token sequences without needing the ONNX model file.

Create `AIOMarketMaker.Tests/Unit/Services/OnnxVariantClassifier_UnitTests.cs`:

```csharp
using AIOMarketMaker.Core.Services;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class OnnxVariantClassifier_UnitTests
{
    [Test]
    public void Should_tokenize_pair_with_correct_roberta_format()
    {
        // Reference from Python: tokenizer("Dyson V15 Detect Absolute | Cordless vacuum cleaner with laser dust detection",
        //   "Dyson V15 Detect Absolute | Brand new cordless vacuum with laser illuminate technology")
        // Produces: [0, 495, 20216, 468, 996, 18129, 42001, 1721, 11931, 1672, 15702, 16126, 19, 13443, 8402, 12673,
        //            2, 2, 495, 20216, 468, 996, 18129, 42001, 1721, 7379, 92, 13051, 1672, 15702, 16126, 19, 22990,
        //            26201, 1593, 2]
        var vocabPath = FindModelFile("vocab.json");
        var mergesPath = FindModelFile("merges.txt");

        var (inputIds, attentionMask) = OnnxVariantClassifier.TokenizePair(
            vocabPath, mergesPath,
            "Dyson V15 Detect Absolute | Cordless vacuum cleaner with laser dust detection",
            "Dyson V15 Detect Absolute | Brand new cordless vacuum with laser illuminate technology",
            maxLength: 256);

        Assert.Multiple(() =>
        {
            // BOS token
            Assert.That(inputIds[0], Is.EqualTo(0), "First token should be <s> (BOS=0)");
            // EOS separator pair at position 16-17
            Assert.That(inputIds[16], Is.EqualTo(2), "Should have </s> separator");
            Assert.That(inputIds[17], Is.EqualTo(2), "Should have </s></s> double separator");
            // Total non-pad tokens = 35 (verified from Python)
            Assert.That(attentionMask.Take(35).All(m => m == 1), Is.True, "First 35 should be attended");
            Assert.That(attentionMask.Skip(35).All(m => m == 0), Is.True, "Rest should be padding");
            // Padding token
            Assert.That(inputIds[35], Is.EqualTo(1), "Padding should be <pad> (PAD=1)");
            // Array lengths
            Assert.That(inputIds, Has.Length.EqualTo(256));
            Assert.That(attentionMask, Has.Length.EqualTo(256));
        });

        // Exact token match for first 30 tokens (verified against Python)
        long[] pythonRef = [0, 495, 20216, 468, 996, 18129, 42001, 1721, 11931, 1672,
            15702, 16126, 19, 13443, 8402, 12673, 2, 2, 495, 20216,
            468, 996, 18129, 42001, 1721, 7379, 92, 13051, 1672, 15702];
        Assert.That(inputIds.Take(30).ToArray(), Is.EqualTo(pythonRef),
            "First 30 tokens should match Python reference exactly");
    }

    [Test]
    public void Should_truncate_long_inputs_to_max_length()
    {
        var vocabPath = FindModelFile("vocab.json");
        var mergesPath = FindModelFile("merges.txt");

        // Create very long text that would exceed 256 tokens
        var longText = string.Join(" ", Enumerable.Repeat("premium quality professional grade", 50));

        var (inputIds, attentionMask) = OnnxVariantClassifier.TokenizePair(
            vocabPath, mergesPath, longText, longText, maxLength: 64);

        Assert.Multiple(() =>
        {
            Assert.That(inputIds, Has.Length.EqualTo(64));
            Assert.That(attentionMask, Has.Length.EqualTo(64));
            Assert.That(attentionMask.All(m => m == 1), Is.True, "All tokens should be attended (no padding after truncation)");
        });
    }

    [Test]
    public void Should_apply_softmax_correctly()
    {
        // Reference from Python ONNX: logits [-2.284080, 3.348258] -> probs [0.003567, 0.996433]
        var probs = OnnxVariantClassifier.Softmax([-2.284080f, 3.348258f]);

        Assert.Multiple(() =>
        {
            Assert.That(probs[0], Is.EqualTo(0.003567f).Within(0.0001f));
            Assert.That(probs[1], Is.EqualTo(0.996433f).Within(0.0001f));
            Assert.That(probs.Sum(), Is.EqualTo(1.0f).Within(0.0001f));
        });
    }

    private static string FindModelFile(string filename)
    {
        // Walk up from test bin directory to find models/variant-classifier/
        var dir = TestContext.CurrentContext.TestDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "models", "variant-classifier", filename);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir)!;
        }
        Assert.Ignore($"Model file {filename} not found — skipping tokenizer test");
        return null!; // unreachable
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~OnnxVariantClassifier_UnitTests" -v minimal
```

Expected: FAIL — `OnnxVariantClassifier` class doesn't exist yet.

**Step 3: Write the OnnxVariantClassifier implementation**

Create `AIOMarketMaker.Core/Services/OnnxVariantClassifier.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace AIOMarketMaker.Core.Services;

public record OnnxClassifierConfig(
    string ModelPath,
    string VocabPath,
    string MergesPath,
    int MaxLength = 256,
    float ConfidenceThreshold = 0.80f);

public class OnnxVariantClassifier : IVariantClassifierClient, IDisposable
{
    private const long BosId = 0;  // <s>
    private const long EosId = 2;  // </s>
    private const long PadId = 1;  // <pad>

    private readonly InferenceSession _session;
    private readonly CodeGenTokenizer _tokenizer;
    private readonly int _maxLength;
    private readonly float _confidenceThreshold;
    private readonly ILogger<OnnxVariantClassifier> _logger;
    private readonly bool _isHealthy;

    public OnnxVariantClassifier(OnnxClassifierConfig config, ILogger<OnnxVariantClassifier> logger)
    {
        _maxLength = config.MaxLength;
        _confidenceThreshold = config.ConfidenceThreshold;
        _logger = logger;

        if (!File.Exists(config.ModelPath))
        {
            throw new FileNotFoundException(
                $"ONNX model not found at '{config.ModelPath}'. See docs/gpu-setup.md for setup instructions.",
                config.ModelPath);
        }

        // Load tokenizer
        using var vocabStream = File.OpenRead(config.VocabPath);
        using var mergesStream = File.OpenRead(config.MergesPath);
        _tokenizer = CodeGenTokenizer.Create(vocabStream, mergesStream,
            addPrefixSpace: false, addBeginOfSentence: false, addEndOfSentence: false);

        // Load ONNX model — try CUDA first, fall back to CPU
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        try
        {
            sessionOptions.AppendExecutionProvider_CUDA(0);
            _logger.LogInformation("ONNX variant classifier using CUDA GPU");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("CUDA not available ({Message}), falling back to CPU. " +
                "GPU inference is ~60x faster. See docs/gpu-setup.md", ex.Message);
            sessionOptions.IntraOpNumThreads = Environment.ProcessorCount;
        }

        _session = new InferenceSession(config.ModelPath, sessionOptions);
        _isHealthy = true;
        _logger.LogInformation("ONNX variant classifier loaded from {ModelPath}", config.ModelPath);
    }

    public Task<IReadOnlyList<PairResult>> Classify(
        IEnumerable<ClassifyPairRequest> pairs,
        CancellationToken ct = default)
    {
        var pairList = pairs.ToList();
        var results = new List<PairResult>(pairList.Count);

        foreach (var pair in pairList)
        {
            ct.ThrowIfCancellationRequested();

            var textA = $"{pair.TitleA} | {pair.DescriptionA}";
            var textB = $"{pair.TitleB} | {pair.DescriptionB}";

            var (inputIds, attentionMask) = TokenizePairInternal(textA, textB);
            var logits = RunInference(inputIds, attentionMask);
            var probs = Softmax(logits);

            var isComparable = probs[1] > probs[0];
            var confidence = probs.Max();
            var needsFallback = confidence < _confidenceThreshold;

            results.Add(new PairResult(isComparable, confidence, needsFallback));
        }

        return Task.FromResult<IReadOnlyList<PairResult>>(results);
    }

    public Task<bool> IsHealthy(CancellationToken ct = default)
    {
        return Task.FromResult(_isHealthy);
    }

    /// <summary>
    /// Static tokenization method exposed for unit testing without requiring a model file.
    /// </summary>
    public static (long[] InputIds, long[] AttentionMask) TokenizePair(
        string vocabPath, string mergesPath, string textA, string textB, int maxLength)
    {
        using var vocabStream = File.OpenRead(vocabPath);
        using var mergesStream = File.OpenRead(mergesPath);
        var tokenizer = CodeGenTokenizer.Create(vocabStream, mergesStream,
            addPrefixSpace: false, addBeginOfSentence: false, addEndOfSentence: false);

        return TokenizePairCore(tokenizer, textA, textB, maxLength);
    }

    public static float[] Softmax(float[] logits)
    {
        var max = logits.Max();
        var exps = logits.Select(l => MathF.Exp(l - max)).ToArray();
        var sum = exps.Sum();
        return exps.Select(e => e / sum).ToArray();
    }

    private (long[] InputIds, long[] AttentionMask) TokenizePairInternal(string textA, string textB)
    {
        return TokenizePairCore(_tokenizer, textA, textB, _maxLength);
    }

    private static (long[] InputIds, long[] AttentionMask) TokenizePairCore(
        CodeGenTokenizer tokenizer, string textA, string textB, int maxLength)
    {
        var idsA = tokenizer.EncodeToIds(textA);
        var idsB = tokenizer.EncodeToIds(textB);

        // RoBERTa sentence pair format: <s> tokens_a </s></s> tokens_b </s>
        var combined = new List<long>(maxLength);
        combined.Add(BosId);
        foreach (var id in idsA)
        {
            combined.Add(id);
        }
        combined.Add(EosId);
        combined.Add(EosId);
        foreach (var id in idsB)
        {
            combined.Add(id);
        }
        combined.Add(EosId);

        // Truncate if needed
        if (combined.Count > maxLength)
        {
            combined.RemoveRange(maxLength, combined.Count - maxLength);
        }

        var realLength = combined.Count;

        // Pad to maxLength
        while (combined.Count < maxLength)
        {
            combined.Add(PadId);
        }

        // Build attention mask
        var mask = new long[maxLength];
        for (var i = 0; i < realLength; i++)
        {
            mask[i] = 1;
        }

        return (combined.ToArray(), mask);
    }

    private float[] RunInference(long[] inputIds, long[] attentionMask)
    {
        var inputTensor = new DenseTensor<long>(inputIds, [1, _maxLength]);
        var maskTensor = new DenseTensor<long>(attentionMask, [1, _maxLength]);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor)
        };

        using var results = _session.Run(inputs);
        return results.First().AsEnumerable<float>().ToArray();
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~OnnxVariantClassifier_UnitTests" -v minimal
```

Expected: 3 tests pass (or skipped if model files not present).

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/OnnxVariantClassifier.cs AIOMarketMaker.Tests/Unit/Services/OnnxVariantClassifier_UnitTests.cs
git commit -m "feat: add OnnxVariantClassifier with tokenization and inference"
```

---

### Task 3: Replace HTTP client with ONNX in DI wiring

**Files:**
- Modify: `AIOMarketMaker.Api/Program.cs:114-120`
- Modify: `AIOMarketMaker.Api/appsettings.json`

**Step 1: Update appsettings.json**

Replace the existing `VariantClassifier` section:

```json
"VariantClassifier": {
    "ModelPath": "models/variant-classifier/model.onnx",
    "VocabPath": "models/variant-classifier/vocab.json",
    "MergesPath": "models/variant-classifier/merges.txt",
    "ConfidenceThreshold": 0.80
}
```

**Step 2: Update Program.cs DI wiring**

Replace lines 114-120 (the HTTP client registration):

```csharp
// Variant classifier (Python model service)
var classifierBaseUrl = configuration.GetValue<string>("VariantClassifier:BaseUrl") ?? "http://localhost:8010";
builder.Services.AddHttpClient<IVariantClassifierClient, VariantClassifierClient>(client =>
{
    client.BaseAddress = new Uri(classifierBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(60);
});
```

With:

```csharp
// Variant classifier (local ONNX model)
var classifierConfig = new OnnxClassifierConfig(
    ModelPath: configuration.GetValue<string>("VariantClassifier:ModelPath") ?? "models/variant-classifier/model.onnx",
    VocabPath: configuration.GetValue<string>("VariantClassifier:VocabPath") ?? "models/variant-classifier/vocab.json",
    MergesPath: configuration.GetValue<string>("VariantClassifier:MergesPath") ?? "models/variant-classifier/merges.txt",
    ConfidenceThreshold: configuration.GetValue<float>("VariantClassifier:ConfidenceThreshold", 0.80f));
builder.Services.AddSingleton(classifierConfig);
builder.Services.AddSingleton<IVariantClassifierClient, OnnxVariantClassifier>();
```

Also add using statement at top of Program.cs if not already present:

```csharp
using AIOMarketMaker.Core.Services;
```

**Step 3: Verify build**

```bash
dotnet build AIOMarketMaker.sln
```

Expected: 0 errors. (The `VariantClassifierClient` class still exists but is no longer registered.)

**Step 4: Run all unit tests**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter Category=Unit -v minimal
```

Expected: All unit tests pass (ModelFirstComparisonService tests mock IVariantClassifierClient, so unaffected).

**Step 5: Commit**

```bash
git add AIOMarketMaker.Api/Program.cs AIOMarketMaker.Api/appsettings.json
git commit -m "feat: wire OnnxVariantClassifier into DI, replacing HTTP client"
```

---

### Task 4: Delete HTTP client and Python sidecar

**Files:**
- Delete: `AIOMarketMaker.Core/Services/VariantClassifierClient.cs` (the class — keep the records and interface)
- Delete: `AIOMarketMaker/variant-classifier-service/` (entire directory)
- Modify: `AIOMarketMaker.Tests/Unit/Services/VariantClassifierClient_UnitTests.cs` (delete file)

**Step 1: Move shared records and interface out of VariantClassifierClient.cs**

The file currently contains the interface `IVariantClassifierClient`, records (`ClassifyPairRequest`, `PairResult`, `ClassifyResponse`, `VariantClassifierConfig`), and the HTTP class. The interface and shared records must be preserved since they're used by `OnnxVariantClassifier` and `ModelFirstComparisonService`.

Move the interface and shared records to `OnnxVariantClassifier.cs` (above the class, per coding standards). Then delete `VariantClassifierClient.cs`.

Records to keep (move to `OnnxVariantClassifier.cs`):
- `ClassifyPairRequest`
- `PairResult`
- `ClassifyResponse` (keep for now, may still be used)
- `IVariantClassifierClient`

Records to drop:
- `VariantClassifierConfig` (replaced by `OnnxClassifierConfig`)
- `VariantClassifierClient` class

**Step 2: Delete the HTTP client unit tests**

Delete `AIOMarketMaker.Tests/Unit/Services/VariantClassifierClient_UnitTests.cs` entirely — it tested the HTTP client that no longer exists.

**Step 3: Delete Python sidecar**

```bash
rm -rf AIOMarketMaker/variant-classifier-service/
```

**Step 4: Verify build and tests**

```bash
dotnet build AIOMarketMaker.sln
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter Category=Unit -v minimal
```

Expected: Build succeeds. All remaining unit tests pass.

**Step 5: Commit**

```bash
git add -A
git commit -m "refactor: remove HTTP variant classifier client and Python sidecar"
```

---

### Task 5: Write GPU setup documentation

**Files:**
- Create: `AIOMarketMaker/docs/gpu-setup.md`

**Step 1: Write the setup guide**

Create `AIOMarketMaker/docs/gpu-setup.md`:

```markdown
# GPU Setup for ONNX Variant Classifier

The variant classifier runs a RoBERTa-large cross-encoder via ONNX Runtime. It works on CPU but is ~60x faster with CUDA GPU acceleration.

## Performance

| Mode | Latency | Throughput |
|------|---------|------------|
| GPU (CUDA) | ~13 ms/pair | ~80 pairs/sec |
| CPU | ~700 ms/pair | ~1.4 pairs/sec |

## Requirements

### 1. NVIDIA GPU

Any NVIDIA GPU with compute capability 7.0+ (RTX 20xx or newer). Tested on RTX 5070 Ti (16GB VRAM).

### 2. NVIDIA Driver

Version 525.60 or later. Check: `nvidia-smi`

### 3. CUDA Toolkit 12.x

Download from: https://developer.nvidia.com/cuda-12-4-0-download-archive

Install the "Runtime" components (cuBLAS, cuDNN, etc.). After installation, verify:

```bash
nvcc --version
# Should show CUDA 12.x
```

### 4. cuDNN 9.x

Download from: https://developer.nvidia.com/cudnn-downloads

After installation, add the bin directory to PATH:

```
C:\Program Files\NVIDIA\CUDNN\v9.x\bin
```

Verify:

```bash
where cudnn64_9.dll
# Should show the path
```

### 5. Model Files

Copy the ONNX model and tokenizer files to `AIOMarketMaker/models/variant-classifier/`:

```
models/variant-classifier/
  model.onnx    (1.36 GB)
  vocab.json
  merges.txt
```

Source: `E:/Dev/ml-training/variant-classifier/model_v6_onnx/`

## Verification

Start the API and check the logs for:

```
ONNX variant classifier using CUDA GPU
ONNX variant classifier loaded from models/variant-classifier/model.onnx
```

If you see "CUDA not available, falling back to CPU", check that CUDA Toolkit and cuDNN are installed and on PATH.

## Troubleshooting

**"cublasLt64_12.dll missing"** — CUDA Toolkit 12.x not installed or not on PATH.

**"cudnn64_9.dll missing"** — cuDNN 9.x not installed or bin directory not on PATH.

**"CUDA not available"** — Driver too old, or GPU not CUDA-capable.

**First inference takes ~60-80 seconds** — This is normal. CUDA compiles kernels on first use. Subsequent inferences are fast (~13ms).
```

**Step 2: Commit**

```bash
git add docs/gpu-setup.md
git commit -m "docs: add GPU setup guide for ONNX variant classifier"
```

---

### Task 6: Integration smoke test

**Step 1: Start the API**

```bash
cd AIOMarketMaker/AIOMarketMaker.Api
dotnet run
```

Expected in logs:
- `ONNX variant classifier using CUDA GPU` (or CPU fallback warning)
- `ONNX variant classifier loaded from models/variant-classifier/model.onnx`
- No startup crashes

**Step 2: Verify health endpoint**

```bash
curl http://localhost:5000/api/health
```

Expected: `{"status":"healthy"}`

**Step 3: Stop the API**

Ctrl+C to stop.

**Step 4: Final commit with any fixes**

If any issues were found and fixed during smoke test, commit them.

---

## Summary of Changes

| Action | File |
|--------|------|
| Add packages | `AIOMarketMaker.Core/AIOMarketMaker.Core.csproj` |
| Add gitignore | `.gitignore` |
| Create | `AIOMarketMaker.Core/Services/OnnxVariantClassifier.cs` |
| Create | `AIOMarketMaker.Tests/Unit/Services/OnnxVariantClassifier_UnitTests.cs` |
| Modify | `AIOMarketMaker.Api/Program.cs` (DI wiring) |
| Modify | `AIOMarketMaker.Api/appsettings.json` (config) |
| Delete | `AIOMarketMaker.Core/Services/VariantClassifierClient.cs` |
| Delete | `AIOMarketMaker.Tests/Unit/Services/VariantClassifierClient_UnitTests.cs` |
| Delete | `AIOMarketMaker/variant-classifier-service/` (Python sidecar) |
| Create | `AIOMarketMaker/docs/gpu-setup.md` |
