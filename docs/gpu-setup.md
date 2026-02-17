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

### 3. CUDA Runtime Libraries

**Option A: pip packages (recommended, no admin required)**

```bash
python -m pip install nvidia-cublas-cu12 nvidia-cuda-runtime-cu12 nvidia-cudnn-cu12 nvidia-cufft-cu12 nvidia-cusparse-cu12 nvidia-curand-cu12 nvidia-cusolver-cu12
```

Then copy the DLLs into the project bin directories after each build:

```bash
python scripts/copy-cuda-dlls.py
```

**Option B: Full CUDA Toolkit + cuDNN**

Download CUDA Toolkit 12.x from: https://developer.nvidia.com/cuda-12-4-0-download-archive

Download cuDNN 9.x from: https://developer.nvidia.com/cudnn-downloads

After installation, ensure both bin directories are on PATH.

### 5. Model Files

The classifier needs three files. Configure paths in `appsettings.json` under `VariantClassifier`:

| File | Size | Description |
|------|------|-------------|
| `model.onnx` | 1.36 GB | ONNX export of roberta-large v6 |
| `vocab.json` | ~900 KB | BPE vocabulary (50,265 tokens) |
| `merges.txt` | ~500 KB | BPE merge rules (50,000 merges) |

Source: Export from the roberta-large v6 model via HuggingFace Optimum.

Default config in `appsettings.json`:

```json
"VariantClassifier": {
    "ModelPath": "models/variant-classifier/model.onnx",
    "VocabPath": "models/variant-classifier/vocab.json",
    "MergesPath": "models/variant-classifier/merges.txt",
    "ConfidenceThreshold": 0.80
}
```

Paths can be absolute (e.g., `E:/Dev/ml-training/variant-classifier/v8/onnx/model.onnx`) if the model is stored on a different drive.

## Verification

Start the API and check the logs for:

```
ONNX variant classifier using CUDA GPU
ONNX variant classifier loaded from <ModelPath>
```

If you see "CUDA not available, falling back to CPU", check that CUDA Toolkit and cuDNN are installed and on PATH.

## Troubleshooting

**"cublasLt64_12.dll missing"** -- CUDA Toolkit 12.x not installed or not on PATH.

**"cudnn64_9.dll missing"** -- cuDNN 9.x not installed or bin directory not on PATH.

**"CUDA not available"** -- Driver too old, or GPU not CUDA-capable.

**First inference takes ~60-80 seconds** -- This is normal. CUDA compiles kernels on first use. Subsequent inferences are fast (~13ms).

**"ONNX model not found"** -- Check that `ModelPath` in appsettings.json points to the actual location of `model.onnx`.

## Integration Tests

Run GPU-specific integration tests:

```bash
dotnet test --filter "FullyQualifiedName~OnnxGpuInference" --no-build
```

Tests verify: CUDA provider loading, classifier GPU usage, single-pair latency (<100ms), and batch throughput (128 pairs <10s).

## NuGet Package Architecture

- `Microsoft.ML.OnnxRuntime.Managed` in ML.csproj -- managed API only (tiny), no native DLLs
- `Microsoft.ML.OnnxRuntime.Gpu` in Api.csproj / Etl.csproj / Tests.csproj -- CUDA native DLLs

OnnxRuntime.Gpu includes `onnxruntime_providers_cuda.dll` but it depends on system-installed CUDA libraries (cuBLAS, cuDNN, cuFFT, etc.). These must be in the same directory as the test/app assembly or on PATH.
