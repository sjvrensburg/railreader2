#!/usr/bin/python3
"""Live AT-SPI dump for RailReader2 — the cross-check the headless peer audit can't do.

MUST be run with the SYSTEM python (pyatspi/dogtail are apt packages, invisible to conda):

    /usr/bin/python3 atspi-dump.py

Prereqs (in another terminal, BEFORE running this):
    gsettings set org.gnome.desktop.interface toolkit-accessibility true
    railreader2 ~/some.pdf &        # or: dotnet run -c Release --project src/RailReader2 -- ~/some.pdf

What it answers: do menu items expose a callable AT-SPI Action (e.g. 'click'),
and does the document/viewport node carry the live state. Prints the full tree
with an actions=[...] column, then a focused summary of the menu commands.
"""
import sys
try:
    import pyatspi
except ModuleNotFoundError:
    sys.exit("pyatspi not found — run with /usr/bin/python3 (not conda's python).")


def actions_of(node):
    try:
        ai = node.queryAction()
    except NotImplementedError:
        return []
    return [ai.getName(i) for i in range(ai.nActions)]


def find_app():
    # RailReader2 registers under Avalonia's generic AT-SPI name "Avalonia Application"
    # (the backend doesn't derive it from the assembly), so match that too.
    desktop = pyatspi.Registry.getDesktop(0)
    names = []
    for app in desktop:
        try:
            n = app.name or ""
        except Exception:
            continue
        names.append(n)
        low = n.lower()
        if "railreader" in low or "avalonia" in low:
            print(f"(matched a11y app: {n!r})")
            return app
    sys.exit("RailReader2 not found on the a11y bus.\n"
             "  Is it running, and did you set toolkit-accessibility true first?\n"
             f"  Registered apps: {names}")


def walk(node, depth=0, out=None):
    if out is None:
        out = []
    try:
        role = node.getRoleName()
        name = node.name
    except Exception:
        return out
    acts = actions_of(node)
    acts_s = f"  actions={acts}" if acts else ""
    out.append((depth, role, name, acts))
    print(f"{'  ' * depth}{role}: {name!r}{acts_s}")
    for child in node:
        if child is not None:
            walk(child, depth + 1, out)
    return out


app = find_app()
print(f"=== AT-SPI tree for {app.name!r} ===")
nodes = walk(app)

print("\n=== Menu commands — do they expose a callable Action? ===")
menu_items = [(d, r, n, a) for (d, r, n, a) in nodes if r in ("menu item", "check menu item", "radio menu item")]
if not menu_items:
    print("  (no menu items in the tree — open a menu, or the menu bar didn't register)")
for _d, role, name, acts in menu_items:
    flag = "OK" if acts else "NO ACTION"
    print(f"  [{flag:9}] {role:16} {name!r:40} actions={acts}")

with_action = sum(1 for *_ , a in menu_items if a)
print(f"\nSummary: {with_action} of {len(menu_items)} menu items expose an Action.")
print("Look also above for a 'document'/'panel' node named 'Document viewport' (or the live rail line).")
