#!/bin/bash
# Download PP-DocLayoutV3 ONNX model from HuggingFace
set -e

MODEL_DIR="$(dirname "$0")/../models"
MODEL_PATH="$MODEL_DIR/PP-DocLayoutV3.onnx"

if [ -f "$MODEL_PATH" ]; then
    echo "Model already exists at $MODEL_PATH"
    exit 0
fi

mkdir -p "$MODEL_DIR"

echo "Downloading PP-DocLayoutV3.onnx..."
curl -L -o "$MODEL_PATH" \
    "https://huggingface.co/alex-dinh/PP-DocLayoutV3-ONNX/resolve/main/PP-DocLayoutV3.onnx"

echo "Downloaded to $MODEL_PATH ($(du -h "$MODEL_PATH" | cut -f1))"
