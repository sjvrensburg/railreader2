#!/bin/bash
# Download ONNX models from HuggingFace
set -e

MODEL_DIR="$(dirname "$0")/../models"
mkdir -p "$MODEL_DIR"

# PP-DocLayoutV3 - layout detection + reading order
LAYOUT_PATH="$MODEL_DIR/PP-DocLayoutV3.onnx"
if [ -f "$LAYOUT_PATH" ]; then
    echo "Layout model already exists at $LAYOUT_PATH"
else
    echo "Downloading PP-DocLayoutV3.onnx..."
    curl -L -o "$LAYOUT_PATH" \
        "https://huggingface.co/alex-dinh/PP-DocLayoutV3-ONNX/resolve/main/PP-DocLayoutV3.onnx"
    echo "Downloaded to $LAYOUT_PATH ($(du -h "$LAYOUT_PATH" | cut -f1))"
fi
