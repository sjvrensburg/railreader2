#!/bin/bash
# Download ONNX models from HuggingFace.
#
# With no arguments, downloads the two bundled-by-default models (Heron INT8 +
# PP-DocLayoutV3), same as before.
#
# GPU (RailReader.Core.Analysis.WebGpu's native WebGPU execution provider,
# Settings ▸ Advanced ▸ GPU Acceleration) now routes to the plain FP32 model
# for both architectures (RailReaderCore 0.60.2, issue #109 — the FP16 GPU
# exports had a real correctness bug: TopK query-selection instability at the
# decoder's confidence cutoff). PP-DocLayoutV3's GPU path reuses the same file
# already downloaded above — nothing extra needed. Heron's GPU path needs the
# separate plain-FP32 Heron export (`heron`), since Heron's CPU default is the
# INT8 build. The old heron-fp16/ppdoc-fp16 FP16 exports are still downloadable
# for direct/manual use but are no longer what GPU acceleration actually uses.
#
# Usage:
#   ./download-model.sh             # Heron INT8 + PP-DocLayoutV3 (default)
#   ./download-model.sh heron-gpu   # Docling Heron FP32, for GPU (~164 MB)
#   ./download-model.sh heron-fp16  # Docling Heron FP16 (legacy, manual use only, ~86 MB)
#   ./download-model.sh ppdoc-fp16  # PP-DocLayoutV3 FP16 (legacy, manual use only, ~68 MB)
#   ./download-model.sh all         # everything above
set -e

MODEL_DIR="$(dirname "$0")/../models"
mkdir -p "$MODEL_DIR"

download_heron_int8() {
    local path="$MODEL_DIR/docling-layout-heron-int8.onnx"
    if [ -f "$path" ]; then
        echo "Heron-INT8 model already exists at $path"
        return
    fi
    echo "Downloading Docling Heron INT8 (~66 MB)..."
    curl -L -o "$path" \
        "https://huggingface.co/stefanj0/docling-layout-heron-int8-onnx/resolve/main/docling-layout-heron-int8.onnx"
    echo "Downloaded to $path ($(du -h "$path" | cut -f1))"
}

download_ppdoc() {
    local path="$MODEL_DIR/PP-DocLayoutV3.onnx"
    if [ -f "$path" ]; then
        echo "PP-DocLayoutV3 model already exists at $path"
        return
    fi
    echo "Downloading PP-DocLayoutV3 (~50 MB)..."
    curl -L -o "$path" \
        "https://huggingface.co/alex-dinh/PP-DocLayoutV3-ONNX/resolve/main/PP-DocLayoutV3.onnx"
    echo "Downloaded to $path ($(du -h "$path" | cut -f1))"
}

download_heron_gpu() {
    # Plain FP32 Heron — the actual GPU model since RailReaderCore 0.60.2
    # (LayoutModelRegistry.Resolve routes GPU requests here, not to the FP16
    # export). Distinct from download_heron_int8's CPU-optimal file.
    local path="$MODEL_DIR/docling-layout-heron.onnx"
    if [ -f "$path" ]; then
        echo "Docling Heron FP32 already exists at $path"
        return
    fi
    echo "Downloading Docling Heron FP32, for GPU (~164 MB)..."
    curl -L -o "$path" \
        "https://huggingface.co/docling-project/docling-layout-heron-onnx/resolve/main/model.onnx"
    echo "Downloaded to $path ($(du -h "$path" | cut -f1))"
}

download_heron_fp16() {
    # For GPU inference via the native WebGPU execution provider
    # (RailReader.Core.Analysis.WebGpu). Same I/O contract as the INT8 model.
    local path="$MODEL_DIR/docling-layout-heron-fp16.onnx"
    if [ -f "$path" ]; then
        echo "Docling Heron FP16 already exists at $path"
        return
    fi
    echo "Downloading Docling Heron FP16 (~86 MB)..."
    curl -L -o "$path" \
        "https://huggingface.co/stefanj0/docling-layout-heron-fp16-onnx/resolve/main/docling-layout-heron-fp16.onnx"
    echo "Downloaded to $path ($(du -h "$path" | cut -f1))"
}

download_ppdoc_fp16() {
    # For GPU inference via the native WebGPU execution provider
    # (RailReader.Core.Analysis.WebGpu). Same [N,7] detection-tensor contract
    # (including model-supplied reading order) as the FP32 model.
    local path="$MODEL_DIR/PP-DocLayoutV3-fp16.onnx"
    if [ -f "$path" ]; then
        echo "PP-DocLayoutV3 FP16 already exists at $path"
        return
    fi
    echo "Downloading PP-DocLayoutV3 FP16 (~68 MB)..."
    curl -L -o "$path" \
        "https://huggingface.co/stefanj0/PP-DocLayoutV3-FP16-ONNX/resolve/main/PP-DocLayoutV3-fp16.onnx"
    echo "Downloaded to $path ($(du -h "$path" | cut -f1))"
}

case "${1:-default}" in
    default)
        download_heron_int8
        download_ppdoc
        ;;
    heron-gpu|herongpu)
        download_heron_gpu
        ;;
    heron-fp16|heronfp16)
        download_heron_fp16
        ;;
    ppdoc-fp16|ppdoclayoutv3-fp16)
        download_ppdoc_fp16
        ;;
    all)
        download_heron_int8
        download_ppdoc
        download_heron_gpu
        download_heron_fp16
        download_ppdoc_fp16
        ;;
    *)
        echo "Unknown model: $1" >&2
        echo "Usage: $0 [heron-gpu|heron-fp16|ppdoc-fp16|all]" >&2
        exit 1
        ;;
esac
