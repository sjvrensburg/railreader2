using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RailReader.Core;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using RailReader2;
using RailReader2.ViewModels;
using RailReader2.Views;

// Headless accessibility audit. Boots the REAL RailReader2 UI under Avalonia.Headless
// and walks the Avalonia automation-peer tree — the exact source the AT-SPI (Linux) and
// UIA (Windows) backends project to assistive tech and UI-automation agents. It needs no
// display server, no a11y bus, and no Accerciser: it reads the peers directly.
//
// What it CAN verify (no desktop needed): every control's accessible Name / ControlType /
// AutomationId, which commands expose an Invoke/Toggle pattern (i.e. are actionable by
// name), and the DocumentViewportAutomationPeer's live page/zoom/rail-line description.
// What it CANNOT verify: AT-SPI-backend-specific projection bugs (e.g. LiveSetting being
// dropped on Linux) — for those, run a pyatspi/dogtail dump against the live app on X11.
//
//   dotnet run --project src/Tools/A11yPeerDump.Headless -c Release -- [pdf] [--rail] [--out file.txt]

static string ResolveRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RailReader2.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

string? GetOption(string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == $"--{name}") return args[i + 1];
    return null;
}
bool HasFlag(string name) => args.Contains($"--{name}");

var repoRoot = ResolveRepoRoot();
bool wantRail = HasFlag("rail");
string? outPath = GetOption("out");

// PDF: first positional non-flag arg, else a bundled sample so the viewport + panels
// realise (the chrome alone — menus/toolbar — audits fine without one).
string? pdfArg = args.FirstOrDefault(a => !a.StartsWith("--") && a != outPath
    && a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
string? pdfPath = pdfArg is not null && File.Exists(pdfArg) ? pdfArg
    : Directory.EnumerateFiles(Path.Combine(repoRoot, "experiments", "PDFs"), "*.pdf").FirstOrDefault();

PdfiumResolver.Initialize();
RailReaderLogging.Logger = new ConsoleLogger();

AppBuilder.Configure<App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .SetupWithoutStarting();

void Pump(int ticks)
{
    for (int i = 0; i < ticks; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(8);
    }
}
bool PumpUntil(Func<bool> done, int maxTicks)
{
    for (int i = 0; i < maxTicks && !done(); i++) Pump(1);
    return done();
}

var appConfig = AppConfig.Load();
var vm = new MainWindowViewModel(appConfig);
var window = new MainWindow { DataContext = vm };
vm.SetWindow(window);
window.Width = 1400;
window.Height = 900;
window.Show();
Pump(20);

string docLabel = "(none)";
string railLabel = "not attempted";
if (pdfPath is not null && File.Exists(pdfPath))
{
    var open = vm.OpenDocument(pdfPath);
    PumpUntil(() => open.IsCompleted, 1500);
    if (open.IsFaulted)
        Console.Error.WriteLine($"  warning: open failed: {open.Exception?.GetBaseException().Message}");
    else
    {
        docLabel = Path.GetFileName(pdfPath);
        Pump(20);
    }

    if (wantRail && vm.ActiveTab is { } tab)
    {
        // Rail needs the current page analysed. The background-analysis timer is unreliable under
        // headless, so trigger it the way the screenshot harness does: zoom above the rail threshold,
        // which eagerly submits analysis for the page and engages rail through the tick. (Rail then
        // seats a line and the viewport peer's Name/HelpText switch to the rail-reading channel.)
        var vpc = window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "Viewport");
        double vpW = vpc?.Bounds.Width ?? window.Width;
        double vpH = vpc?.Bounds.Height ?? window.Height;
        vm.StartBackgroundAnalysis();
        tab.State.ApplyZoom((float)(tab.Rail.ZoomThreshold * 1.3), vpW, vpH);
        tab.State.UpdateRenderDpiIfNeeded();
        vm.RequestCameraUpdate();
        bool analysed = PumpUntil(() => tab.AnalysisCache.ContainsKey(tab.State.CurrentPage), 5000);
        Pump(40);   // let the tick engage rail and seat the first navigable line
        if (tab.Rail.Active)
        {
            // PDFium text extraction is async, so the first render-path accessibility notify can cache an
            // empty line-text snapshot (the peer keys its cache on (page,rail,block,line) and won't requery
            // for the same line). Wait until Core's reading-position text is actually ready, THEN advance one
            // line so the peer re-notifies with the now-warm page text — mirroring how it self-corrects on
            // every line advance in the real app.
            PumpUntil(() => vm.GetReadingPosition()?.LineText is { Length: > 0 }, 2500);
            vm.HandleArrowDown();
            Pump(30);
        }
        railLabel = tab.Rail.Active
            ? $"engaged at {tab.Camera.Zoom * 100:F0}% zoom"
            : analysed ? "page analysed but rail not active"
            : "page not analysed (ONNX model missing? run scripts/download-model.sh)";
    }
}

// Make the viewport peer recompute its cached strings from the current state.
foreach (var p in window.GetVisualDescendants().OfType<ViewportPanel>())
    p.NotifyAccessibilityStateChanged();
Pump(2);

// ---------------------------------------------------------------------------
// Report
// ---------------------------------------------------------------------------

var sb = new StringBuilder();
void W(string s = "") => sb.AppendLine(s);

W("RailReader2 — accessibility (automation-peer) audit");
W("Source: Avalonia automation peers (what the AT-SPI/UIA backends project). Headless, no a11y bus.");
W($"Document: {docLabel}    Rail mode: {railLabel}");
W();

// --- Pattern detection: which UIA/AT-SPI control patterns a peer exposes (i.e. is it
//     actionable by name, toggleable, expandable, readable as a value). ---
static string Patterns(AutomationPeer peer)
{
    var p = new List<string>();
    if (peer is IInvokeProvider) p.Add("Invoke");
    if (peer is IToggleProvider) p.Add("Toggle");
    if (peer is IExpandCollapseProvider) p.Add("ExpandCollapse");
    if (peer is ISelectionItemProvider) p.Add("SelectionItem");
    if (peer is IValueProvider) p.Add("Value");
    if (peer is IRangeValueProvider) p.Add("RangeValue");
    if (peer is IScrollProvider) p.Add("Scroll");
    return p.Count == 0 ? "-" : string.Join("+", p);
}

static string Clean(string? s) => string.IsNullOrEmpty(s) ? "" : s;

int unnamedActionable = 0;
int menuLeaves = 0;        // leaf menu commands (no submenu)
int menuInvokable = 0;     // ...of which expose the UIA Invoke pattern

// === 1. Automation peer tree (default browse view) ===
W("== Automation-peer tree (as an AT client sees it before opening any menu) ==");
void WalkTree(AutomationPeer peer, int depth)
{
    if (depth > 40) return;
    string name = Clean(peer.GetName());
    string type = peer.GetAutomationControlType().ToString();
    string id = Clean(peer.GetAutomationId());
    string pat = Patterns(peer);
    var bits = new List<string> { type };
    if (name.Length > 0) bits.Add($"\"{Trunc(name, 70)}\"");
    if (id.Length > 0) bits.Add($"#{id}");
    if (pat != "-") bits.Add($"[{pat}]");
    if (!peer.IsEnabled()) bits.Add("disabled");
    W(new string(' ', depth * 2) + string.Join(" ", bits));

    IReadOnlyList<AutomationPeer> children;
    try { children = peer.GetChildren(); }
    catch { return; }
    foreach (var c in children) WalkTree(c, depth + 1);
}
var rootPeer = ControlAutomationPeer.CreatePeerForElement(window);
WalkTree(rootPeer, 0);
W();

// === 2. Menu command audit (every MenuItem via the logical tree — closed submenus included) ===
W("== Menu command audit (every command, incl. items inside closed submenus) ==");
var menu = window.GetVisualDescendants().OfType<Menu>().FirstOrDefault();
if (menu is null) W("  (no menu bar found)");
else
{
    void WalkMenu(MenuItem mi, int depth)
    {
        var peer = ControlAutomationPeer.CreatePeerForElement(mi);
        string name = Clean(peer.GetName());
        string accel = Clean(peer.GetAcceleratorKey());
        string id = Clean(peer.GetAutomationId());
        string pat = Patterns(peer);
        bool hasSub = mi.Items.OfType<MenuItem>().Any();

        var bits = new List<string>();
        bits.Add(name.Length > 0 ? $"\"{name}\"" : "\"\" (NO NAME)");
        bits.Add($"invoke={(peer is IInvokeProvider ? "yes" : (hasSub ? "submenu" : "NO"))}");
        if (accel.Length > 0) bits.Add($"accel={accel}");
        if (id.Length > 0) bits.Add($"#{id}");
        if (pat != "-" && pat != "Invoke") bits.Add($"[{pat}]");
        W(new string(' ', depth * 2) + "  " + string.Join("  ", bits));

        // Coverage: leaf commands (no submenu) and how many expose the UIA Invoke pattern.
        if (!hasSub)
        {
            menuLeaves++;
            if (peer is IInvokeProvider) menuInvokable++;
            if (name.Length == 0) unnamedActionable++;
        }

        foreach (var child in mi.Items.OfType<MenuItem>()) WalkMenu(child, depth + 1);
    }
    foreach (var top in menu.Items.OfType<MenuItem>())
    {
        W(Clean(ControlAutomationPeer.CreatePeerForElement(top).GetName()));
        foreach (var child in top.Items.OfType<MenuItem>()) WalkMenu(child, 1);
    }
}
W();

// === 3. Deliberately-annotated chrome (AutomationProperties.Name / AutomationId set) ===
W("== Named / identified controls (explicit AutomationProperties.Name or AutomationId) ==");
foreach (var c in window.GetVisualDescendants().OfType<Control>())
{
    string name = Clean(AutomationProperties.GetName(c));
    string id = Clean(AutomationProperties.GetAutomationId(c));
    if (name.Length == 0 && id.Length == 0) continue;
    var bits = new List<string> { c.GetType().Name };
    if (name.Length > 0) bits.Add($"name=\"{Trunc(name, 50)}\"");
    if (id.Length > 0) bits.Add($"id=#{id}");
    W("  " + string.Join("  ", bits));
}
W();

// === 4. Document viewport peer spotlight (the GPU canvas's a11y channel) ===
W("== Document viewport peer (DocumentViewportAutomationPeer) ==");
var vpPanel = window.GetVisualDescendants().OfType<ViewportPanel>().FirstOrDefault();
if (vpPanel is null) W("  (no viewport — open a document)");
else
{
    var vpPeer = ControlAutomationPeer.CreatePeerForElement(vpPanel);
    W($"  ControlType:  {vpPeer.GetAutomationControlType()}");
    W($"  AutomationId: {Clean(vpPeer.GetAutomationId())}");
    W($"  Patterns:     {Patterns(vpPeer)}");
    W($"  Name:         \"{Clean(vpPeer.GetName())}\"        (cached snapshot)");
    W($"  HelpText:     \"{Clean(vpPeer.GetHelpText())}\"  (cached snapshot)");
    if (vm.GetReadingPosition() is { } pos)
    {
        W($"  Reading pos:  role={pos.Role}, line {pos.LineIndex + 1}, text=\"{Clean(pos.LineText)}\"  (LIVE from Core)");
        W("  → The peer's Name becomes this line text and HelpText appends it, set on each line advance. The");
        W("    cached Name/HelpText above can lag the live value here because this harness drives rail faster");
        W("    than PDFium text extraction settles; in the real app the page text is warm before rail-reading,");
        W("    so the announced Name carries the line text. The LIVE line confirms the channel produces it.");
    }
    else
    {
        W("  Name = stable 'Document viewport' while browsing, the current line while rail-reading (run with");
        W("  --rail to exercise it); HelpText/Value = page, zoom, mode, rail line, and page outline.");
    }
}
W();

// === 5. Summary ===
W("== Summary ==");
W($"  Leaf menu commands with NO accessible name:        {unnamedActionable}" +
  (unnamedActionable == 0 ? "  (good — every command is reachable by name)" : "  ← FIX"));
W($"  Leaf menu commands exposing the UIA Invoke pattern: {menuInvokable} of {menuLeaves}");
if (menuInvokable == 0 && menuLeaves > 0)
{
    W("    → Avalonia's MenuItemAutomationPeer does not implement IInvokeProvider, so on Windows/UIA");
    W("      menu commands can't be fired via the Invoke pattern (agents use the keyboard accelerators");
    W("      shown above, or open-then-select). On Linux/AT-SPI the backend may still expose an Action");
    W("      interface for them — CONFIRM with a live pyatspi/dogtail dump (the key open question).");
}
W("  Note: this audits the Avalonia peer tree. For AT-SPI-projection specifics (live-region");
W("  announcements, the menu Action interface), run a pyatspi/dogtail dump against the live app");
W("  on X11 — see the tool README.");

static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

var report = sb.ToString();
if (outPath is not null)
{
    File.WriteAllText(outPath, report);
    Console.Error.WriteLine($"Wrote audit to {outPath}");
}
else
{
    Console.Out.Write(report);
}
return 0;
