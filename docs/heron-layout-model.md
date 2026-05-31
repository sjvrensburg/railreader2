# Docling Heron Layout Model

RailReader2 uses **Docling Heron (INT8)** as its default layout-detection
model. Starting with v3.14, Heron replaces PP-DocLayoutV3 as the bundled
default for new installations.

This guide explains how to install the model, when you might switch to
PP-DocLayoutV3, and how to switch back if needed.

---

## Why Heron?

Heron's appeal is its **class space**. It was trained on a broader set of
document elements:

> caption, footnote, formula, list_item, page_footer, page_header,
> picture, section_header, table, text, title, document_index, code,
> checkbox, form, key_value_region

If you spend a lot of time in any of the following document types, Heron
may give noticeably better detections:

- **Code-heavy technical docs** — Heron has a dedicated `code` class;
  PP labels code blocks as plain text.
- **Forms and checklists** — `form`, `checkbox`, and `key_value_region`
  are first-class.
- **Books and reports with proper indices** — `document_index` is its
  own class.
- **Multi-language pages** — Heron's training data is broader.

PP-DocLayoutV3 remains available as an alternative:

- **Native reading order** — PP-DocLayoutV3 includes reading order in its
  model output. Heron does not; RailReader2 determines reading order via
  the XY-Cut++ algorithm instead.
- **Smaller file** — ~50 MB, vs. ~66 MB for Heron-INT8.
- **Faster per page** — PP is a lighter model. Exact margin depends on
  your CPU.

---

## Install the Heron model

The model is published on Hugging Face at
[`stefanj0/docling-layout-heron-int8-onnx`](https://huggingface.co/stefanj0/docling-layout-heron-int8-onnx)
(INT8 quantised, ~66 MB).

You need exactly one file: the ONNX weights. The filename must be
`docling-layout-heron-int8.onnx`.

> **Backward compat:** If you previously downloaded the FP32 model as
> `docling-layout-heron.onnx`, RailReader2 will still find it. You can
> continue using the FP32 version, or replace it with the INT8 variant
> for a smaller download with negligible accuracy loss.

### Option A: helper script (Linux / macOS, source build)

```bash
./scripts/download-model.sh heron
# Produces ./models/docling-layout-heron-int8.onnx
```

Move that file to one of the locations in the **probe order** below.

### Option B: direct download (any platform)

```bash
curl -L -o docling-layout-heron-int8.onnx \
  https://huggingface.co/stefanj0/docling-layout-heron-int8-onnx/resolve/main/docling-layout-heron-int8.onnx
```

On Windows PowerShell:

```powershell
Invoke-WebRequest `
  -Uri "https://huggingface.co/stefanj0/docling-layout-heron-int8-onnx/resolve/main/docling-layout-heron-int8.onnx" `
  -OutFile "docling-layout-heron-int8.onnx"
```

### Where to put the file

RailReader2 searches the following locations, in order. The **first match
wins**. The recommended location is alongside your existing `config.json`:

| OS      | Recommended path                                                       |
|---------|------------------------------------------------------------------------|
| Linux   | `~/.config/railreader2/models/docling-layout-heron-int8.onnx`              |
| macOS   | `~/Library/Application Support/railreader2/models/docling-layout-heron-int8.onnx` |
| Windows | `%APPDATA%\railreader2\models\docling-layout-heron-int8.onnx`              |

If the `models/` subdirectory doesn't exist yet, create it:

```bash
# Linux
mkdir -p ~/.config/railreader2/models
mv docling-layout-heron-int8.onnx ~/.config/railreader2/models/
```

```powershell
# Windows
New-Item -ItemType Directory -Force -Path "$env:APPDATA\railreader2\models"
Move-Item docling-layout-heron-int8.onnx "$env:APPDATA\railreader2\models\"
```

Full probe order (checked top-down; first existing file wins):

1. `<install-dir>/models/docling-layout-heron-int8.onnx`
2. `$APPDIR/models/docling-layout-heron-int8.onnx` (inside an AppImage)
3. `<config-dir>/models/docling-layout-heron-int8.onnx` *(recommended)*
4. `<LocalAppData>/railreader2/models/docling-layout-heron-int8.onnx`
5. `./models/docling-layout-heron-int8.onnx` (current directory)
6. `../models/docling-layout-heron-int8.onnx`, `../../models/...`, `../../../models/...`
7. *(fallback)* All of the above with the legacy name `docling-layout-heron.onnx`

---

## Enable / disable Heron

Heron is the default for new installations. If you previously used
PP-DocLayoutV3, you can switch models at any time. Both methods require
**restarting RailReader2** to take effect.

### From Settings (recommended)

1. **File → Settings…** (or `Ctrl+,`).
2. Open the **Advanced** tab.
3. Under **Layout Model**, the dropdown shows the active model.
4. The status line below the dropdown tells you whether the file was
   found, and the path it resolved to.
5. Close the Settings window and restart the app.

If the status line says *"Heron model not found"*, the file isn't at any
of the probe paths — re-check the filename (must be exactly
`docling-layout-heron-int8.onnx` or `docling-layout-heron.onnx`) and the location.

### By editing the config file

Useful for headless setups or scripted installs. The config sidecar lives
at:

| OS      | Path                                                          |
|---------|---------------------------------------------------------------|
| Linux   | `~/.config/railreader2/custom_layout_model.json`             |
| macOS   | `~/Library/Application Support/railreader2/custom_layout_model.json` |
| Windows | `%APPDATA%\railreader2\custom_layout_model.json`             |

If the file doesn't exist yet, create it with:

```json
{
  "enabled": false,
  "builtin_analyzer": "Heron"
}
```

Valid values: `"Heron"` (default) or `"PpDocLayoutV3"`. The setting is
case-sensitive.

> **Note:** If you have a custom layout model enabled (`"enabled": true`
> with valid `model_path` and `mapping_path`), it takes precedence over
> `builtin_analyzer`. The built-in selection only kicks in when no custom
> model is active.

Restart RailReader2 after editing.

---

## Verify it's working

After restart, open any PDF. There are a few quick checks:

1. **Diagnostic log.** *Help → Export Diagnostic Log…* — look for a line
   mentioning `Starting worker with model: ...docling-layout-heron-int8.onnx`.
   You should also see `[Heron ONNX]` debug lines on first inference.

2. **Debug overlay.** Press `Shift+D` to toggle the layout debug
   overlay. A small *Model: Docling Heron* badge appears in the
   top-left of the page, and block labels reflect Heron's class space —
   for example, a chapter heading will be tagged `section_header`
   (Heron) rather than `paragraph_title` (PP-DocLayoutV3).

3. **Try the CLI.** The `RailReader2.Cli structure` command picks up
   the same config, and the JSON output's class names come straight from
   the active model.

---

## Switch to PP-DocLayoutV3

Set the dropdown in **Settings → Advanced → Layout Model** to
*PP-DocLayoutV3 (bundled)*, or edit `custom_layout_model.json`
and set `"builtin_analyzer": "PpDocLayoutV3"`. Restart.

You can leave `docling-layout-heron-int8.onnx` in place — RailReader2 ignores
it unless Heron is the selected model. Delete it if you want to free the ~66 MB.

---

## Troubleshooting

**"Heron model not found" appears even though I downloaded it.**
The filename must be exactly `docling-layout-heron-int8.onnx` (or
`docling-layout-heron.onnx` for the legacy FP32 variant). Hugging Face
gives you `docling-layout-heron-int8.onnx` by default — no rename needed.
Also check it landed in one of the probe paths above (the *recommended*
path is usually simplest).

**App seems to load PP-DocLayoutV3 even though I picked Heron.**
RailReader2 falls back to PP-DocLayoutV3 if it can't find Heron, rather than
dropping into layout-less mode. Check the diagnostic log for a line ending in
`falling back to PP-DocLayoutV3`. Usually means the file isn't where it
needs to be — see the previous item.

**Layout detection quality changed after switching.**
Expected. The two models were trained on different data and have
different strengths. The best way to decide is to A/B test on a sample of
your real documents — switch back and forth and compare debug-overlay
results on the same pages. If one is clearly better for your work, stick
with it. If you find a case where one wins and the other loses
consistently, please [open an issue](https://github.com/sjvrensburg/railreader2/issues)
with the PDF so the maintainers can take a look.

**Inference feels slower with Heron.**
Heron has more parameters than PP-DocLayoutV3 — expect a per-page slowdown
on the order of 1.5–2×, depending on your CPU. If this is a problem, switch
to PP-DocLayoutV3 or only enable Heron for specific documents.

**The Settings status line says "found", but the log says "not found".**
The Settings dialog re-probes on open; the log was written at app
startup. If you placed the file *after* the app started, restart it.
