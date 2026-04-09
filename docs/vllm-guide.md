# VLM Setup Guide

RailReader2's **Copy as LaTeX** feature (`Ctrl+L`) sends detected layout blocks to a Vision Language Model (VLM) for conversion. It works with any OpenAI-compatible API — cloud or local.

**For most users, a cloud API like OpenAI is the easiest option and produces the best results.** Local models are an alternative for users who need privacy or want to avoid API costs.

---

## Option 1: OpenAI (recommended for accuracy)

OpenAI's models produce the most reliable LaTeX and Markdown output, especially for complex multi-line equations and dense tables.

### Setup

1. Get an API key from [platform.openai.com/api-keys](https://platform.openai.com/api-keys)
2. Open **Settings > VLM** in RailReader2 and enter:

| Field    | Value                        |
|----------|------------------------------|
| Endpoint | `https://api.openai.com/v1`  |
| Model    | `gpt-4.1-mini`               |
| API Key  | your API key                 |

3. Click **Test Connection** to verify, then close Settings.

### Which model?

| Model          | Quality   | Speed  | Cost       |
|----------------|-----------|--------|------------|
| `gpt-4.1`      | Best      | Slower | ~$0.01/image |
| `gpt-4.1-mini` | Very good | Fast   | ~$0.001/image |
| `gpt-4o`       | Good      | Fast   | ~$0.005/image |

`gpt-4.1-mini` is the best starting point — fast, cheap, and accurate enough for most equations and tables.

> **Privacy note:** cloud APIs send cropped block images to external servers. For sensitive documents, use a local model instead.

---

## Option 2: Ollama (simplest local setup)

[Ollama](https://ollama.com) is the easiest way to run a vision model locally. No data leaves your machine.

### Setup

```bash
ollama pull qwen2.5-vl:7b
ollama serve
```

Then configure RailReader2:

| Field    | Value                        |
|----------|------------------------------|
| Endpoint | `http://localhost:11434/v1`   |
| Model    | `qwen2.5-vl:7b`              |
| API Key  | *(leave blank)*              |

Requires a GPU with 8+ GB VRAM. Results are good for general use but less reliable than cloud APIs for complex equations.

---

## Option 3: vLLM + LightOnOCR (local, specialised for OCR)

[vLLM](https://docs.vllm.ai/) is a high-performance inference server. Paired with [LightOnOCR-2-1B](https://huggingface.co/lightonai/LightOnOCR-2-1B), it provides fast local OCR specialised for equations, tables, and document layouts. This is a good choice if you process many documents and want consistent local performance.

### Prerequisites

- Python 3.10+
- A GPU with 8+ GB VRAM (NVIDIA, CUDA required)
- [uv](https://docs.astral.sh/uv/) (recommended) or pip

### Install vLLM

```bash
uv venv .venv
source .venv/bin/activate
uv pip install vllm "transformers>=5.0.0"
```

### Start the server

**16+ GB VRAM:**

```bash
vllm serve lightonai/LightOnOCR-2-1B \
  --limit-mm-per-prompt '{"image": 1}' \
  --mm-processor-cache-gb 0 \
  --no-enable-prefix-caching
```

**8 GB GPUs (e.g. RTX 3060, RTX A2000):**

```bash
vllm serve lightonai/LightOnOCR-2-1B \
  --limit-mm-per-prompt '{"image": 1}' \
  --mm-processor-cache-gb 0 \
  --no-enable-prefix-caching \
  --enforce-eager \
  --max-model-len 8192 \
  --gpu-memory-utilization 0.95
```

The server starts on `http://localhost:8000/v1` by default.

### Configure RailReader2

| Field    | Value                       |
|----------|-----------------------------|
| Endpoint | `http://localhost:8000/v1`  |
| Model    | `lightonai/LightOnOCR-2-1B` |
| API Key  | *(leave blank)*             |

Click **Test Connection** to verify.

### Tips

- LightOnOCR handles equations, tables, multi-column layouts, and general OCR well.
- For best results, the model expects images at ~200 DPI (RailReader2 renders at 300 DPI, which works fine).

---

## Usage

Once configured, use any of these to copy a block:

- **`Ctrl+L`** — copies the current rail block (auto-selects LaTeX for equations, Markdown for tables, description for figures)
- **`Ctrl+right-click`** any detected block — opens a context menu with Copy as LaTeX, Copy as Markdown, Copy Description, and Copy Image
- **Edit > Copy Block as LaTeX** — same as `Ctrl+L`

A toast notification shows progress ("Sending to VLM...") and confirms when the result is on the clipboard.
