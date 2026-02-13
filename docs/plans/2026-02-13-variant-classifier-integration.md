# Variant Classifier Integration Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace GPT calls in `ListingComparisonService` with a local roberta-large model served via FastAPI, falling back to GPT for low-confidence pairs.

**Architecture:** A Python FastAPI sidecar serves the trained roberta-large model on port 8010. A .NET `VariantClassifierClient` calls it via HTTP. A new `ModelFirstComparisonService` implements `IListingComparisonService` — it calls the model first, and if confidence < 0.80, falls back to GPT. DI registration swaps the implementation.

**Tech Stack:** Python 3.12, FastAPI, uvicorn, transformers, torch. .NET 8.0 HttpClient.

---

## Component Overview

```
ComparablesEtlService
  └─ IListingComparisonService.Compare(a, b)
       └─ ModelFirstComparisonService (NEW)
            ├─ VariantClassifierClient.Classify(batch) → POST http://localhost:8010/classify
            │    └─ Python FastAPI → roberta-large inference
            ├─ if confidence >= 0.80 → return verdict
            └─ if confidence < 0.80 → ListingComparisonService.Compare(a, b) [GPT fallback]
```

## Files

| Action | Path | Purpose |
|--------|------|---------|
| Create | `AIOMarketMaker/variant-classifier-service/main.py` | FastAPI app serving model |
| Create | `AIOMarketMaker/variant-classifier-service/requirements.txt` | Python dependencies |
| Create | `AIOMarketMaker.Core/Services/VariantClassifierClient.cs` | HTTP client for Python service |
| Create | `AIOMarketMaker.Core/Services/ModelFirstComparisonService.cs` | IListingComparisonService impl |
| Modify | `AIOMarketMaker.Api/Program.cs:115-118` | Swap DI registration |
| Modify | `AIOMarketMaker.Api/appsettings.json` | Add VariantClassifier config |
| Create | `AIOMarketMaker.Tests/Unit/Services/ModelFirstComparisonService_UnitTests.cs` | Unit tests |
| Create | `AIOMarketMaker.Tests/Unit/Services/VariantClassifierClient_UnitTests.cs` | Unit tests |

---

### Task 1: Python FastAPI Service

**Files:**
- Create: `AIOMarketMaker/variant-classifier-service/main.py`
- Create: `AIOMarketMaker/variant-classifier-service/requirements.txt`

**Step 1: Create requirements.txt**

```
fastapi==0.115.0
uvicorn[standard]==0.30.0
torch>=2.0.0
transformers>=4.40.0
```

**Step 2: Create main.py**

```python
"""Variant classifier inference service.

Serves a trained roberta-large cross-encoder model via FastAPI.
Accepts batches of listing pairs and returns same/different verdicts with confidence scores.

Usage:
    uvicorn main:app --port 8010
    # or with auto-reload for development:
    uvicorn main:app --port 8010 --reload
"""

import os
import logging
from contextlib import asynccontextmanager

import torch
from fastapi import FastAPI
from pydantic import BaseModel
from transformers import AutoTokenizer, AutoModelForSequenceClassification

logger = logging.getLogger("variant-classifier")

MODEL_PATH = os.environ.get(
    "MODEL_PATH",
    "E:/Dev/ml-training/variant-classifier/model_v6_lr1e5",
)
MAX_LENGTH = int(os.environ.get("MAX_LENGTH", "256"))
CONFIDENCE_THRESHOLD = float(os.environ.get("CONFIDENCE_THRESHOLD", "0.80"))

model = None
tokenizer = None


class ListingPair(BaseModel):
    title_a: str
    description_a: str
    title_b: str
    description_b: str


class ClassifyRequest(BaseModel):
    pairs: list[ListingPair]


class PairResult(BaseModel):
    is_comparable: bool
    confidence: float
    needs_fallback: bool


class ClassifyResponse(BaseModel):
    results: list[PairResult]


@asynccontextmanager
async def lifespan(app: FastAPI):
    global model, tokenizer
    logger.info("Loading model from %s", MODEL_PATH)
    tokenizer = AutoTokenizer.from_pretrained(MODEL_PATH)
    model = AutoModelForSequenceClassification.from_pretrained(MODEL_PATH)
    model.eval()
    logger.info("Model loaded successfully")
    yield


app = FastAPI(title="Variant Classifier", lifespan=lifespan)


@app.get("/health")
def health():
    return {"status": "healthy", "model_loaded": model is not None}


@app.post("/classify", response_model=ClassifyResponse)
def classify(request: ClassifyRequest):
    texts_a = [f"{p.title_a} | {p.description_a}" for p in request.pairs]
    texts_b = [f"{p.title_b} | {p.description_b}" for p in request.pairs]

    inputs = tokenizer(
        texts_a,
        texts_b,
        return_tensors="pt",
        max_length=MAX_LENGTH,
        truncation=True,
        padding=True,
    )

    with torch.no_grad():
        logits = model(**inputs).logits
        probs = torch.softmax(logits, dim=-1)

    results = []
    for i in range(len(request.pairs)):
        p_same = probs[i][1].item()
        p_different = probs[i][0].item()
        confidence = max(p_same, p_different)
        is_comparable = p_same > p_different

        results.append(
            PairResult(
                is_comparable=is_comparable,
                confidence=round(confidence, 4),
                needs_fallback=confidence < CONFIDENCE_THRESHOLD,
            )
        )

    return ClassifyResponse(results=results)


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="0.0.0.0", port=8010)
```

**Step 3: Verify it starts and serves predictions**

```bash
cd AIOMarketMaker/variant-classifier-service
pip install -r requirements.txt
python main.py
# In another terminal:
curl http://localhost:8010/health
curl -X POST http://localhost:8010/classify -H "Content-Type: application/json" -d "{\"pairs\": [{\"title_a\": \"Dyson V15 Detect Absolute\", \"description_a\": \"Cordless vacuum\", \"title_b\": \"Dyson V15 Detect Extra\", \"description_b\": \"Cordless vacuum blue\"}]}"
```

Expected: `{"results": [{"is_comparable": false, "confidence": 0.997, "needs_fallback": false}]}`

**Step 4: Commit**

```bash
git add variant-classifier-service/
git commit -m "feat: add variant classifier FastAPI inference service"
```

---

### Task 2: VariantClassifierClient (.NET HTTP client)

**Files:**
- Create: `AIOMarketMaker.Core/Services/VariantClassifierClient.cs`
- Create: `AIOMarketMaker.Tests/Unit/Services/VariantClassifierClient_UnitTests.cs`

**Step 1: Write the failing tests**

```csharp
// VariantClassifierClient_UnitTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class VariantClassifierClient_UnitTests
{
    private Mock<HttpMessageHandler> _handlerMock = null!;
    private VariantClassifierClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _handlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_handlerMock.Object)
        {
            BaseAddress = new Uri("http://localhost:8010")
        };
        _client = new VariantClassifierClient(
            httpClient,
            Mock.Of<ILogger<VariantClassifierClient>>());
    }

    [Test]
    public async Task Should_return_verdict_when_model_responds()
    {
        var response = new ClassifyResponse(
            new[] { new PairResult(true, 0.95f, false) });

        SetupResponse(HttpStatusCode.OK, response);

        var results = await _client.Classify(new[]
        {
            new ClassifyPairRequest("Title A", "Desc A", "Title B", "Desc B")
        });

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].IsComparable, Is.True);
            Assert.That(results[0].Confidence, Is.EqualTo(0.95f).Within(0.01));
            Assert.That(results[0].NeedsFallback, Is.False);
        });
    }

    [Test]
    public async Task Should_return_fallback_needed_when_confidence_low()
    {
        var response = new ClassifyResponse(
            new[] { new PairResult(false, 0.62f, true) });

        SetupResponse(HttpStatusCode.OK, response);

        var results = await _client.Classify(new[]
        {
            new ClassifyPairRequest("Title A", "Desc A", "Title B", "Desc B")
        });

        Assert.That(results[0].NeedsFallback, Is.True);
    }

    [Test]
    public void Should_throw_when_service_unavailable()
    {
        _handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        Assert.ThrowsAsync<HttpRequestException>(async () =>
            await _client.Classify(new[]
            {
                new ClassifyPairRequest("A", "A", "B", "B")
            }));
    }

    private void SetupResponse(HttpStatusCode status, ClassifyResponse body)
    {
        _handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = status,
                Content = JsonContent.Create(body)
            });
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test AIOMarketMaker.Tests --filter "FullyQualifiedName~VariantClassifierClient_UnitTests" -v n
```

Expected: Build error — `VariantClassifierClient` does not exist.

**Step 3: Write the implementation**

```csharp
// VariantClassifierClient.cs
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services;

public record VariantClassifierConfig(string BaseUrl = "http://localhost:8010");

public record ClassifyPairRequest(
    string TitleA,
    string DescriptionA,
    string TitleB,
    string DescriptionB);

public record PairResult(bool IsComparable, float Confidence, bool NeedsFallback);

public record ClassifyResponse(IReadOnlyList<PairResult> Results);

public interface IVariantClassifierClient
{
    Task<IReadOnlyList<PairResult>> Classify(
        IEnumerable<ClassifyPairRequest> pairs,
        CancellationToken ct = default);

    Task<bool> IsHealthy(CancellationToken ct = default);
}

public class VariantClassifierClient : IVariantClassifierClient
{
    private readonly HttpClient _http;
    private readonly ILogger<VariantClassifierClient> _logger;

    public VariantClassifierClient(HttpClient http, ILogger<VariantClassifierClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PairResult>> Classify(
        IEnumerable<ClassifyPairRequest> pairs,
        CancellationToken ct = default)
    {
        var payload = new
        {
            pairs = pairs.Select(p => new
            {
                title_a = p.TitleA,
                description_a = p.DescriptionA,
                title_b = p.TitleB,
                description_b = p.DescriptionB
            })
        };

        var response = await _http.PostAsJsonAsync("classify", payload, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ClassifyResponse>(ct);
        return result?.Results ?? Array.Empty<PairResult>();
    }

    public async Task<bool> IsHealthy(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test AIOMarketMaker.Tests --filter "FullyQualifiedName~VariantClassifierClient_UnitTests" -v n
```

Expected: All 3 tests PASS.

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/VariantClassifierClient.cs AIOMarketMaker.Tests/Unit/Services/VariantClassifierClient_UnitTests.cs
git commit -m "feat: add VariantClassifierClient HTTP wrapper for Python model service"
```

---

### Task 3: ModelFirstComparisonService

**Files:**
- Create: `AIOMarketMaker.Core/Services/ModelFirstComparisonService.cs`
- Create: `AIOMarketMaker.Tests/Unit/Services/ModelFirstComparisonService_UnitTests.cs`

**Step 1: Write the failing tests**

```csharp
// ModelFirstComparisonService_UnitTests.cs
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ModelFirstComparisonService_UnitTests
{
    private Mock<IVariantClassifierClient> _classifierMock = null!;
    private Mock<IListingComparisonService> _gptFallbackMock = null!;
    private ModelFirstComparisonService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _classifierMock = new Mock<IVariantClassifierClient>();
        _gptFallbackMock = new Mock<IListingComparisonService>();
        _service = new ModelFirstComparisonService(
            _classifierMock.Object,
            _gptFallbackMock.Object,
            Mock.Of<ILogger<ModelFirstComparisonService>>());
    }

    private static Listing CreateListing(int id, string title, string? desc = null) =>
        new()
        {
            Id = id, ListingId = id.ToString(), Title = title,
            Description = desc ?? $"Desc for {title}", ScrapeJobId = 1
        };

    [Test]
    public async Task Should_return_model_verdict_when_confident()
    {
        _classifierMock.Setup(c => c.Classify(It.IsAny<IEnumerable<ClassifyPairRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PairResult(true, 0.95f, false) });

        var result = await _service.Compare(
            CreateListing(1, "Dyson V15 Detect Absolute"),
            CreateListing(2, "Dyson V15 Detect Absolute New"));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.True);
            Assert.That(result.Explanation, Does.Contain("0.95"));
        });
        _gptFallbackMock.Verify(g => g.Compare(It.IsAny<Listing>(), It.IsAny<Listing>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Should_fallback_to_gpt_when_model_uncertain()
    {
        _classifierMock.Setup(c => c.Classify(It.IsAny<IEnumerable<ClassifyPairRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PairResult(false, 0.62f, true) });

        _gptFallbackMock.Setup(g => g.Compare(It.IsAny<Listing>(), It.IsAny<Listing>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComparableVerdict(true, "GPT says same product"));

        var result = await _service.Compare(
            CreateListing(1, "Dyson V15 Detect"),
            CreateListing(2, "Dyson V15 Detect Iron"));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.True);
            Assert.That(result.Explanation, Is.EqualTo("GPT says same product"));
        });
        _gptFallbackMock.Verify(g => g.Compare(It.IsAny<Listing>(), It.IsAny<Listing>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Should_fallback_to_gpt_when_model_service_unavailable()
    {
        _classifierMock.Setup(c => c.Classify(It.IsAny<IEnumerable<ClassifyPairRequest>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        _gptFallbackMock.Setup(g => g.Compare(It.IsAny<Listing>(), It.IsAny<Listing>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComparableVerdict(false, "GPT fallback"));

        var result = await _service.Compare(
            CreateListing(1, "Item A"),
            CreateListing(2, "Item B"));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComparable, Is.False);
            Assert.That(result.Explanation, Is.EqualTo("GPT fallback"));
        });
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test AIOMarketMaker.Tests --filter "FullyQualifiedName~ModelFirstComparisonService_UnitTests" -v n
```

Expected: Build error — `ModelFirstComparisonService` does not exist.

**Step 3: Write the implementation**

```csharp
// ModelFirstComparisonService.cs
using AIOMarketMaker.Core.Data.Models;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services;

public class ModelFirstComparisonService : IListingComparisonService
{
    private readonly IVariantClassifierClient _classifier;
    private readonly IListingComparisonService _gptFallback;
    private readonly ILogger<ModelFirstComparisonService> _logger;

    public ModelFirstComparisonService(
        IVariantClassifierClient classifier,
        IListingComparisonService gptFallback,
        ILogger<ModelFirstComparisonService> logger)
    {
        _classifier = classifier;
        _gptFallback = gptFallback;
        _logger = logger;
    }

    public async Task<ComparableVerdict> Compare(Listing a, Listing b, CancellationToken ct = default)
    {
        try
        {
            var results = await _classifier.Classify(new[]
            {
                new ClassifyPairRequest(
                    a.Title ?? "", a.Description ?? "",
                    b.Title ?? "", b.Description ?? "")
            }, ct);

            var result = results[0];

            if (!result.NeedsFallback)
            {
                _logger.LogDebug(
                    "Model verdict for ({IdA}, {IdB}): {Verdict} (confidence={Confidence:F3})",
                    a.Id, b.Id, result.IsComparable ? "comparable" : "different", result.Confidence);

                return new ComparableVerdict(
                    result.IsComparable,
                    $"Model: confidence={result.Confidence:F3}");
            }

            _logger.LogDebug(
                "Model uncertain for ({IdA}, {IdB}), confidence={Confidence:F3} — falling back to GPT",
                a.Id, b.Id, result.Confidence);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Model service unavailable, falling back to GPT for ({IdA}, {IdB})", a.Id, b.Id);
        }

        return await _gptFallback.Compare(a, b, ct);
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test AIOMarketMaker.Tests --filter "FullyQualifiedName~ModelFirstComparisonService_UnitTests" -v n
```

Expected: All 3 tests PASS.

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/ModelFirstComparisonService.cs AIOMarketMaker.Tests/Unit/Services/ModelFirstComparisonService_UnitTests.cs
git commit -m "feat: add ModelFirstComparisonService with GPT fallback"
```

---

### Task 4: DI Wiring

**Files:**
- Modify: `AIOMarketMaker.Api/Program.cs:115-118`
- Modify: `AIOMarketMaker.Api/appsettings.json`

**Step 1: Add config to appsettings.json**

Add to `appsettings.json`:
```json
"VariantClassifier": {
    "BaseUrl": "http://localhost:8010"
}
```

**Step 2: Update DI registration in Program.cs**

Replace the current `IListingComparisonService` registration block with:

```csharp
// Variant classifier (Python model service)
var classifierBaseUrl = configuration.GetValue<string>("VariantClassifier:BaseUrl") ?? "http://localhost:8010";
builder.Services.AddHttpClient<IVariantClassifierClient, VariantClassifierClient>(client =>
{
    client.BaseAddress = new Uri(classifierBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(60);
});

// GPT comparison (fallback)
var openAiKey = configuration.GetValue<string>("OpenAi:ApiKey") ?? "";
var chatModel = configuration.GetValue<string>("OpenAi:ChatModel") ?? "gpt-5-nano";
var comparisonConfig = new ListingComparisonConfig(openAiKey, chatModel);
builder.Services.AddSingleton(comparisonConfig);
builder.Services.AddSingleton<ListingComparisonService>();

// Model-first with GPT fallback
builder.Services.AddSingleton<IListingComparisonService>(sp =>
    new ModelFirstComparisonService(
        sp.GetRequiredService<IVariantClassifierClient>(),
        sp.GetRequiredService<ListingComparisonService>(),
        sp.GetRequiredService<ILogger<ModelFirstComparisonService>>()));
```

**Step 3: Verify build succeeds**

```bash
dotnet build AIOMarketMaker.Api/AIOMarketMaker.Api.csproj
```

**Step 4: Run all tests to verify nothing broke**

```bash
dotnet test AIOMarketMaker.Tests --filter Category=Unit -v n
```

Expected: All existing + new tests pass.

**Step 5: Commit**

```bash
git add AIOMarketMaker.Api/Program.cs AIOMarketMaker.Api/appsettings.json
git commit -m "feat: wire ModelFirstComparisonService as default IListingComparisonService"
```

---

### Task 5: Integration Smoke Test

**Step 1: Start the Python service**

```bash
cd AIOMarketMaker/variant-classifier-service
python main.py
```

Verify: `curl http://localhost:8010/health` returns `{"status": "healthy", "model_loaded": true}`

**Step 2: Start the .NET API**

```bash
cd AIOMarketMaker/AIOMarketMaker.Api
dotnet run
```

**Step 3: Run ComparablesEtl dry-run to verify the full flow**

Trigger a dry-run of the comparables ETL (via API or console) and check logs for:
- `"Model verdict for (X, Y): comparable (confidence=0.XXX)"` — model handled
- `"Model uncertain for (X, Y), confidence=0.XXX — falling back to GPT"` — fallback
- No `"Model service unavailable"` errors

---

## Running the System

After integration:

```bash
# Terminal 1: Python model service
cd AIOMarketMaker/variant-classifier-service
python main.py
# Serves on http://localhost:8010

# Terminal 2: .NET API
cd AIOMarketMaker/AIOMarketMaker.Api
dotnet run
# Serves on http://localhost:5000
```

## Port Registry

| Service | Port | Purpose |
|---------|------|---------|
| AIOMarketMaker.Api | 5000 | Main API |
| ScraperWorker | 7126 | Browser automation |
| Variant Classifier | 8010 | Model inference |
| Azurite | 10000-10002 | Storage emulator |

## Graceful Degradation

If the Python service is not running, `ModelFirstComparisonService` catches the `HttpRequestException` and falls back to GPT for every pair. The system works identically to before — just costs more. No crash, no data loss.
