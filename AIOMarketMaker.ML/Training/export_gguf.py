"""Export fine-tuned Qwen3-4B LoRA adapter to GGUF format for LLamaSharp.

Usage:
    python export_gguf.py
    python export_gguf.py --quantize q4_k_m
    python export_gguf.py --output-dir E:/Dev/ml-training/extraction-model/gguf
"""

import argparse
import subprocess
import sys
from pathlib import Path

ADAPTER_DIR = Path(__file__).parent / "data" / "topdown_taxonomy" / "models" / "qwen3-4b-extraction" / "lora_adapter"
DEFAULT_OUTPUT = Path("F:/ml-temp/extraction-model/gguf")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--quantize", default="q4_k_m", help="GGUF quantization type")
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()

    args.output_dir.mkdir(parents=True, exist_ok=True)
    merged_dir = args.output_dir / "merged"
    merged_dir.mkdir(exist_ok=True)

    # Step 1: Merge LoRA into base model
    print("Step 1: Merging LoRA adapter into base model...")
    from unsloth import FastLanguageModel
    model, tokenizer = FastLanguageModel.from_pretrained(
        model_name=str(ADAPTER_DIR),
        max_seq_length=1024,
        load_in_4bit=False,  # Need full precision for GGUF conversion
    )
    model.save_pretrained_merged(str(merged_dir), tokenizer, save_method="merged_16bit")
    print(f"Merged model saved to {merged_dir}")

    # Step 2: Convert to GGUF using llama.cpp's convert script
    print(f"\nStep 2: Converting to GGUF ({args.quantize})...")
    gguf_path = args.output_dir / f"qwen3-4b-extraction-{args.quantize}.gguf"

    # Try llama-cpp-python's built-in converter first, fall back to manual
    try:
        convert_cmd = [
            sys.executable, "-m", "llama_cpp.convert",
            str(merged_dir),
            "--outfile", str(gguf_path),
            "--outtype", args.quantize,
        ]
        subprocess.run(convert_cmd, check=True)
    except (ImportError, subprocess.CalledProcessError, FileNotFoundError):
        print("llama-cpp-python convert not available. Trying huggingface-hub approach...")
        print(f"\nPlease run manually:")
        print(f"  python convert_hf_to_gguf.py {merged_dir} --outfile {gguf_path} --outtype {args.quantize}")
        print(f"\nOr install llama-cpp-python: pip install llama-cpp-python")
        return

    print(f"\nGGUF model saved to: {gguf_path}")
    print(f"Size: {gguf_path.stat().st_size / 1024**3:.2f} GB")


if __name__ == "__main__":
    main()
