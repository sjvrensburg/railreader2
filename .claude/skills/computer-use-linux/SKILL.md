---
name: computer-use-linux
description: Use when you need to observe or drive a local Linux GUI app through the computer-use-linux MCP server — inspect the AT-SPI accessibility tree, list/focus windows, screenshot, click, scroll, type, press keys, or invoke semantic AT-SPI actions. Especially for driving the RailReader2 Avalonia app to verify changes in the real UI.
license: MIT
---

# computer-use-linux

Drive or observe the local Linux desktop through the `computer-use-linux` MCP server.
Adapted for Claude Code from the upstream skill at
`~/computer-use-linux/skills/computer-use-linux/SKILL.md`.

## Tool names in Claude Code

The server is registered as `computer-use-linux`, so its MCP tools are exposed as
`mcp__computer-use-linux__<tool>`:

| Capability | Tool |
| --- | --- |
| Readiness report | `mcp__computer-use-linux__doctor` |
| List running apps | `mcp__computer-use-linux__list_apps` |
| List windows | `mcp__computer-use-linux__list_windows` |
| Focused window | `mcp__computer-use-linux__focused_window` |
| Screenshot + a11y tree for an app | `mcp__computer-use-linux__get_app_state` |
| Screenshot | `mcp__computer-use-linux__screenshot` |
| Click (index / selector / pixel) | `mcp__computer-use-linux__click` |
| Drag | `mcp__computer-use-linux__drag` |
| Scroll | `mcp__computer-use-linux__scroll` |
| Press key / chord | `mcp__computer-use-linux__press_key` |
| Type literal text | `mcp__computer-use-linux__type_text` |
| Invoke AT-SPI action | `mcp__computer-use-linux__perform_action` |
| Set value (field/slider/spinner) | `mcp__computer-use-linux__set_value` |
| Focus a window | `mcp__computer-use-linux__activate_window` |

If the tools are missing from the session, the MCP server was added mid-session —
restart Claude Code in this project to load them.

## Procedure

1. Start every desktop-control session with `doctor`; proceed only if `blockers: []`.
2. If `can_build_accessibility_tree` is false, run `setup` (CLI: `computer-use-linux setup`) and restart the target app.
3. Before targeted input, call `list_windows` / `focused_window` and verify the intended window by title, app id, pid, or wm_class.
4. Prefer semantic targeting from `get_app_state`: element indices or role/name/text/states selectors. Use raw pixel coordinates only for surfaces with no useful a11y tree (e.g. the GPU PDF canvas).
5. For text input, prefer `type_text` with an explicit target selector (`window_id`, `pid`, `app_id`, `wm_class`, `title`, …) rather than relying on current focus.
6. After mutating actions, re-check with `get_app_state` / `focused_window` / a screenshot.

## Driving RailReader2

- Launch: `dotnet run -c Release --project src/RailReader2 -- <path-to-pdf>`
  (run in the background; it's a long-lived GUI process).
- RailReader2 is Avalonia and exposes AT-SPI; `DocumentViewportAutomationPeer` on
  `ViewportPanel` publishes live page/zoom/rail-mode/current-line state, so semantic
  selectors and the automation tree are richer than raw pixels — prefer them.
- The PDF render area is a GPU composition surface (not a11y-introspectable content),
  so inspect *it* via `screenshot`, but use selectors for menus, toolbar, accordion,
  dialogs, and status bar.
- Useful in-app interactions to exercise: rail mode activates above ~3x zoom; `C`
  cycles colour effects; `Ctrl+E` toggles annotation mode; `Shift+D` debug overlay;
  `Ctrl+Shift+I` Index accordion. Confirm via screenshot after each.

## Pitfalls

- Already-running GTK/Qt/Electron apps may need a restart after AT-SPI is enabled.
- GNOME may show a portal prompt on the first screenshot / `get_app_state`.
- Desktop input is stateful — avoid concurrent tool calls against this server.
- `click`, `drag`, `press_key`, `type_text`, `perform_action`, `set_value` change real
  application state. Confirm the target window before destructive input.

## Verification

```bash
computer-use-linux doctor | jq .readiness
```

Ready output has `can_register_mcp_tools`, `can_build_accessibility_tree`,
`can_query_windows`, `can_send_development_input` all `true` and `blockers: []`.
