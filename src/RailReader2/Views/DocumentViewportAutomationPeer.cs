using System.Globalization;
using System.Text;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
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
/// Two channels, by design:
/// <list type="bullet">
/// <item>The full state string is the accessible <b>description</b> (HelpText) / UIA value — the
/// channel an automation client reads on demand (<c>get_app_state</c>, Orca "describe").</item>
/// <item>The accessible <b>name</b> is the stable label "Document viewport" while browsing, but becomes
/// the <b>current line text</b> while rail-reading. <see cref="NotifyStateChanged"/> raises a
/// name-changed event as the line advances, which is the lever a screen reader most reliably speaks
/// (Avalonia's AT-SPI backend does not support live regions). The stable handle for automation is the
/// <c>AutomationId</c> ("DocumentViewport"), which is unaffected by the changing name.</item>
/// </list>
/// </summary>
internal sealed class DocumentViewportAutomationPeer : ControlAutomationPeer, IValueProvider
{
    private const string BrowseName = "Document viewport";
    private readonly ViewportPanel _owner;

    // Computed on the UI thread (construction + NotifyStateChanged) and cached, so an accessibility
    // query — which may arrive on the D-Bus thread — never triggers PDFium text extraction off the UI
    // thread. String reference reads/writes are atomic.
    private string _cachedName;
    private string _cachedDescription;
    private (int Page, bool Rail, int Block, int Line) _lastSig;

    public DocumentViewportAutomationPeer(ViewportPanel owner) : base(owner)
    {
        _owner = owner;
        _lastSig = Signature();
        var tab = _owner.ViewModel?.ActiveTab;
        // Cheap seed; no PDFium during (possibly off-thread) creation — line text is filled by the
        // first NotifyStateChanged from the render path.
        _cachedName = ComputeName(tab, lineText: null);
        _cachedDescription = Describe(tab, lineText: null);
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
    /// a name-changed event so a connected screen reader speaks the newly-focused line. Called from the
    /// render path on the UI thread; the cheap signature compare makes per-frame calls free, and the
    /// line-text extraction only runs on an actual change.
    /// </summary>
    public void NotifyStateChanged()
    {
        var sig = Signature();
        if (sig == _lastSig) return;
        _lastSig = sig;

        var tab = _owner.ViewModel?.ActiveTab;
        string? lineText = tab is { Rail.Active: true } ? LineText(tab) : null;

        string previousName = _cachedName;
        _cachedName = ComputeName(tab, lineText);
        _cachedDescription = Describe(tab, lineText);

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
    private static string ComputeName(TabViewModel? tab, string? lineText)
    {
        if (tab is { Rail.Active: true } railTab)
            return lineText is { Length: > 0 } ? lineText : $"Rail line {railTab.Rail.CurrentLine + 1}";
        return BrowseName;
    }

    private static string Describe(TabViewModel? tab, string? lineText)
    {
        if (tab is null) return "No document open.";

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"Page {tab.CurrentPage + 1} of {tab.PageCount}, zoom {tab.Camera.Zoom * 100:F0}%");

        var rail = tab.Rail;
        if (rail.Active)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $". Rail reading {BlockType(rail.CurrentNavigableBlock)}, line {rail.CurrentLine + 1} of {rail.CurrentLineCount}");
            if (lineText is { Length: > 0 } text)
                sb.Append(CultureInfo.InvariantCulture, $": “{text}”");
        }
        else
        {
            sb.Append(". Browse mode.");
        }
        return sb.ToString();
    }

    private static string BlockType(LayoutBlock? block) =>
        block is null ? "text" : block.Role switch
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

    /// <summary>Best-effort text of the current rail line, from the document's cached page text.
    /// Returns null if unavailable; never throws into the accessibility layer.</summary>
    private static string? LineText(TabViewModel tab)
    {
        try
        {
            var line = tab.Rail.CurrentLineInfo;
            if (line.Width <= 0 || line.Height <= 0) return null;
            var text = tab.State.GetOrExtractText(tab.CurrentPage)
                ?.ExtractTextInRect(line.X, line.Y, line.X + line.Width, line.Y + line.Height);
            return text?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        }
        catch
        {
            return null;
        }
    }
}
