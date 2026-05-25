# Using Docling Heron as the Layout Model

RailReader2 ships with **PP-DocLayoutV3** as its bundled layout-detection
model. Starting with v3.13, you can optionally switch to **Docling Heron**,
the layout model used by IBM's Docling pipeline.

This guide explains when to switch, how to install Heron, and how to switch
back if it doesn't suit your documents.

---

## Why Heron?

PP-DocLayoutV3 is the default for good reason: it's accurate on
academic-style PDFs, small enough to ship inside the installer (~50 MB),
and fast on CPU. Most users should leave it alone.

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

The trade-offs:

- **Larger file** — ~164 MB, vs. ~50 MB for PP-DocLayoutV3.
- **Not in the installer** — you must download it separately
  (license: Apache-2.0).
- **Slightly slower per page** — Heron is an RT-DETRv2 model and has more
  layers than PP. Exact margin depends on your CPU.

PP-DocLayoutV3 remains the default. Heron is opt-in.

---

## Install the Heron model

The model is published on Hugging Face by the Docling project at
[`docling-project/docling-layout-heron-onnx`](https://huggingface.co/docling-project/docling-layout-heron-onnx).

You need exactly one file: the ONNX weights, renamed to
`docling-layout-heron.onnx`.

### Option A: helper script (Linux / macOS, source build)

If you have the [RailReaderCore](https://github.com/sjvrensburg/RailReaderCore)
repo checked out (or are happy to clone it), the canonical helper script
does the right thing:

```bash
git clone https://github.com/sjvrensburg/RailReaderCore.git
./RailReaderCore/scripts/download-model.sh heron
# Produces ./models/docling-layout-heron.onnx
```

Move that file to one of the locations in the **probe order** below.

### Option B: direct download (any platform)

```bash
curl -L -o docling-layout-heron.onnx \
  https://huggingface.co/docling-project/docling-layout-heron-onnx/resolve/main/model.onnx
```

On Windows PowerShell:

```powershell
Invoke-WebRequest `
  -Uri "https://huggingface.co/docling-project/docling-layout-heron-onnx/resolve/main/model.onnx" `
  -OutFile "docling-layout-heron.onnx"
```

The filename **must** be exactly `docling-layout-heron.onnx` — that's
what RailReader2 looks for.

### Where to put the file

RailReader2 searches the following locations, in order. The **first match
wins**. The recommended location is alongside your existing `config.json`:

| OS      | Recommended path                                                       |
|---------|------------------------------------------------------------------------|
| Linux   | `~/.config/railreader2/models/docling-layout-heron.onnx`              |
| macOS   | `~/Library/Application Support/railreader2/models/docling-layout-heron.onnx` |
| Windows | `%APPDATA%\railreader2\models\docling-layout-heron.onnx`              |

If the `models/` subdirectory doesn't exist yet, create it:

```bash
# Linux
mkdir -p ~/.config/railreader2/models
mv docling-layout-heron.onnx ~/.config/railreader2/models/
```

```powershell
# Windows
New-Item -ItemType Directory -Force -Path "$env:APPDATA\railreader2\models"
Move-Item docling-layout-heron.onnx "$env:APPDATA\railreader2\models\"
```

Full probe order (checked top-down; first existing file wins):

1. `<install-dir>/models/docling-layout-heron.onnx`
2. `$APPDIR/models/docling-layout-heron.onnx` (inside an AppImage)
3. `<config-dir>/models/docling-layout-heron.onnx` *(recommended)*
4. `<LocalAppData>/railreader2/models/docling-layout-heron.onnx`
5. `./models/docling-layout-heron.onnx` (current directory)
6. `../models/docling-layout-heron.onnx`, `../../models/...`, `../../../models/...`

---

## Enable Heron

You have two ways to switch. Both require **restarting RailReader2** to
take effect.

### From Settings (recommended)

1. **File → Settings…** (or `Ctrl+,`).
2. Open the **Advanced** tab.
3. Under **Layout Model**, change the dropdown from
   *PP-DocLayoutV3 (default, bundled)* to *Docling Heron*.
4. The status line below the dropdown tells you whether the file was
   found, and the path it resolved to.
5. Close the Settings window and restart the app.

If the status line says *"Heron model not found"*, the file isn't at any
of the probe paths — re-check the filename (must be exactly
`docling-layout-heron.onnx`) and the location.

### By editing `config.json`

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

If it already exists (you have a custom layout model configured, for
example), just add or change the `builtin_analyzer` key:

```json
{
  "enabled": false,
  "model_path": null,
  "mapping_path": null,
  "builtin_analyzer": "Heron"
}
```

Valid values: `"PpDocLayoutV3"` (default) or `"Heron"`. The setting is
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
   mentioning `Starting worker with model: ...docling-layout-heron.onnx`.
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

## Switch back to PP-DocLayoutV3

Set the dropdown in **Settings → Advanced → Layout Model** back to
*PP-DocLayoutV3 (default, bundled)*, or edit `custom_layout_model.json`
and set `"builtin_analyzer": "PpDocLayoutV3"`. Restart.

You can leave `docling-layout-heron.onnx` in place — RailReader2 ignores
it unless you re-enable Heron. Delete it if you want to free the ~164 MB.

---

## Troubleshooting

**"Heron model not found" appears even though I downloaded it.**
The filename must be exactly `docling-layout-heron.onnx`. Hugging Face
gives you `model.onnx` by default — rename it. Also check it landed in
one of the probe paths above (the *recommended* path is usually
simplest).

**App seems to load PP-DocLayoutV3 even though I picked Heron.**
RailReader2 falls back to PP if it can't find Heron, rather than dropping
into layout-less mode. Check the diagnostic log for a line ending in
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
on the order of 1.5–2×, depending on your CPU. If this is a problem, stay
on PP-DocLayoutV3 or only enable Heron for specific documents.

**The Settings status line says "found", but the log says "not found".**
The Settings dialog re-probes on open; the log was written at app
startup. If you placed the file *after* the app started, restart it.
