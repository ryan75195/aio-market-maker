"""
Export the v8 RoBERTa-large variant classifier to ONNX format.

Usage:
    py -3.12 export_onnx.py
"""

import sys
import io

# Fix Windows cp1252 encoding issue with torch.onnx dynamo exporter
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

import shutil
from pathlib import Path

import torch
from transformers import AutoModelForSequenceClassification, AutoTokenizer

MODEL_DIR = Path("E:/Dev/ml-training/variant-classifier/v8/pytorch")
OUTPUT_DIR = Path("E:/Dev/ml-training/variant-classifier/v8/onnx")


def main():
    print(f"Loading model from {MODEL_DIR}...")
    model = AutoModelForSequenceClassification.from_pretrained(MODEL_DIR)
    tokenizer = AutoTokenizer.from_pretrained(MODEL_DIR)
    model.eval()

    # Create dummy input matching inference shape
    dummy = tokenizer(
        "Sony PlayStation 5 Console",
        "PS5 Disc Edition 825GB",
        return_tensors="pt",
        max_length=256,
        padding="max_length",
        truncation=True,
    )

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    onnx_path = OUTPUT_DIR / "model.onnx"

    print(f"Exporting to {onnx_path}...")
    torch.onnx.export(
        model,
        (dummy["input_ids"], dummy["attention_mask"]),
        str(onnx_path),
        input_names=["input_ids", "attention_mask"],
        output_names=["logits"],
        dynamic_axes={
            "input_ids": {0: "batch", 1: "sequence"},
            "attention_mask": {0: "batch", 1: "sequence"},
            "logits": {0: "batch"},
        },
        opset_version=17,
        do_constant_folding=True,
        dynamo=False,  # Use legacy exporter (torch 2.10 dynamo has cp1252 bug on Windows)
    )

    onnx_size = onnx_path.stat().st_size / (1024 * 1024)
    print(f"ONNX model exported: {onnx_size:.0f} MB")

    # Copy tokenizer files needed by .NET OnnxVariantClassifier
    for fname in ["vocab.json", "merges.txt"]:
        src = MODEL_DIR / fname
        dst = OUTPUT_DIR / fname
        if src.exists():
            shutil.copy2(src, dst)
            print(f"Copied {fname}")
        else:
            print(f"WARNING: {fname} not found in {MODEL_DIR}")

    # Quick validation: run inference on the ONNX model
    import onnxruntime as ort
    import numpy as np

    print("\nValidating ONNX model...")
    session = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])

    # Compare PyTorch vs ONNX output
    with torch.no_grad():
        pt_logits = model(**dummy).logits.numpy()

    ort_inputs = {
        "input_ids": dummy["input_ids"].numpy(),
        "attention_mask": dummy["attention_mask"].numpy(),
    }
    ort_logits = session.run(["logits"], ort_inputs)[0]

    diff = np.abs(pt_logits - ort_logits).max()
    print(f"PyTorch logits: {pt_logits[0]}")
    print(f"ONNX logits:    {ort_logits[0]}")
    print(f"Max difference: {diff:.6f}")

    if diff < 0.001:
        print("\nValidation PASSED - ONNX output matches PyTorch")
    else:
        print(f"\nWARNING: Large difference ({diff:.6f}) - check export")

    print(f"\nDone. Model ready at {OUTPUT_DIR}")


if __name__ == "__main__":
    main()
