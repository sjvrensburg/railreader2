using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace RailReader2.Controls;

/// <summary>A TextBlock that shows a library snippet with its matched words in bold. The snippet marks
/// a match with <see cref="Open"/> … <see cref="Close"/> (the markers the Library window asks the index
/// for), which never occur in normalised page text.</summary>
public class HighlightedText : TextBlock
{
    public const char Open = '\u0001';
    public const char Close = '\u0002';

    public static readonly StyledProperty<string?> SnippetProperty =
        AvaloniaProperty.Register<HighlightedText, string?>(nameof(Snippet));

    public string? Snippet
    {
        get => GetValue(SnippetProperty);
        set => SetValue(SnippetProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SnippetProperty) Rebuild(change.GetNewValue<string?>());
    }

    void Rebuild(string? snippet)
    {
        var inlines = new InlineCollection();
        if (!string.IsNullOrEmpty(snippet))
        {
            bool bold = false;
            int start = 0;
            for (int i = 0; i <= snippet.Length; i++)
            {
                if (i < snippet.Length && snippet[i] is not (Open or Close)) continue;
                if (i > start)
                    inlines.Add(new Run(snippet[start..i]) { FontWeight = bold ? FontWeight.Bold : FontWeight.Normal });
                if (i < snippet.Length) bold = snippet[i] == Open;
                start = i + 1;
            }
        }
        Inlines = inlines;
    }

    /// <summary>The snippet without markers (automation names, tooltips).</summary>
    public static string Plain(string? snippet)
        => snippet is null ? "" : snippet.Replace(Open.ToString(), "").Replace(Close.ToString(), "");
}
