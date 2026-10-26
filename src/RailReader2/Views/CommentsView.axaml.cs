using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using RailReader.Core.Models;
using RailReader.Core.Services;
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
    /// <summary>Replies in this thread (on its first comment only).</summary>
    public int ReplyCount { get; init; }
    public string ReplyCountDisplay => ReplyCount == 1 ? "1 reply" : $"{ReplyCount} replies";
    public bool HasReplies => ReplyCount > 0;
    public bool CanEdit => MainWindowViewModel.CanEditComment(Annotation);
    public Avalonia.Thickness RowMargin => new(2 + Math.Min(Depth, 1) * 14, 1, 2, 1);

    // A comment you wrote reads back from the PDF as "in the PDF" once saved, so go by the author name too.
    public bool IsReviewerComment => Annotation.Source == AnnotationSource.InPdf && !IsOwnByName;
    private bool IsOwnByName => !string.IsNullOrWhiteSpace(OwnAuthorName)
        && string.Equals(Annotation.Author?.Trim(), OwnAuthorName.Trim(), StringComparison.OrdinalIgnoreCase);
    public string SourceBadge => IsReviewerComment ? "Reviewer" : "You";

    private static readonly IBrush ReviewerBadgeBrush = new SolidColorBrush(Color.Parse("#2D5B88"));
    private static readonly IBrush OwnBadgeBrush = new SolidColorBrush(Color.Parse("#555555"));

    public IBrush BadgeBackground => IsReviewerComment ? ReviewerBadgeBrush : OwnBadgeBrush;

    public string Author => string.IsNullOrWhiteSpace(Annotation.Author)
        ? (Annotation.Source == AnnotationSource.InPdf ? "Reviewer"
            : string.IsNullOrWhiteSpace(OwnAuthorName) ? "You" : OwnAuthorName!)
        : Annotation.Author!;

    /// <summary>The reader's name for this annotation's colour, when it has one.</summary>
    public string? Category { get; init; }
    public bool HasCategory => !string.IsNullOrEmpty(Category);

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
        _vm.ColourNamesChanged += OnAnnotationsMutated;
        _watchedTab = _vm.ActiveTab;
        RefreshIfVisible();
    }

    private void Detach()
    {
        if (_vm is null) return;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.AnnotationsMutated -= OnAnnotationsMutated;
        _vm.ColourNamesChanged -= OnAnnotationsMutated;
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

        var entries = new List<CommentEntryViewModel>();
        string? me = _vm!.AnnotationAuthorName;
        // Annotation -> page, built once (and only if a reply needs it) instead of a per-reply scan of the file.
        Dictionary<Annotation, int>? pageOf = null;
        foreach (var thread in _vm.ActiveTab!.State.GetThreads())
        {
            entries.Add(new CommentEntryViewModel
            {
                Annotation = thread.Root, PageIndex = thread.Page, Depth = 0,
                Category = _vm.ColourName(thread.Root.Color), ReplyCount = thread.Replies.Count, OwnAuthorName = me,
            });
            foreach (var reply in thread.Replies)
            {
                entries.Add(new CommentEntryViewModel
                {
                    Annotation = reply,
                    PageIndex = PageOfAnnotation(file, reply, ref pageOf) ?? thread.Page,
                    Depth = 1, OwnAuthorName = me, Category = _vm.ColourName(reply.Color),
                });
            }
        }

        RefreshCategoryFilter(entries);
        string? category = CategoryFilter.SelectedIndex > 0 ? CategoryFilter.SelectedItem as string : null;

        // The filters apply to a thread's first comment; replies follow it, so a reviewer's thread
        // still shows your answers.
        if (filter != 0 || category is not null)
        {
            var kept = new List<CommentEntryViewModel>();
            bool keep = false;
            foreach (var e in entries)
            {
                if (!e.IsReply)
                    keep = (filter == 0 || e.IsReviewerComment == (filter == 1))
                        && (category is null || e.Category == category);
                if (keep) kept.Add(e);
            }
            entries = kept;
        }

        CommentEntryList.ItemsSource = entries;
        CommentEmptyLabel.IsVisible = entries.Count == 0;
    }

    private bool _updatingCategories;

    /// <summary>Lists the colour names in use in this document, keeping the current choice when it
    /// is still among them. Hidden when no colour has a name.</summary>
    private void RefreshCategoryFilter(List<CommentEntryViewModel> entries)
    {
        var names = entries.Where(e => !e.IsReply && e.HasCategory).Select(e => e.Category!)
            .Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        string? current = CategoryFilter.SelectedIndex > 0 ? CategoryFilter.SelectedItem as string : null;
        _updatingCategories = true;
        try
        {
            CategoryFilter.ItemsSource = new[] { "All categories" }.Concat(names).ToList();
            CategoryFilter.SelectedIndex = current is not null && names.Contains(current) ? names.IndexOf(current) + 1 : 0;
            CategoryFilter.IsVisible = names.Count > 0;
        }
        finally { _updatingCategories = false; }
    }

    private void OnCategoryFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is not null && !_updatingCategories) RefreshComments();
    }

    private static int? PageOfAnnotation(AnnotationFile file, Annotation ann, ref Dictionary<Annotation, int>? map)
    {
        if (map is null)
        {
            map = new Dictionary<Annotation, int>(ReferenceEqualityComparer.Instance);
            foreach (var (page, list) in file.Pages)
                foreach (var a in list) map[a] = page;
        }
        return map.TryGetValue(ann, out int p) ? p : null;
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

    private void OnCommentReplyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CommentEntryViewModel entry } && _vm is not null)
            _vm.FireAndForget(_vm.ReplyToAnnotation(entry.Annotation, _vm.ActiveTab?.State), nameof(_vm.ReplyToAnnotation));
    }

    private void OnCommentEditClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CommentEntryViewModel entry } && _vm is not null)
            _vm.EditCommentFromPane(entry.PageIndex, entry.Annotation);
    }

    private void OnCommentDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CommentEntryViewModel entry } && _vm is not null)
            _vm.DeleteCommentFromPane(entry.PageIndex, entry.Annotation);
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
