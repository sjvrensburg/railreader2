# Stylus support

railreader2 has basic, no-pressure stylus support on the desktop. It is **X11-first**: it is
developed and validated on X11, where Avalonia's tablet input is reliable. This document records
what the feature does, the design rule that keeps it safe, and the known Wayland/XWayland caveats.

## What it does (desktop v1, basic — no pressure)

All of this lives in `Views/ViewportPanel.cs` (App-side). RailReaderCore's annotation engine is
already device-agnostic, so basic stylus needs **no Core change**.

1. **Pen draws in annotation mode.** A pen draws exactly like the mouse — this needs no special
   code, because the mouse already drives the markup tools through `HandleAnnotationPointerDown/
   Move/Up`. Outside annotation mode the pen is just a pointer (browse, select, Ctrl-free-pan).
2. **Eraser tip → Eraser tool.** When a pen's eraser tip touches down in annotation mode, the
   active tool is temporarily switched to `AnnotationTool.Eraser` for that stroke and restored on
   lift. Flip-to-erase — the most intuitive stylus gesture.
3. **Barrel button → free-pan/inspect.** Holding the pen's barrel button while dragging pans the
   page (equivalent to Ctrl+drag), even inside annotation mode, so you can reposition without
   inking. In rail mode this pauses the rail and resumes on lift, mirroring Ctrl+drag free-pan.

**Deliberately deferred:** pressure → variable stroke width. It needs a per-point-width model in
RailReaderCore (and is the property most likely to be mangled by XWayland — see below), so it is
out of scope for the desktop v1. It is a natural fit for the planned iPad/Apple-Pencil app, which
shares the same Core.

**Out of scope entirely (mobile-app territory):** finger pan/pinch gestures, pen-vs-finger device
routing, and palm rejection. railreader2 is the desktop app; touch-first interaction belongs to the
separate future mobile app built on the shared Core.

## The design rule: pen detection only ADDS, never gates

The single rule that makes stylus support safe on every platform:

> Pen-identity (`Pointer.Type == Pen`, `IsEraser`, `IsBarrelButtonPressed`) is only ever read to
> *add* a convenience. It never *gates* existing functionality.

Mouse-drawing, manual tool selection, and Ctrl+drag pan remain the always-available baseline. The
pen niceties are layered on top. Consequence: there is no input path — on any compositor, X11 or
Wayland — where a user ends up worse off than they would be with no stylus at all. The worst case is
that a pen behaves like a mouse and the conveniences are silently inert.

## Wayland / XWayland caveats

**Status (as of 2026):** Avalonia has no shipped native Wayland backend. Avalonia 12 lays the
groundwork, but native Wayland is in **private preview**, rolling out embedded-first with desktop
compositors (GNOME/KDE/Sway) to follow — no general-availability date. Until then, an Avalonia app
running in a Wayland session reaches the display server through **XWayland**, the X11 compatibility
layer.

That means stylus events take a lossy path we don't control:

```
pen → libinput → Wayland compositor (tablet-v2) → XWayland (→ X11 input) → Avalonia X11 backend
```

The pen-identity metadata is the first thing to degrade across that translation. Concretely:

- **Hover cursor may be absent.** A documented XWayland limitation: the stylus cursor is not shown
  on proximity/hover — you don't see where you're about to mark until the pen touches down. Cosmetic
  but real for precise placement. It disappears when Avalonia's native Wayland backend ships.
- **`Type == Pen`, `IsEraser`, barrel may not survive faithfully** and behaviour is
  compositor-dependent. If they don't come through, the eraser-tip and barrel-button conveniences
  silently do nothing and the pen behaves like a mouse (use the Eraser tool / Ctrl+drag manually).
- **Pressure is the most likely to be flattened.** A second reason it's deferred.

### What this means for usability

**Usability risk is essentially zero.** Because a pen always arrives as at least a pointer (motion +
button state), everything that matters — reading, rail mode, pan/zoom, navigation, text selection,
and even drawing annotations — works with a stylus on Wayland regardless. No Wayland user is locked
out or degraded below the no-stylus experience. What may be missing is the pen *conveniences*, and
their absence is graceful by construction (the design rule above).

**The real risk is silent feature-degradation on a platform we can't fully test from an X11 box.**
Treat the pen conveniences as best-effort on Wayland, and validate any changes under an actual
Wayland session (GNOME and KDE, via XWayland) rather than trusting an X11-only test.

### Watch / revisit

When Avalonia's native Wayland backend leaves private preview, the XWayland rough edges (hover
cursor, metadata fidelity) go away and richer pen features (hover, pressure) become worth
revisiting. Joining Avalonia's Wayland private preview is a (heavyweight) option if Wayland-quality
stylus ever becomes strategically important.
