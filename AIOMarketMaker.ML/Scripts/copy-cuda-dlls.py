"""
Copy CUDA runtime DLLs from pip-installed nvidia packages into .NET project bin directories.

OnnxRuntime.Gpu includes onnxruntime_providers_cuda.dll, but it depends on system CUDA libraries
(cuBLAS, cuDNN, cuFFT, etc.) that must be colocated with the application DLL.

Run after each `dotnet build` or `dotnet test --no-build`:
    python AIOMarketMaker.ML/Scripts/copy-cuda-dlls.py

Prerequisites:
    python -m pip install nvidia-cublas-cu12 nvidia-cuda-runtime-cu12 nvidia-cudnn-cu12 \
        nvidia-cufft-cu12 nvidia-cusparse-cu12 nvidia-curand-cu12 nvidia-cusolver-cu12
"""

import glob
import os
import shutil
import sys

def find_nvidia_packages():
    """Find the nvidia pip package directory."""
    for site_dir in sys.path:
        nv = os.path.join(site_dir, "nvidia")
        if os.path.isdir(nv):
            return nv
    return None

def collect_cuda_dlls(nv_dir):
    """Collect all DLL paths from nvidia package bin directories."""
    dlls = []
    for pkg in os.listdir(nv_dir):
        bin_dir = os.path.join(nv_dir, pkg, "bin")
        if os.path.isdir(bin_dir):
            dlls.extend(glob.glob(os.path.join(bin_dir, "*.dll")))
    return dlls

def copy_dlls(dlls, target_dir):
    """Copy DLLs to target directory, skipping up-to-date files."""
    copied = 0
    for dll in dlls:
        name = os.path.basename(dll)
        dest = os.path.join(target_dir, name)
        src_mtime = os.path.getmtime(dll)
        if os.path.exists(dest) and os.path.getmtime(dest) >= src_mtime:
            continue
        shutil.copy2(dll, target_dir)
        copied += 1
    return copied

def main():
    nv_dir = find_nvidia_packages()
    if not nv_dir:
        print("ERROR: nvidia pip packages not found. Install with:")
        print("  python -m pip install nvidia-cublas-cu12 nvidia-cuda-runtime-cu12 nvidia-cudnn-cu12 \\")
        print("      nvidia-cufft-cu12 nvidia-cusparse-cu12 nvidia-curand-cu12 nvidia-cusolver-cu12")
        sys.exit(1)

    dlls = collect_cuda_dlls(nv_dir)
    if not dlls:
        print(f"ERROR: No DLLs found in {nv_dir}")
        sys.exit(1)

    print(f"Found {len(dlls)} CUDA DLLs in {nv_dir}")

    # Project bin directories that use OnnxRuntime.Gpu
    # Script is at AIOMarketMaker.ML/Scripts/ — go up 2 levels to reach solution dir
    script_dir = os.path.dirname(os.path.abspath(__file__))
    solution_dir = os.path.dirname(os.path.dirname(script_dir))

    targets = [
        os.path.join(solution_dir, "AIOMarketMaker.Tests", "bin", "Debug", "net8.0"),
        os.path.join(solution_dir, "AIOMarketMaker.Etl", "bin", "Debug", "net8.0"),
    ]

    for target in targets:
        if not os.path.isdir(target):
            proj_name = os.path.basename(os.path.dirname(os.path.dirname(os.path.dirname(target))))
            print(f"  SKIP {proj_name} (not built)")
            continue
        copied = copy_dlls(dlls, target)
        proj = os.path.basename(os.path.dirname(os.path.dirname(os.path.dirname(target))))
        print(f"  {proj}: {copied} DLLs copied ({len(dlls) - copied} up-to-date)")

    print("Done.")

if __name__ == "__main__":
    main()
