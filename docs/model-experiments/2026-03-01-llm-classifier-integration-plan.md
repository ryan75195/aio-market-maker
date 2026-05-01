# LLM Classifier Integration — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Test the fine-tuned Qwen3-4B model, convert it to GGUF for llama.cpp serving, and integrate it into the C# pipeline as the production variant classifier.

**Architecture:** llama.cpp serves the model on port 8080 with an OpenAI-compatible API. The existing `LlmVariantClassifier` in C# points at it via `IChatClient`. A config flag switches between ONNX and LLM classifiers.

**Tech Stack:** Python (Unsloth, llama.cpp conversion), llama.cpp server (CUDA), C# (.NET 8, existing IChatClient/LlmVariantClassifier)

---

### Task 1: Run Audit Benchmark Evaluation

This is the go/no-go gate. 90%+ accuracy required to proceed.

**Files:**
- Run: `AIOMarketMaker/AIOMarketMaker.ML/Training/eval_finetuned.py`

**Step 1: Run the evaluation script**

```bash
cd AIOMarketMaker/AIOMarketMaker.ML/Training
python eval_finetuned.py --n 50
```

Expected output: per-pair verdicts with OK/WRONG/PARSE_ERR status, final accuracy summary.

**Step 2: Assess results**

- If 90%+: proceed to Task 2
- If 80-89%: review the WRONG pairs — are they edge cases or fundamental failures? Decide whether to proceed
- If <80%: stop. Investigate training data quality, consider more epochs or hyperparameter changes
- If parse errors >10%: the model isn't producing valid JSON — may need prompt adjustment or more training

**Step 3: Save the eval output**

```bash
python eval_finetuned.py --n 50 > logs/eval_results.txt 2>&1
```

---

### Task 2: Write and Run Stress Test on Known Failure Modes

**Files:**
- Create: `AIOMarketMaker/AIOMarketMaker.ML/Training/stress_test.py`

**Step 1: Create stress_test.py with hand-picked pairs**

```python
"""
Stress test the fine-tuned model on known classifier failure modes.
These are real pairs that the ONNX RoBERTa model got wrong.

Usage:
    python stress_test.py
    python stress_test.py --adapter output/qwen3-variant-classifier
"""

import argparse
import json
import os
import time
from pathlib import Path

os.environ["TORCHINDUCTOR_CACHE_DIR"] = "C:/tmp/ti"
os.environ["TRITON_CACHE_DIR"] = "C:/tmp/triton"
os.environ["UNSLOTH_COMPILE_DISABLE"] = "1"
os.environ["TORCH_COMPILE_DISABLE"] = "1"

USER_TEMPLATE = """Listing A:
Title: {title_a}
Description: {desc_a}

Listing B:
Title: {title_b}
Description: {desc_b}"""

# Each pair: (title_a, desc_a, title_b, desc_b, expected_verdict, failure_mode)
STRESS_PAIRS = [
    # Language editions — should be DIFFERENT
    (
        "Pokemon Phantasmal Flames Booster Box New Sealed PORTUGUESE - Free Shipping",
        "Brand new sealed Pokemon Phantasmal Flames booster box. Portuguese language edition. 36 packs.",
        "Pokemon Phantasmal Flames Booster Box Factory Sealed (36 Packs) - New & Sealed",
        "English edition Phantasmal Flames booster box. 36 booster packs. Factory sealed.",
        "different",
        "Language edition (Portuguese vs English)",
    ),
    # Product tier — Premium Collection vs Ultra-Premium Collection
    (
        "Pokemon TCG Charizard EX Premium Collection Box New Sealed",
        "Charizard EX Premium Collection. Includes 6 promo cards and 4 booster packs.",
        "Pokemon TCG Mega Charizard X EX Ultra-Premium Collection Box Sealed",
        "Mega Charizard X EX Ultra-Premium Collection UPC. Includes 16 booster packs, metal card, playmat.",
        "different",
        "Product tier (Premium vs Ultra-Premium Collection)",
    ),
    # Accessory vs full product — disc drive vs console
    (
        "PS5 Disc Drive for PlayStation 5 Digital Edition CFI-1015B",
        "Replacement disc drive for PS5 Digital Edition. Drive unit only, no console.",
        "Sony PlayStation 5 Slim Disc Console 1TB",
        "Brand new PS5 Slim with disc drive. 1TB storage. Includes controller.",
        "different",
        "Accessory vs full product (disc drive vs console)",
    ),
    # Storage difference
    (
        "Apple iPhone 15 Pro Max 256GB Black Titanium Unlocked",
        "iPhone 15 Pro Max with 256GB storage. Black Titanium. Factory unlocked.",
        "Apple iPhone 15 Pro Max 512GB Black Titanium Unlocked",
        "iPhone 15 Pro Max with 512GB storage. Black Titanium. Factory unlocked.",
        "different",
        "Storage variant (256GB vs 512GB)",
    ),
    # CPU difference
    (
        'Apple MacBook Pro 14" M3 Pro 18GB 512GB Space Black 2023',
        "MacBook Pro 14 inch with M3 Pro chip. 18GB unified memory. 512GB SSD.",
        'Apple MacBook Pro 14" M3 Max 36GB 1TB Space Black 2023',
        "MacBook Pro 14 inch with M3 Max chip. 36GB unified memory. 1TB SSD.",
        "different",
        "CPU/RAM variant (M3 Pro vs M3 Max)",
    ),
    # Condition mismatch (2+ bands apart)
    (
        "Samsung Galaxy S24 Ultra 256GB - Grade C - Cracked Back",
        "Grade C condition. Cracked back panel. Heavy scratches on screen. Fully functional.",
        "Samsung Galaxy S24 Ultra 256GB - Pristine - Like New",
        "Pristine condition. No scratches, no marks. Comes with original box and accessories.",
        "different",
        "Condition mismatch (Grade C vs Pristine)",
    ),
    # Bundle vs single item
    (
        "Nintendo Switch OLED Console + 3 Games + Case + Screen Protector Bundle",
        "Nintendo Switch OLED White. Comes with Mario Kart 8, Zelda TOTK, Animal Crossing, carry case, screen protector.",
        "Nintendo Switch OLED Console White",
        "Nintendo Switch OLED Model. White Joy-Con. Console only with original accessories.",
        "different",
        "Bundle vs single item",
    ),
    # Color irrelevant for electronics — should be SAME
    (
        "Sony WH-1000XM5 Wireless Noise Cancelling Headphones - Black",
        "Sony WH-1000XM5 headphones. Black. Brand new sealed.",
        "Sony WH-1000XM5 Wireless Noise Cancelling Headphones - Silver",
        "Sony WH-1000XM5 headphones. Silver. Brand new sealed.",
        "same",
        "Color irrelevant for electronics",
    ),
    # Same product, different seller descriptions — should be SAME
    (
        "Dyson V15 Detect Absolute Cordless Vacuum",
        "Dyson V15 Detect Absolute. Brand new in box. FREE FAST SHIPPING.",
        "Dyson V15 Detect Absolute Cordless Stick Vacuum Cleaner",
        "Brand new sealed Dyson V15 Detect Absolute vacuum. Full manufacturer warranty.",
        "same",
        "Same product, different listing styles",
    ),
    # Shoe size difference — should be DIFFERENT
    (
        "Nike Air Jordan 1 Retro High OG Chicago 2025 Size 10",
        "Air Jordan 1 Chicago 2025 retro. Size US 10. Brand new deadstock.",
        "Nike Air Jordan 1 Retro High OG Chicago 2025 Size 12",
        "Air Jordan 1 Chicago 2025 retro. Size US 12. Brand new deadstock.",
        "different",
        "Shoe size difference",
    ),
    # Watch colorway matters — should be DIFFERENT
    (
        "Rolex Submariner Date 126613LN Black Dial Two-Tone",
        "Rolex Submariner 126613LN. Black dial, black bezel. Steel and gold.",
        "Rolex Submariner Date 126613LB Blue Dial Two-Tone",
        "Rolex Submariner 126613LB. Blue dial, blue bezel. Steel and gold.",
        "different",
        "Watch colorway (LN black vs LB blue)",
    ),
    # Half box vs full box — should be DIFFERENT
    (
        "Pokemon TCG Phantasmal Flames Half Booster Box 18 Packs Sealed",
        "Half booster box containing 18 packs. New and sealed.",
        "Pokemon Phantasmal Flames Booster Box Factory Sealed 36 Packs",
        "Full booster box with 36 packs. Factory sealed.",
        "different",
        "Half box (18 packs) vs full box (36 packs)",
    ),
]


def classify(model, tokenizer, title_a, desc_a, title_b, desc_b):
    import torch
    user_msg = USER_TEMPLATE.format(
        title_a=title_a, desc_a=desc_a[:300],
        title_b=title_b, desc_b=desc_b[:300],
    )
    messages = [{"role": "user", "content": user_msg}]
    text = tokenizer.apply_chat_template(
        messages, tokenize=False, add_generation_prompt=True,
    )
    inputs = tokenizer(text, return_tensors="pt").to(model.device)
    input_len = inputs["input_ids"].shape[1]

    with torch.no_grad():
        outputs = model.generate(
            **inputs, max_new_tokens=150, do_sample=False, temperature=1.0,
        )
    response = tokenizer.decode(outputs[0][input_len:], skip_special_tokens=True)

    try:
        start = response.index("{")
        end = response.rindex("}") + 1
        parsed = json.loads(response[start:end])
        return parsed.get("verdict", "unknown"), parsed.get("reason", ""), response
    except (ValueError, json.JSONDecodeError):
        return "parse_error", response, response


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--adapter", default="output/qwen3-variant-classifier")
    args = parser.parse_args()

    adapter_path = Path(__file__).parent / args.adapter
    print(f"Loading model from {adapter_path}")

    from unsloth import FastLanguageModel

    model, tokenizer = FastLanguageModel.from_pretrained(
        model_name=str(adapter_path),
        max_seq_length=1024,
        load_in_4bit=True,
        dtype=None,
    )
    FastLanguageModel.for_inference(model)

    print(f"\nRunning {len(STRESS_PAIRS)} stress test pairs...")
    print("=" * 80)

    correct = 0
    total = 0
    failures = []

    for i, (ta, da, tb, db, expected, mode) in enumerate(STRESS_PAIRS):
        t1 = time.time()
        verdict, reason, raw = classify(model, tokenizer, ta, da, tb, db)
        elapsed = time.time() - t1

        is_correct = verdict == expected
        if is_correct:
            correct += 1
        else:
            failures.append((mode, expected, verdict, reason))
        total += 1

        status = "PASS" if is_correct else "FAIL"
        print(f"[{i+1}/{len(STRESS_PAIRS)}] {status} | {mode}")
        print(f"  Expected: {expected} | Got: {verdict}")
        print(f"  Reason: {reason[:120]}")
        print(f"  Time: {elapsed:.1f}s")
        print()

    print("=" * 80)
    print(f"STRESS TEST: {correct}/{total} ({100*correct/total:.0f}%)")

    if failures:
        print(f"\nFAILURES ({len(failures)}):")
        for mode, expected, got, reason in failures:
            print(f"  {mode}: expected={expected}, got={got}")
            print(f"    Reason: {reason[:100]}")
    else:
        print("\nAll stress tests passed!")


if __name__ == "__main__":
    main()
```

**Step 2: Run the stress test**

```bash
cd AIOMarketMaker/AIOMarketMaker.ML/Training
python stress_test.py
```

**Step 3: Assess results**

All 12 pairs should pass. Pay attention to the reasoning — even if the verdict is correct, check that the reason references the right distinction (e.g., "Portuguese vs English" not just "different titles").

Any failures here indicate the model's world knowledge didn't survive fine-tuning for that specific pattern. Note which failure modes fail for potential follow-up.

---

### Task 3: Merge LoRA Adapter and Convert to GGUF

**Files:**
- Create: `AIOMarketMaker/AIOMarketMaker.ML/Training/merge_and_convert.py`

**Step 1: Create the merge and conversion script**

```python
"""
Merge the LoRA adapter into the base model and export to GGUF for llama.cpp.

Usage:
    python merge_and_convert.py
    python merge_and_convert.py --adapter output/qwen3-variant-classifier
    python merge_and_convert.py --quant Q4_K_M
"""

import argparse
import os
import subprocess
import sys
from pathlib import Path

os.environ["TORCHINDUCTOR_CACHE_DIR"] = "C:/tmp/ti"
os.environ["TRITON_CACHE_DIR"] = "C:/tmp/triton"
os.environ["UNSLOTH_COMPILE_DISABLE"] = "1"
os.environ["TORCH_COMPILE_DISABLE"] = "1"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--adapter", default="output/qwen3-variant-classifier",
                        help="LoRA adapter path (relative to Training/)")
    parser.add_argument("--quant", default="Q4_K_M",
                        help="GGUF quantization type")
    parser.add_argument("--output-dir", default="output/qwen3-variant-classifier-gguf",
                        help="Output directory for GGUF files")
    args = parser.parse_args()

    adapter_path = Path(__file__).parent / args.adapter
    merged_dir = Path(__file__).parent / "output" / "qwen3-variant-classifier-merged"
    gguf_dir = Path(__file__).parent / args.output_dir

    # Step 1: Merge LoRA into base model
    print(f"Step 1: Merging LoRA adapter from {adapter_path}...")
    from unsloth import FastLanguageModel

    model, tokenizer = FastLanguageModel.from_pretrained(
        model_name=str(adapter_path),
        max_seq_length=1024,
        load_in_4bit=True,
        dtype=None,
    )

    print(f"Saving merged model to {merged_dir}...")
    merged_dir.mkdir(parents=True, exist_ok=True)
    model.save_pretrained_merged(
        str(merged_dir),
        tokenizer,
        save_method="merged_16bit",  # 16-bit for GGUF conversion
    )
    tokenizer.save_pretrained(str(merged_dir))
    print(f"Merged model saved to {merged_dir}")

    # Step 2: Convert to GGUF
    print(f"\nStep 2: Converting to GGUF ({args.quant})...")
    gguf_dir.mkdir(parents=True, exist_ok=True)

    # Find llama.cpp convert script
    # User needs llama.cpp cloned or installed — check common locations
    convert_script = None
    for candidate in [
        Path("C:/tools/llama.cpp/convert_hf_to_gguf.py"),
        Path.home() / "llama.cpp" / "convert_hf_to_gguf.py",
        Path(__file__).parent / "llama.cpp" / "convert_hf_to_gguf.py",
    ]:
        if candidate.exists():
            convert_script = candidate
            break

    if convert_script is None:
        print("\nERROR: Could not find llama.cpp convert_hf_to_gguf.py")
        print("Clone llama.cpp and set the path, or run manually:")
        print(f"  python convert_hf_to_gguf.py {merged_dir} --outtype {args.quant.lower()} --outfile {gguf_dir}/model.gguf")
        sys.exit(1)

    gguf_path = gguf_dir / "model.gguf"
    cmd = [
        sys.executable, str(convert_script),
        str(merged_dir),
        "--outtype", args.quant.lower() if args.quant.startswith("f") else "f16",
        "--outfile", str(gguf_path),
    ]
    print(f"Running: {' '.join(cmd)}")
    subprocess.run(cmd, check=True)

    # Step 3: Quantize if not already a float type
    if not args.quant.startswith("f"):
        print(f"\nStep 3: Quantizing to {args.quant}...")
        quantized_path = gguf_dir / f"model-{args.quant}.gguf"
        # llama-quantize is a compiled binary from llama.cpp
        quantize_cmd = ["llama-quantize", str(gguf_path), str(quantized_path), args.quant]
        print(f"Running: {' '.join(quantize_cmd)}")
        subprocess.run(quantize_cmd, check=True)
        print(f"\nQuantized model saved to {quantized_path}")
        print(f"Size: {quantized_path.stat().st_size / 1024 / 1024:.0f} MB")
    else:
        print(f"\nGGUF model saved to {gguf_path}")
        print(f"Size: {gguf_path.stat().st_size / 1024 / 1024:.0f} MB")


if __name__ == "__main__":
    main()
```

**Step 2: Install llama.cpp (if not already installed)**

```bash
# Option A: Download pre-built Windows binaries with CUDA
# From https://github.com/ggerganov/llama.cpp/releases
# Extract to C:/tools/llama.cpp/

# Option B: Clone and build
git clone https://github.com/ggerganov/llama.cpp C:/tools/llama.cpp
cd C:/tools/llama.cpp
cmake -B build -DGGML_CUDA=ON
cmake --build build --config Release
```

**Step 3: Run the merge and conversion**

```bash
cd AIOMarketMaker/AIOMarketMaker.ML/Training
python merge_and_convert.py
```

Expected output: merged model in `output/qwen3-variant-classifier-merged/`, GGUF file in `output/qwen3-variant-classifier-gguf/model-Q4_K_M.gguf` (~2.5GB).

---

### Task 4: Start llama.cpp Server and Verify GGUF Quality

**Step 1: Start the llama.cpp server**

```bash
# From llama.cpp install directory
llama-server \
  -m AIOMarketMaker/AIOMarketMaker.ML/Training/output/qwen3-variant-classifier-gguf/model-Q4_K_M.gguf \
  --host 0.0.0.0 --port 8080 \
  --n-gpu-layers 99 \
  --ctx-size 1024
```

Verify it starts: `curl http://localhost:8080/health` should return `{"status":"ok"}`.

**Step 2: Test a single pair via curl**

```bash
curl -s http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "qwen3-variant-classifier",
    "messages": [
      {"role": "user", "content": "Listing A:\nTitle: PS5 Disc Drive CFI-1015B\nDescription: Replacement disc drive only\n\nListing B:\nTitle: Sony PlayStation 5 Slim 1TB Console\nDescription: Full console with controller"}
    ],
    "temperature": 0,
    "max_tokens": 150
  }'
```

Expected: JSON response with `{"reason": "...", "verdict": "different"}`.

**Step 3: Run stress test against llama.cpp server**

Create a quick HTTP-based version of the stress test, or modify `stress_test.py` to accept a `--server` flag that calls the HTTP API instead of loading the model locally. Compare verdicts between Unsloth and llama.cpp — they should match on all 12 pairs.

If any verdicts flip between Unsloth and GGUF, the quantization lost important information. Try Q5_K_M or Q6_K instead.

---

### Task 5: Create LocalLlmChatClient (IChatClient for llama.cpp)

**Files:**
- Create: `AIOMarketMaker/AIOMarketMaker.ML/Services/LocalLlmChatClient.cs`
- Test: `AIOMarketMaker/AIOMarketMaker.Tests.Unit/ML/LocalLlmChatClientTests.cs`

**Step 1: Write the failing test**

```csharp
using AIOMarketMaker.ML.Services;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.ML;

[TestFixture]
[Category("Unit")]
public class LocalLlmChatClientTests
{
    [Test]
    public async Task Should_send_user_message_only_when_system_prompt_is_null()
    {
        // The fine-tuned model was trained without a system prompt.
        // LocalLlmChatClient should omit the system message when
        // systemPrompt is null or empty.
        var client = new LocalLlmChatClient(
            new HttpClient(),
            baseUrl: "http://localhost:8080",
            model: "test");

        // We can't call the real server in a unit test, but we can
        // verify the request body construction via a testable method.
        var body = LocalLlmChatClient.BuildRequestBody(
            systemPrompt: null,
            userPrompt: "test prompt",
            model: "test",
            maxTokens: 150,
            temperature: 0f);

        Assert.That(body.Messages, Has.Count.EqualTo(1));
        Assert.That(body.Messages[0].Role, Is.EqualTo("user"));
    }

    [Test]
    public async Task Should_include_system_message_when_system_prompt_provided()
    {
        var body = LocalLlmChatClient.BuildRequestBody(
            systemPrompt: "You are a classifier",
            userPrompt: "test prompt",
            model: "test",
            maxTokens: 150,
            temperature: 0f);

        Assert.That(body.Messages, Has.Count.EqualTo(2));
        Assert.That(body.Messages[0].Role, Is.EqualTo("system"));
        Assert.That(body.Messages[1].Role, Is.EqualTo("user"));
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit --filter "FullyQualifiedName~LocalLlmChatClientTests" -v n
```

Expected: FAIL — `LocalLlmChatClient` doesn't exist yet.

**Step 3: Implement LocalLlmChatClient**

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIOMarketMaker.ML.Utils;

namespace AIOMarketMaker.ML.Services;

public record LocalLlmConfig(
    string BaseUrl = "http://localhost:8080",
    string Model = "qwen3-variant-classifier",
    int MaxTokens = 150,
    float Temperature = 0f);

public class LocalLlmChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly float _temperature;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public LocalLlmChatClient(HttpClient http, LocalLlmConfig config)
        : this(http, config.BaseUrl, config.Model, config.MaxTokens, config.Temperature)
    {
    }

    public LocalLlmChatClient(HttpClient http, string baseUrl, string model,
        int maxTokens = 150, float temperature = 0f)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _maxTokens = maxTokens;
        _temperature = temperature;
    }

    public async Task<string> CompleteChat(string systemPrompt, string userPrompt,
        CancellationToken ct = default)
    {
        var body = BuildRequestBody(systemPrompt, userPrompt, _model, _maxTokens, _temperature);
        var json = JsonSerializer.Serialize(body, JsonOptions);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _http.PostAsync($"{_baseUrl}/v1/chat/completions", content, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(JsonOptions, ct);
        return result?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
    }

    public static ChatCompletionRequest BuildRequestBody(
        string? systemPrompt, string userPrompt, string model,
        int maxTokens, float temperature)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messages.Add(new ChatMessage("system", systemPrompt));
        }

        messages.Add(new ChatMessage("user", userPrompt));

        return new ChatCompletionRequest(model, messages, maxTokens, temperature);
    }

    public record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    public record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] List<ChatMessage> Messages,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("temperature")] float Temperature);

    private record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] List<Choice>? Choices);

    private record Choice(
        [property: JsonPropertyName("message")] ResponseMessage? Message);

    private record ResponseMessage(
        [property: JsonPropertyName("content")] string? Content);
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit --filter "FullyQualifiedName~LocalLlmChatClientTests" -v n
```

Expected: PASS

**Step 5: Commit**

```bash
cd AIOMarketMaker
git add AIOMarketMaker.ML/Services/LocalLlmChatClient.cs AIOMarketMaker.Tests.Unit/ML/LocalLlmChatClientTests.cs
git commit -m "feat: add LocalLlmChatClient for llama.cpp OpenAI-compatible API"
```

---

### Task 6: Update LlmVariantClassifier for Fine-Tuned Model

The fine-tuned model was trained without a system prompt and outputs `{"reason": "...", "verdict": "same/different"}` (lowercase). The existing `LlmVariantClassifier` uses a system prompt and expects structured output via OpenAI's `json_schema` feature. We need to make it work with the local model.

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.ML/Services/LlmVariantClassifier.cs`
- Test: `AIOMarketMaker/AIOMarketMaker.Tests.Unit/ML/LlmVariantClassifierTests.cs` (existing)

**Step 1: Write a test for no-system-prompt mode**

Add to the existing test file:

```csharp
[Test]
public async Task Should_send_null_system_prompt_when_configured_for_local_model()
{
    // The fine-tuned local model was trained without a system prompt.
    // When useSystemPrompt=false, the classifier should pass null.
    var mockClient = new Mock<IChatClient>();
    mockClient.Setup(c => c.CompleteChat(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync("{\"reason\": \"same product\", \"verdict\": \"same\"}");

    var config = new LlmClassifierConfig(UseSystemPrompt: false);
    var classifier = new LlmVariantClassifier(mockClient.Object, config,
        Mock.Of<ILogger<LlmVariantClassifier>>());

    var results = await classifier.Classify(new[]
    {
        new ClassifyPairRequest("Title A", "Desc A", "Title B", "Desc B")
    });

    mockClient.Verify(c => c.CompleteChat(
        null, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit --filter "FullyQualifiedName~Should_send_null_system_prompt" -v n
```

**Step 3: Add UseSystemPrompt flag to config and update ClassifyOne**

In `LlmVariantClassifier.cs`, update the config record:

```csharp
public record LlmClassifierConfig(
    int MaxConcurrency = 50,
    int MaxRetries = 3,
    bool UseSystemPrompt = true);
```

Update `ClassifyOne`:

```csharp
private async Task<PairResult> ClassifyOne(ClassifyPairRequest pair, CancellationToken ct)
{
    var userPrompt = BuildUserPrompt(pair);
    var systemPrompt = _config.UseSystemPrompt ? SystemPromptText : null;
    var response = await WithRetry(
        () => _client.CompleteChat<ClassifierResponse>(systemPrompt, userPrompt, ct), ct);

    if (response is null)
    {
        return new PairResult(false, 0.0f);
    }

    return response.Verdict switch
    {
        Verdict.Same => new PairResult(true, 1.0f, response.Reason),
        Verdict.Different => new PairResult(false, 1.0f, response.Reason),
        Verdict.Uncertain => new PairResult(false, 0.5f, response.Reason),
        _ => new PairResult(false, 0.0f)
    };
}
```

Store the config as a field:

```csharp
private readonly LlmClassifierConfig _config;

public LlmVariantClassifier(IChatClient client, LlmClassifierConfig config, ILogger<LlmVariantClassifier> logger)
{
    _client = client;
    _config = config;
    _semaphore = new SemaphoreSlim(config.MaxConcurrency, config.MaxConcurrency);
    _maxRetries = config.MaxRetries;
    _logger = logger;
}
```

Also update `IChatClient.CompleteChat<T>` to handle null system prompt (pass through to `CompleteChat` which `LocalLlmChatClient` already handles).

**Step 4: Run all classifier tests**

```bash
dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit --filter "FullyQualifiedName~VariantClassifier" -v n
```

Expected: all pass (existing + new).

**Step 5: Commit**

```bash
cd AIOMarketMaker
git add AIOMarketMaker.ML/Services/LlmVariantClassifier.cs
git commit -m "feat: add UseSystemPrompt flag for fine-tuned local model"
```

---

### Task 7: Config-Driven Classifier Switch in Program.cs

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Api/Program.cs:135-153`
- Modify: `AIOMarketMaker/AIOMarketMaker.Console/Program.cs` (same DI setup)

**Step 1: Add config key to local.settings.json**

```json
{
  "Values": {
    "VariantClassifier:Provider": "Onnx",
    "VariantClassifier:Llm:BaseUrl": "http://localhost:8080",
    "VariantClassifier:Llm:Model": "qwen3-variant-classifier",
    "VariantClassifier:Llm:MaxTokens": "150"
  }
}
```

**Step 2: Update Program.cs DI registration**

Replace the current block (lines 135-153) with:

```csharp
// Variant classifier — choose provider via config
var classifierProvider = configuration.GetValue<string>("VariantClassifier:Provider") ?? "Onnx";

if (classifierProvider.Equals("Llm", StringComparison.OrdinalIgnoreCase))
{
    // Local LLM via llama.cpp OpenAI-compatible API
    var llmConfig = new LocalLlmConfig(
        BaseUrl: configuration.GetValue<string>("VariantClassifier:Llm:BaseUrl") ?? "http://localhost:8080",
        Model: configuration.GetValue<string>("VariantClassifier:Llm:Model") ?? "qwen3-variant-classifier",
        MaxTokens: configuration.GetValue<int?>("VariantClassifier:Llm:MaxTokens") ?? 150);
    builder.Services.AddSingleton(llmConfig);
    builder.Services.AddSingleton<IChatClient>(sp =>
        new LocalLlmChatClient(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, llmConfig));
    builder.Services.AddSingleton(new LlmClassifierConfig(
        MaxConcurrency: 1, MaxRetries: 3, UseSystemPrompt: false));
    builder.Services.AddSingleton<IVariantClassifierClient, LlmVariantClassifier>();
}
else
{
    // ONNX model + ensemble calibration
    var classifierConfig = new OnnxClassifierConfig(
        ModelPath: configuration.GetValue<string>("VariantClassifier:ModelPath") ?? "models/variant-classifier/model.onnx",
        VocabPath: configuration.GetValue<string>("VariantClassifier:VocabPath") ?? "models/variant-classifier/vocab.json",
        MergesPath: configuration.GetValue<string>("VariantClassifier:MergesPath") ?? "models/variant-classifier/merges.txt",
        MaxLength: configuration.GetValue<int?>("VariantClassifier:MaxLength") ?? 512);
    builder.Services.AddSingleton(classifierConfig);
    builder.Services.AddSingleton<VariantModelRunner>();
    builder.Services.AddSingleton<IVariantModelRunner>(sp => sp.GetRequiredService<VariantModelRunner>());

    var ensembleLogitWeight = configuration.GetValue<float>("VariantClassifier:Ensemble:LogitWeight");
    if (ensembleLogitWeight != 0)
    {
        builder.Services.AddSingleton(new EnsembleConfig(
            LogitWeight: ensembleLogitWeight,
            SimilarityWeight: configuration.GetValue<float>("VariantClassifier:Ensemble:SimilarityWeight"),
            Intercept: configuration.GetValue<float>("VariantClassifier:Ensemble:Intercept")));
    }
    builder.Services.AddSingleton<IVariantClassifierClient, VariantClassifier>();
}
```

Note: `MaxConcurrency: 1` for local LLM — the model can only process one request at a time on a single GPU. The semaphore prevents queuing 256 concurrent requests.

**Step 3: Apply the same change to Console Program.cs**

The Console project has the same DI setup. Apply the same config-driven switch.

**Step 4: Build and verify**

```bash
dotnet build AIOMarketMaker/AIOMarketMaker.sln
```

Expected: clean build, no errors.

**Step 5: Commit**

```bash
cd AIOMarketMaker
git add AIOMarketMaker.Api/Program.cs AIOMarketMaker.Console/Program.cs
git commit -m "feat: config-driven switch between ONNX and LLM classifier providers"
```

---

### Task 8: Create Reclassify Console Task

**Files:**
- Create: `AIOMarketMaker/AIOMarketMaker.Console/Tasks/ReclassifyTask.cs`

**Step 1: Implement the reclassify task**

```csharp
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Console.Tasks;

public class ReclassifyTask : ITask
{
    public string Name => "reclassify";
    public string Description => "Re-classify existing relationships using the current classifier provider";

    private readonly IDbContextFactory<EtlDbContext> _dbFactory;
    private readonly IVariantClassifierClient _classifier;
    private readonly ILogger<ReclassifyTask> _logger;

    public ReclassifyTask(
        IDbContextFactory<EtlDbContext> dbFactory,
        IVariantClassifierClient classifier,
        ILogger<ReclassifyTask> logger)
    {
        _dbFactory = dbFactory;
        _classifier = classifier;
        _logger = logger;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var batchSize = 50;  // Small batches for LLM — each takes minutes
        var startFromId = 0;

        // Parse optional args
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--batch-size")
            {
                batchSize = int.Parse(args[i + 1]);
            }
            if (args[i] == "--start-from")
            {
                startFromId = int.Parse(args[i + 1]);
            }
        }

        // Find relationships for active listings that have predictions
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var relationships = await db.ListingRelationships
            .Where(lr => lr.Id > startFromId)
            .Where(lr => lr.IsComparable)  // Only re-check ones marked comparable
            .Where(lr => db.Listings.Any(l => l.Id == lr.ListingIdA && l.ListingStatus == "Active"))
            .OrderBy(lr => lr.Id)
            .ToListAsync(ct);

        _logger.LogInformation("Found {Count} comparable relationships to reclassify", relationships.Count);

        // Load listings for building requests
        var listingIds = relationships
            .SelectMany(r => new[] { r.ListingIdA, r.ListingIdB })
            .Distinct()
            .ToHashSet();

        var listings = await db.Listings
            .Where(l => listingIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, ct);

        var total = relationships.Count;
        var processed = 0;
        var changed = 0;

        foreach (var batch in relationships.Chunk(batchSize))
        {
            var requests = batch.Select(r =>
            {
                var a = listings.GetValueOrDefault(r.ListingIdA);
                var b = listings.GetValueOrDefault(r.ListingIdB);
                return new ClassifyPairRequest(
                    a?.Title ?? "", a?.Description ?? "",
                    b?.Title ?? "", b?.Description ?? "",
                    (float?)r.SimilarityScore);
            }).ToList();

            var results = await _classifier.Classify(requests, ct);

            for (var i = 0; i < batch.Length; i++)
            {
                var rel = batch[i];
                var result = results[i];

                var wasComparable = rel.IsComparable;
                rel.IsComparable = result.IsComparable;
                rel.ClassifierConfidence = result.Confidence;
                rel.Explanation = result.Reason ?? $"LLM: confidence={result.Confidence:F3}";

                if (wasComparable != result.IsComparable)
                {
                    changed++;
                }
            }

            processed += batch.Length;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Progress: {Processed}/{Total} | Changed: {Changed} | Last ID: {LastId}",
                processed, total, changed, batch.Last().Id);
        }

        _logger.LogInformation(
            "Reclassification complete. {Processed} processed, {Changed} changed ({Pct:F1}%)",
            processed, changed, 100.0 * changed / Math.Max(processed, 1));

        return 0;
    }
}
```

**Step 2: Register the task**

In `AIOMarketMaker/AIOMarketMaker.Console/Program.cs`, add:

```csharp
services.AddTask<ReclassifyTask>();
```

**Step 3: Build and verify**

```bash
dotnet build AIOMarketMaker/AIOMarketMaker.sln
```

**Step 4: Commit**

```bash
cd AIOMarketMaker
git add AIOMarketMaker.Console/Tasks/ReclassifyTask.cs AIOMarketMaker.Console/Program.cs
git commit -m "feat: add reclassify console task for LLM re-classification of existing pairs"
```

---

### Task 9: End-to-End Integration Test

This is a manual smoke test to verify the full pipeline works.

**Step 1: Start llama.cpp server**

```bash
llama-server -m <path-to-gguf> --host 0.0.0.0 --port 8080 --n-gpu-layers 99 --ctx-size 1024
```

**Step 2: Set config to LLM provider**

In `AIOMarketMaker.Api/local.settings.json` and `AIOMarketMaker.Console/local.settings.json`:

```json
"VariantClassifier:Provider": "Llm"
```

**Step 3: Run the API and trigger a comparables batch**

```bash
cd AIOMarketMaker/AIOMarketMaker.Api
dotnet run
```

Trigger a batch via the Desktop app or API. Monitor logs for:
- `LLM classifier starting N pairs`
- `LLM classifier completed N pairs`
- No HTTP errors to llama.cpp

**Step 4: Run the reclassify task on a small subset**

```bash
cd AIOMarketMaker/AIOMarketMaker.Console
dotnet run -- reclassify --batch-size 10
```

Verify:
- Task completes without errors
- Some relationships flip from comparable → not comparable (the whole point)
- Explanations now contain reasoning text instead of just "Model: confidence=X"

**Step 5: Check pricing impact**

```bash
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "
SELECT TOP 10 lr.Explanation, lr.IsComparable, lr.ClassifierConfidence
FROM ListingRelationships lr
WHERE lr.Explanation LIKE 'LLM:%' OR lr.Explanation NOT LIKE 'Model:%'
ORDER BY lr.Id DESC
" -y 0
```

Verify the LLM's explanations are meaningful and the IsComparable decisions look correct.

---

## Summary of Deliverables

| Task | Type | Deliverable |
|------|------|-------------|
| 1 | Eval | Audit benchmark results (90%+ gate) |
| 2 | Eval | Stress test script + results |
| 3 | Infra | Merged model + GGUF file |
| 4 | Infra | llama.cpp server verified |
| 5 | Code | LocalLlmChatClient + tests |
| 6 | Code | LlmVariantClassifier UseSystemPrompt flag + tests |
| 7 | Code | Config-driven provider switch in Program.cs |
| 8 | Code | Reclassify console task |
| 9 | Test | End-to-end smoke test |

Tasks 1-4 are Python/infra work (no C# changes). Tasks 5-8 are C# code changes with tests. Task 9 is manual verification.

**Critical path:** Task 1 is the gate. If the model scores <90%, tasks 2-9 are blocked until the model improves.
