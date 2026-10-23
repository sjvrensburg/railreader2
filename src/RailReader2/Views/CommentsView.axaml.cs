using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using RailReader.Core.Models;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>One row in the Comments pane: an annotation plus its display metadata.</summary>
public sealed class CommentEntryViewModel
{
    public required Annotation Annotation { get; init; }
    public required int PageIndex { get; init; }

    /// <summary>Nesting level in a reply thread: 0 for a thread's first comment.</summary>
    public int Depth { get; init; }
    /// <summary>The reader's configured name, shown for their own comments that carry no author.</summary>
    public string? OwnAuthorName { get; init; }
    public bool IsReply => Depth > 0;
    public Avalonia.Thickness RowMargin => new(2 + Math.Min(Depth, 4) * 14, 1, 2, 1);

    public bool IsReviewerComment => Annotation.Source == AnnotationSource.InPdf;
    public string SourceBadge => IsReviewerComment ? "Reviewer" : "You";

    private static readonly IBrush ReviewerBadgeBrush = new SolidColorBrush(Color.Parse("#2D5B88"));
    private static readonly IBrush OwnBadgeBrush = new SolidColorBrush(Color.Parse("#555555"));

    public IBrush BadgeBackground => IsReviewerComment ? ReviewerBadgeBrush : OwnBadgeBrush;

    public string Author => string.IsNullOrWhiteSpace(Annotation.Author)
        ? (IsReviewerComment ? "Reviewer" : string.IsNullOrWhiteSpace(OwnAuthorName) ? "You" : OwnAuthorName!)
        : Annotation.Author!;

    public string Body => Annotation.EffectiveContents;
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);

    public string DateDisplay =>
        (Annotation.ModifiedUtc ?? Annotation.CreatedUtc)?.LocalDateTime.ToString("g") ?? "";

    public string PageDisplay => $"Page {PageIndex + 1}";

    public ReviewState State => Annotation.State;
    public ReviewState[] StateOptions { get; } =
        [ReviewState.None, ReviewState.Accepted, ReviewState.Rejected, ReviewState.Cancelled, ReviewState.Completed];

    public string TypeLabel => (IsReply ? "↳ " : "") + BaseTypeLabel;

    private string BaseTypeLabel => Annotation switch
    {
        HighlightAnnotation => "Highlight",
        UnderlineAnnotation => "Underline",
        StrikeOutAnnotation => "Strike-out",
        SquigglyAnnotation => "Squiggly",
        TextNoteAnnotation => "Note",
        RectAnnotation => "Box",
        FreehandAnnotation => "Drawing",
        CaretAnnotation => "Caret",
        FreeTextAnnotation => "Text box",
        _ => "Annotation",
    };
}

/// <summary>
/// Comments pane — lists every annotation in the active document with its author, comment
/// text, dates, Source badge (reviewer vs your own), and an editable review state. Refreshes
/// from the railreader2-side <see cref="MainWindowViewModel.AnnotationsMutated"/> signal (Core
/// has no annotation-changed event) and on document switch. Self-contained: wires its
/// subscriptions on load and tears them down on unload.
/// </summary>
public partial class CommentsView : PaneRefreshView
{
    private MainWindowViewModel? _vm;
    private TabViewModel? _watchedTab;
    private bool _suppressCommentsRefresh;

    public CommentsView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Attach(DataContext as MainWindowViewModel);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        Detach();
        base.OnUnloaded(e);
    }

    private void Attach(MainWindowViewModel? vm)
    {
        Detach();
        _vm = vm;
        if (_vm is null) return;

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.AnnotationsMutated += OnAnnotationsMutated;
        _watchedTab = _vm.ActiveTab;
        RefreshIfVisible();
    }

    private void Detach()
    {
        if (_vm is null) return;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.AnnotationsMutated -= OnAnnotationsMutated;
        _vm = null;
        _watchedTab = null;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        // ActiveTab is raised synthetically on every navigation; the comment list depends only on
        // the document and AnnotationsMutated, so rebuild only on a real tab switch.
        if (args.PropertyName == nameof(MainWindowViewModel.ActiveTab) && _vm?.ActiveTab != _watchedTab)
        {
            _watchedTab = _vm?.ActiveTab;
            RefreshIfVisible();
        }
    }

    private void RefreshComments()
    {
        var file = _vm?.ActiveTab?.Annotations;
        if (file is null)
        {
            CommentEntryList.ItemsSource = null;
            CommentEmptyLabel.IsVisible = false;
            return;
        }

        int filter = CommentFilter.SelectedIndex; // 0 All, 1 Reviewer, 2 Yours

        var all = new List<(Annotation Ann, int Page)>();
        foreach (var (page, list) in file.Pages.OrderBy(kv => kv.Key))
            foreach (var ann in list) all.Add((ann, page));

        var entries = new List<CommentEntryViewModel>();
        foreach (var (ann, page, depth) in OrderAsThreads(all))
        {
            entries.Add(new CommentEntryViewModel
            {
                Annotation = ann, PageIndex = page, Depth = depth,
                OwnAuthorName = _vm!.AnnotationAuthorName,
            });
        }

        // The filter applies to a thread's first comment; replies follow it, so a reviewer's thread
        // still shows your answers.
        if (filter != 0)
        {
            var kept = new List<CommentEntryViewModel>();
            bool keep = false;
            foreach (var e in entries)
            {
                if (!e.IsReply) keep = e.IsReviewerComment == (filter == 1);
                if (keep) kept.Add(e);
            }
            entries = kept;
        }

        CommentEntryList.ItemsSource = entries;
        CommentEmptyLabel.IsVisible = entries.Count == 0;
    }

    /// <summary>Annotations in document order with each reply placed directly under its parent
    /// (<see cref="Annotation.InReplyTo"/> = the parent's <see cref="Annotation.NativeId"/>). A reply whose
    /// parent is absent, and any cycle, degrades to an ordinary top-level entry.</summary>
    internal static List<(Annotation Ann, int Page, int Depth)> OrderAsThreads(
        IReadOnlyList<(Annotation Ann, int Page)> all)
    {
        var byId = new Dictionary<string, (Annotation Ann, int Page)>(StringComparer.Ordinal);
        foreach (var item in all)
            if (!string.IsNullOrEmpty(item.Ann.NativeId)) byId.TryAdd(item.Ann.NativeId!, item);

        var children = new Dictionary<string, List<(Annotation Ann, int Page)>>(StringComparer.Ordinal);
        var roots = new List<(Annotation Ann, int Page)>();
        foreach (var item in all)
        {
            string? parent = item.Ann.InReplyTo;
            if (!string.IsNullOrEmpty(parent) && byId.TryGetValue(parent!, out var p)
                && !ReferenceEquals(p.Ann, item.Ann))
            {
                if (!children.TryGetValue(parent!, out var list)) children[parent!] = list = [];
                list.Add(item);
            }
            else roots.Add(item);
        }

        var result = new List<(Annotation, int, int)>(all.Count);
        var seen = new HashSet<Annotation>(ReferenceEqualityComparer.Instance);
        void Walk((Annotation Ann, int Page) node, int depth)
        {
            if (!seen.Add(node.Ann)) return;
            result.Add((node.Ann, node.Page, depth));
            if (string.IsNullOrEmpty(node.Ann.NativeId) || !children.TryGetValue(node.Ann.NativeId!, out var kids)) return;
            foreach (var kid in kids.OrderBy(k => k.Ann.CreatedUtc ?? DateTimeOffset.MinValue))
                Walk(kid, depth + 1);
        }
        foreach (var r in roots) Walk(r, 0);
        // Members of a pure cycle are reachable from no root: list them flat rather than dropping them.
        foreach (var item in all) if (!seen.Contains(item.Ann)) Walk(item, 0);
        return result;
    }

    private void OnAnnotationsMutated()
    {
        // A review-state edit from this list raises AnnotationsMutated synchronously; the
        // edited row's ComboBox already shows the new value, so skip the full rebuild to
        // avoid tearing down the visual tree (flicker / lost focus) mid-SelectionChanged.
        if (_suppressCommentsRefresh) return;
        RefreshIfVisible();
    }

    protected override void Refresh() => RefreshComments();

    private void OnCommentFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Guard _vm first: this fires during InitializeComponent (the ComboBox's initial
        // SelectedIndex) before _vm is attached.
        if (_vm is not null) RefreshComments();
    }

    private void OnCommentEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CommentEntryViewModel entry } && _vm is not null)
        {
            _vm.NavigateToAnnotation(entry.PageIndex, entry.Annotation);
            _vm.RequestViewportFocus();
        }
    }

    private void OnCommentStateChanged(object? sender, SelectionChangedEventArgs e)
    {
        // SetReviewState is idempotent, so the initial binding's SelectionChanged (newState
        // == current) is a harmless no-op — no suppression flag needed.
        if (sender is ComboBox { DataContext: CommentEntryViewModel entry, SelectedItem: ReviewState newState }
            && _vm is not null)
        {
            _suppressCommentsRefresh = true;
            try { _vm.SetReviewState(entry.Annotation, newState); }
            finally { _suppressCommentsRefresh = false; }
        }
    }
}
