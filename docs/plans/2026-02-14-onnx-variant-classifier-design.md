# ONNX Variant Classifier Integration Design

## Goal

Replace the Python FastAPI variant classifier sidecar with native .NET ONNX Runtime inference, eliminating the Python dependency while gaining GPU acceleration (12ms/pair on CUDA vs 700ms/pair on CPU).

## Architecture

Replace `VariantClassifierClient` (HTTP to Python) with `OnnxVariantClassifier` (local ONNX Runtime + CUDA). Same `IVariantClassifierClient` interface — `ModelFirstComparisonService` and all existing tests work unchanged.

```
ModelFirstComparisonService
    |-- IVariantClassifierClient  ->  OnnxVariantClassifier (ONNX Runtime + GPU)
    |-- IListingComparisonService ->  ListingComparisonService (GPT fallback)
```

## Components

### OnnxVariantClassifier

New class in `Core/Services/`, implements `IVariantClassifierClient`.

- Loads `CodeGenTokenizer` from vocab.json + merges.txt at construction
- Creates `InferenceSession` with CUDA provider (falls back to CPU if unavailable)
- `Classify()` tokenizes pairs into RoBERTa sentence-pair format (`<s> A </s></s> B </s>`), runs ONNX inference, applies softmax, returns `PairResult` with confidence and `NeedsFallback` flag
- `IsHealthy()` returns true if session loaded successfully
- Singleton lifetime (model loaded once, thread-safe — ONNX `InferenceSession.Run` is thread-safe)

### OnnxClassifierConfig

Record: `ModelPath`, `VocabPath`, `MergesPath`, `MaxLength` (256), `ConfidenceThreshold` (0.80).

### Configuration (appsettings.json)

```json
"VariantClassifier": {
    "ModelPath": "models/variant-classifier/model.onnx",
    "VocabPath": "models/variant-classifier/vocab.json",
    "MergesPath": "models/variant-classifier/merges.txt",
    "ConfidenceThreshold": 0.80
}
```

## What Gets Removed

- `VariantClassifierClient` — HTTP client class (deleted)
- `variant-classifier-service/` — Python FastAPI project (deleted)
- `AddHttpClient<IVariantClassifierClient>` wiring in Program.cs
- `VariantClassifier:BaseUrl` config key

## Model Files

Location: `AIOMarketMaker/models/variant-classifier/` (gitignored).

| File | Size | Source |
|------|------|--------|
| `model.onnx` | 1.36 GB | Exported from roberta-large v6 via HuggingFace Optimum |
| `vocab.json` | ~1.5 MB | Extracted from tokenizer.json BPE vocabulary |
| `merges.txt` | ~500 KB | Extracted from tokenizer.json merge rules |

## GPU / CUDA

Assume CUDA Toolkit 12.x and cuDNN 9.x installed system-wide. A setup doc at `docs/gpu-setup.md` lists prerequisites. The classifier tries CUDA first, logs a warning and falls back to CPU if unavailable.

## NuGet Packages (added to Core.csproj)

- `Microsoft.ML.OnnxRuntime.Gpu` (1.24.1) — includes CPU fallback
- `Microsoft.ML.Tokenizers` (2.0.0) — `CodeGenTokenizer` for RoBERTa BPE

## Testing

- Existing `ModelFirstComparisonService` unit tests pass unchanged (they mock `IVariantClassifierClient`)
- New unit tests for `OnnxVariantClassifier` tokenization logic (tokenizer only, no model required)
- New integration test that loads the real model and verifies against Python reference logits

## Error Handling

- **Model files missing at startup**: Throw with clear message pointing to `docs/gpu-setup.md`
- **CUDA unavailable**: Log warning, fall back to CPU (functional, just slower)
- **Inference failure**: Exception propagates to `ModelFirstComparisonService` catch block, which falls back to GPT

## PoC Validation (2026-02-13)

Proven in `experiments/onnx-dotnet-poc/`:

| Metric | Result |
|--------|--------|
| Token parity with Python | Exact match (30/30 tokens) |
| Logit parity (CPU) | 0.000000 difference |
| Logit parity (GPU) | 0.000298 max difference (FP32 rounding) |
| GPU latency | 12.6 ms/pair (79.7 pairs/sec) |
| CPU latency | 737 ms/pair (1.4 pairs/sec) |
| Model load time | ~2.5s (one-time) |
| GPU first inference | ~76s (CUDA kernel compilation, one-time) |
