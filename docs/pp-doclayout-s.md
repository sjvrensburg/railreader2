# Using PP-DocLayout-S as the Layout Model

RailReader2 ships with **Docling Heron-INT8** as its bundled layout-detection
model (~66 MB). You can optionally switch to **PP-DocLayout-S**, a lightweight
sibling from the PaddleOCR family — ~4.7 MB and useful for resource-constrained
environments.

This guide explains when to switch, how to install PP-S, and how to switch
back.

---

## Why PP-DocLayout-S?

Docling Heron-INT8 is the default for good reason: it's highly accurate,
ships bundled (~66 MB), and detects a broad class space including code and forms.
Reading order is determined via the XY-Cut++ algorithm. Most users should leave
it alone.

PP-DocLayout-S's appeal is its **size**:

- **~4.7 MB ONNX** vs. Heron's ~66 MB and PP-DocLayoutV3's ~50 MB.
- **Faster on CPU** — PicoDet/GFL is a much smaller backbone than
  Heron's RT-DETRv2 or PP-V3's RT-DETR.
- **Same 23-class document schema** as PP-V3 (minus the inline-formula and
  vertical-text classes, with `chart_title` and `table_title` first-class).

The trade-offs:

- **Lower recall on small text.** PP-S is weaker than Heron and V3 on
  bibliography rows, footnotes, and dense small print. The analyzer
  works around this by rasterising at 1920 px on the longest edge and
  downsizing to the model's 480×480 input internally — going straight
  to 480 loses a lot of small text.
- **No model-provided reading order.** RailReader2 pairs PP-S with the
  built-in XY-Cut++ resolver, same as Heron.
- **Not in the installer.** You must download it separately
  (license: Apache-2.0).

Docling Heron-INT8 is the default. PP-S is opt-in, primarily useful if
you care about startup time and disk footprint and your documents aren't
small-text-heavy.

---

## Install the PP-DocLayout-S model

A `paddle2onnx`-exported ONNX is published on Hugging Face at
[`stefanj0/PP-DocLayout-S-ONNX`](https://huggingface.co/stefanj0/PP-DocLayout-S-ONNX)
(the upstream PaddlePaddle repo only ships Paddle-native files).

You need exactly one file: the ONNX weights, named `pp_doclayout_s.onnx`.

### Option A: helper script (Linux / macOS, source build)

If you have the [RailReaderCore](https://github.com/sjvrensburg/RailReaderCore)
repo checked out (or are happy to clone it), the canonical helper script
does the right thing:

```bash
git clone https://github.com/sjvrensburg/RailReaderCore.git
./RailReaderCore/scripts/download-model.sh pps
# Produces ./models/pp_doclayout_s.onnx
```

Move that file to one of the locations in the **probe order** below.

### Option B: direct download (any platform)

```bash
curl -L -o pp_doclayout_s.onnx \
  https://huggingface.co/stefanj0/PP-DocLayout-S-ONNX/resolve/main/pp_doclayout_s.onnx
```

On Windows PowerShell:

```powershell
Invoke-WebRequest `
  -Uri "https://huggingface.co/stefanj0/PP-DocLayout-S-ONNX/resolve/main/pp_doclayout_s.onnx" `
  -OutFile "pp_doclayout_s.onnx"
```

The filename **must** be exactly `pp_doclayout_s.onnx` — that's what
RailReader2 looks for.

### Where to put the file

RailReader2 searches the following locations, in order. The **first match
wins**. The recommended location is alongside your existing `config.json`:

| OS      | Recommended path                                                       |
|---------|------------------------------------------------------------------------|
| Linux   | `~/.config/railreader2/models/pp_doclayout_s.onnx`                    |
| macOS   | `~/Library/Application Support/railreader2/models/pp_doclayout_s.onnx` |
| Windows | `%APPDATA%\railreader2\models\pp_doclayout_s.onnx`                    |

If the `models/` subdirectory doesn't exist yet, create it:

```bash
# Linux
mkdir -p ~/.config/railreader2/models
mv pp_doclayout_s.onnx ~/.config/railreader2/models/
```

```powershell
# Windows
New-Item -ItemType Directory -Force -Path "$env:APPDATA\railreader2\models"
Move-Item pp_doclayout_s.onnx "$env:APPDATA\railreader2\models\"
```

Full probe order (checked top-down; first existing file wins):

1. `<install-dir>/models/pp_doclayout_s.onnx`
2. `$APPDIR/models/pp_doclayout_s.onnx` (inside an AppImage)
3. `<config-dir>/models/pp_doclayout_s.onnx` *(recommended)*
4. `<LocalAppData>/railreader2/models/pp_doclayout_s.onnx`
5. `./models/pp_doclayout_s.onnx` (current directory)
6. `../models/pp_doclayout_s.onnx`, `../../models/...`, `../../../models/...`

---

## Enable PP-DocLayout-S

You have two ways to switch. Both require **restarting RailReader2** to
take effect.

### From Settings (recommended)

1. **File → Settings…** (or `Ctrl+,`).
2. Open the **Advanced** tab.
3. Under **Layout Model**, change the dropdown from
   *Docling Heron-INT8 (default, bundled)* to *PP-DocLayout-S (lightweight)*.
4. The status line below the dropdown tells you whether the file was
   found, and the path it resolved to.
5. Close the Settings window and restart the app.

If the status line says *"PP-DocLayout-S model not found"*, the file
isn't at any of the probe paths — re-check the filename (must be exactly
`pp_doclayout_s.onnx`) and the location.

### By editing `custom_layout_model.json`

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
  "builtin_analyzer": "PpDocLayoutS"
}
```

Valid values: `"Heron"` (default), `"PpDocLayoutV3"`, or
`"PpDocLayoutS"`. The setting is case-sensitive.

> **Note:** If you have a custom layout model enabled (`"enabled": true`
> with valid `model_path` and `mapping_path`), it takes precedence over
> `builtin_analyzer`.

Restart RailReader2 after editing.

---

## Verify it's working

After restart, open any PDF:

1. **Diagnostic log.** *Help → Export Diagnostic Log…* — look for a line
   mentioning `Starting worker with model: ...pp_doclayout_s.onnx`.
   You should also see `[PP-S ONNX]` debug lines on first inference.

2. **Debug overlay.** Press `Shift+D` to toggle the layout debug overlay.
   A small *Model: PP-DocLayout-S* badge appears in the top-left of the
   page.

3. **Try the CLI.** The `RailReader2.Cli structure` command picks up the
   same config sidecar, and the JSON output's class names come straight
   from the active model.

---

## Switch back to Heron-INT8

Set the dropdown in **Settings → Advanced → Layout Model** back to
*Docling Heron-INT8 (default, bundled)*, or edit `custom_layout_model.json`
and set `"builtin_analyzer": "Heron"`. Restart.

You can leave `pp_doclayout_s.onnx` in place — RailReader2 ignores it
unless you re-enable PP-S. It's only ~4.7 MB, so most users just keep it
around.

---

## Troubleshooting

**"PP-DocLayout-S model not found" appears even though I downloaded it.**
The filename must be exactly `pp_doclayout_s.onnx`. Also check it landed
in one of the probe paths above (the *recommended* path is usually
simplest).

**App seems to load PP-DocLayoutV3 even though I picked PP-S.**
RailReader2 falls back to PP-V3 if it can't find PP-S, rather than
dropping into layout-less mode. Check the diagnostic log for a line
ending in `falling back to PP-DocLayoutV3`. Usually means the file isn't
where it needs to be.

**Footnotes / bibliography rows aren't being detected.**
PP-S is genuinely weaker than V3 on small text. The analyzer mitigates
this by rasterising at 1920 px longest-edge before downsizing to 480×480
internally, but it can't fully close the gap. If small-text recall
matters for your work, stay on V3 or try Heron.

**Reading order looks different from V3.**
PP-S doesn't emit reading order — RailReader2 derives it via the
column-aware XY-Cut++ algorithm. For two- and three-column papers the
result is usually correct; for unusual layouts (asides, sidebars,
magazine-style) it can differ from V3's model-provided order.
