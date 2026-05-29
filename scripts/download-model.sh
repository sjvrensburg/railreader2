#!/bin/bash
# Download ONNX models from HuggingFace
set -e

MODEL_DIR="$(dirname "$0")/../models"
mkdir -p "$MODEL_DIR"

HERON_PATH="$MODEL_DIR/docling-layout-heron-int8.onnx"
PPV3_PATH="$MODEL_DIR/PP-DocLayoutV3.onnx"

# Docling Heron (INT8) — default layout model
if [ -f "$HERON_PATH" ]; then
    echo "Heron-INT8 model already exists at $HERON_PATH"
else
    echo "Downloading Docling Heron INT8 (~66 MB)..."
    curl -L -o "$HERON_PATH" \
        "https://huggingface.co/stefanj0/docling-layout-heron-int8-onnx/resolve/main/docling-layout-heron-int8.onnx"
    echo "Downloaded to $HERON_PATH ($(du -h "$HERON_PATH" | cut -f1))"
fi

# PP-DocLayoutV3 — bundled fallback
if [ -f "$PPV3_PATH" ]; then
    echo "PP-DocLayoutV3 model already exists at $PPV3_PATH"
else
    echo "Downloading PP-DocLayoutV3 (~50 MB)..."
    curl -L -o "$PPV3_PATH" \
        "https://huggingface.co/alex-dinh/PP-DocLayoutV3-ONNX/resolve/main/PP-DocLayoutV3.onnx"
    echo "Downloaded to $PPV3_PATH ($(du -h "$PPV3_PATH" | cut -f1))"
fi
