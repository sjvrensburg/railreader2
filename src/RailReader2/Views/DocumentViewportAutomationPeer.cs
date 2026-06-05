using System.Globalization;
using System.Text;
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
/// </summary>
internal sealed class DocumentViewportAutomationPeer : ControlAutomationPeer, IValueProvider
{
    private readonly ViewportPanel _owner;

    public DocumentViewportAutomationPeer(ViewportPanel owner) : base(owner) => _owner = owner;

    protected override string GetClassNameCore() => "DocumentViewport";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override string GetNameCore() => "Document viewport";
    protected override bool IsContentElementCore() => true;
    protected override string GetHelpTextCore() => Describe();

    // IValueProvider: the live status string, also exposed via the AT-SPI value interface so a client
    // that reads "value" rather than "description" still sees the state.
    public bool IsReadOnly => true;
    public string? Value => Describe();
    public void SetValue(string? value) { /* read-only */ }

    private string Describe()
    {
        if (_owner.ViewModel?.ActiveTab is not { } tab)
            return "No document open.";

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"Page {tab.CurrentPage + 1} of {tab.PageCount}, zoom {tab.Camera.Zoom * 100:F0}%");

        var rail = tab.Rail;
        if (rail.Active)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $". Rail reading {BlockType(rail.CurrentNavigableBlock)}, line {rail.CurrentLine + 1} of {rail.CurrentLineCount}");
            if (LineText(tab) is { Length: > 0 } text)
                sb.Append(CultureInfo.InvariantCulture, $": “{text}”");
        }
        else
        {
            sb.Append(". Browse mode.");
        }
        return sb.ToString();
    }

    private static string BlockType(LayoutBlock? block) =>
        block is null ? "text" : block.Role.ToString().ToLowerInvariant();

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
