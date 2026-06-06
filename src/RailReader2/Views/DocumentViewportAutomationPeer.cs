using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>
/// Surfaces the document viewport's live state to the platform accessibility tree (AT-SPI on Linux,
/// UIA on Windows): current page, zoom, browse-vs-rail mode, and — in rail mode — the active block
/// type, the line position, and the <em>text of the line being read</em>. The page is a GPU-rendered
/// canvas with no intrinsic accessibility, so this peer is what lets a screen reader (or an automation
/// tool driving the app) know what RailReader2 is actually showing and reading.
///
/// The rail state (role + block/line text) comes from <see cref="MainWindowViewModel.GetReadingPosition"/>
/// and the on-demand page outline from <see cref="MainWindowViewModel.GetPageDescription"/> — both
/// computed in Core, so this peer no longer hand-rolls PDFium text extraction.
///
/// Two channels, by design:
/// <list type="bullet">
/// <item>The full state string is the accessible <b>description</b> (HelpText) / UIA value — the
/// channel an automation client reads on demand (<c>get_app_state</c>, Orca "describe"). It now also
/// carries a compact page outline (e.g. "Page contains: 1 heading, 3 paragraphs, 1 figure").</item>
/// <item>The accessible <b>name</b> is the stable label "Document viewport" while browsing, but becomes
/// the <b>current line text</b> while rail-reading. <see cref="NotifyStateChanged"/> raises a
/// name-changed event as the line advances, which is the lever a screen reader most reliably speaks
/// (Avalonia's AT-SPI backend does not support live regions). The stable handle for automation is the
/// <c>AutomationId</c> ("DocumentViewport"), which is unaffected by the changing name.</item>
/// </list>
///
/// Threading: all extraction happens on the UI thread (construction seed + <see cref="NotifyStateChanged"/>)
/// and is cached, so an accessibility query — which may arrive on the D-Bus thread — only ever reads the
/// cached strings and never triggers off-UI-thread work. String reference reads/writes are atomic. The
/// construction seed deliberately skips the Core queries (it may run off-thread); the first
/// NotifyStateChanged from the render path fills in the rail text and page outline.
/// </summary>
internal sealed class DocumentViewportAutomationPeer : ControlAutomationPeer, IValueProvider
{
    private const string BrowseName = "Document viewport";
    private readonly ViewportPanel _owner;

    private string _cachedName;
    private string _cachedDescription;
    private (int Page, bool Rail, int Block, int Line) _lastSig;

    // Page outline is relatively expensive to format and only changes with the page, so cache it.
    private int _outlinePage = -1;
    private string _outlineCache = "";

    public DocumentViewportAutomationPeer(ViewportPanel owner) : base(owner)
    {
        _owner = owner;
        _lastSig = Signature();
        var tab = _owner.ViewModel?.ActiveTab;
        // Cheap seed; no Core queries during (possibly off-thread) creation — rail text and the page
        // outline are filled by the first NotifyStateChanged from the render path.
        _cachedName = ComputeName(tab, position: null);
        _cachedDescription = Describe(tab, position: null, outline: "");
    }

    protected override string GetClassNameCore() => "DocumentViewport";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override string GetNameCore() => _cachedName;
    protected override bool IsContentElementCore() => true;
    protected override string GetHelpTextCore() => _cachedDescription;

    public bool IsReadOnly => true;
    public string? Value => _cachedDescription;
    public void SetValue(string? value) { /* read-only */ }

    /// <summary>
    /// Refresh the cached name/description and, when the page / rail mode / block / line changes, raise
    /// a name-changed event so a connected screen reader speaks the newly-focused line. Called on the UI
    /// thread from the render path and from Core's PageChanged / ReadingPositionChanged callbacks; the
    /// cheap signature compare makes redundant calls free, and the Core queries only run on an actual
    /// change.
    /// </summary>
    public void NotifyStateChanged()
    {
        var sig = Signature();
        if (sig == _lastSig) return;
        _lastSig = sig;

        var vm = _owner.ViewModel;
        var tab = vm?.ActiveTab;
        // Rail position (role + block/line text), computed in Core; null when not rail-reading.
        var position = tab is { Rail.Active: true } ? vm?.GetReadingPosition() : null;
        string outline = PageOutline(vm, tab?.CurrentPage ?? -1);

        string previousName = _cachedName;
        _cachedName = ComputeName(tab, position);
        _cachedDescription = Describe(tab, position, outline);

        // Announce via the focused element's NAME change — the lever AT-SPI/UIA most reliably speak.
        // The full state stays on the description for on-demand reads; we don't separately raise it, to
        // avoid a screen reader double-speaking the line.
        if (_cachedName != previousName)
            RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, previousName, _cachedName);
    }

    /// <summary>Cheap change key: announce on page / mode / block / line transitions, but not on zoom or
    /// camera frames (which would spam during the snap animation).</summary>
    private (int, bool, int, int) Signature()
    {
        if (_owner.ViewModel?.ActiveTab is not { } tab) return (-1, false, -1, -1);
        var rail = tab.Rail;
        return (tab.CurrentPage, rail.Active,
            rail.Active ? rail.CurrentBlock : -1,
            rail.Active ? rail.CurrentLine : -1);
    }

    /// <summary>Stable landmark name while browsing; the current line while rail-reading.</summary>
    private static string ComputeName(TabViewModel? tab, ReadingPosition? position)
    {
        if (tab is { Rail.Active: true } railTab)
            return position?.LineText is { Length: > 0 } line ? line : $"Rail line {railTab.Rail.CurrentLine + 1}";
        return BrowseName;
    }

    private static string Describe(TabViewModel? tab, ReadingPosition? position, string outline)
    {
        if (tab is null) return "No document open.";

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"Page {tab.CurrentPage + 1} of {tab.PageCount}, zoom {tab.Camera.Zoom * 100:F0}%");

        var rail = tab.Rail;
        if (rail.Active)
        {
            int lineNo = (position?.LineIndex ?? rail.CurrentLine) + 1;
            string role = position is { } p ? RoleName(p.Role) : "text";
            sb.Append(CultureInfo.InvariantCulture,
                $". Rail reading {role}, line {lineNo} of {rail.CurrentLineCount}");
            if (position?.LineText is { Length: > 0 } text)
                sb.Append(CultureInfo.InvariantCulture, $": “{text}”");
        }
        else
        {
            sb.Append(". Browse mode.");
        }

        if (outline.Length > 0)
            sb.Append(CultureInfo.InvariantCulture, $". Page contains: {outline}.");

        return sb.ToString();
    }

    /// <summary>Compact page structure ("1 heading, 3 paragraphs, 1 figure") from the Core layout, for
    /// the on-demand read channel. Cached per page; returns "" until the page has been analysed (so it is
    /// retried on the next call). Page furniture (headers/footers/page numbers/decoration) is skipped.</summary>
    private string PageOutline(MainWindowViewModel? vm, int page)
    {
        if (vm is null || page < 0) return "";
        if (page == _outlinePage && _outlineCache.Length > 0) return _outlineCache;

        var desc = vm.GetPageDescription(page);
        if (desc is null || desc.Blocks.Count == 0) return ""; // not analysed yet — don't cache

        // Count by friendly bucket, preserving first-seen (reading) order.
        var order = new List<string>();
        var counts = new Dictionary<string, int>();
        foreach (var block in desc.Blocks)
        {
            var bucket = OutlineBucket(block.Role);
            if (bucket is null) continue;
            if (!counts.TryGetValue(bucket, out int n))
            {
                counts[bucket] = 1;
                order.Add(bucket);
            }
            else
            {
                counts[bucket] = n + 1;
            }
        }
        if (order.Count == 0) return "";

        var parts = new List<string>(order.Count);
        foreach (var bucket in order)
        {
            int n = counts[bucket];
            parts.Add(n == 1 ? $"1 {bucket}" : $"{n} {bucket}s");
        }
        _outlineCache = string.Join(", ", parts);
        _outlinePage = page;
        return _outlineCache;
    }

    /// <summary>Friendly, pluralisable noun for the page-outline counts; null for page furniture that the
    /// outline omits.</summary>
    private static string? OutlineBucket(BlockRole role) => role switch
    {
        BlockRole.Title => "title",
        BlockRole.Heading => "heading",
        BlockRole.Text => "paragraph",
        BlockRole.Caption => "caption",
        BlockRole.Aside => "aside",
        BlockRole.DisplayMath => "equation",
        BlockRole.Algorithm => "algorithm",
        BlockRole.Table => "table",
        BlockRole.Figure => "figure",
        BlockRole.Chart => "chart",
        BlockRole.Footnote => "footnote",
        BlockRole.Reference => "reference",
        _ => null, // InlineMath, Header, Footer, PageNumber, Decoration, Unknown
    };

    /// <summary>Friendly role label for the rail announcement.</summary>
    private static string RoleName(BlockRole role) => role switch
    {
        BlockRole.Title => "document title",
        BlockRole.Heading => "section heading",
        BlockRole.Caption => "caption",
        BlockRole.Table => "table",
        BlockRole.Figure => "figure",
        BlockRole.Chart => "chart",
        BlockRole.DisplayMath => "equation",
        BlockRole.InlineMath => "inline equation",
        BlockRole.Algorithm => "algorithm",
        BlockRole.Aside => "aside",
        BlockRole.Footnote => "footnote",
        BlockRole.Header => "header",
        BlockRole.Footer => "footer",
        BlockRole.PageNumber => "page number",
        BlockRole.Reference => "reference",
        BlockRole.Decoration => "decoration",
        BlockRole.Text => "text",
        _ => "content",
    };
}
