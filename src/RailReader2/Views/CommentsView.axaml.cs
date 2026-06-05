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

    public bool IsReviewerComment => Annotation.Source == AnnotationSource.InPdf;
    public string SourceBadge => IsReviewerComment ? "Reviewer" : "You";

    public IBrush BadgeBackground => IsReviewerComment
        ? new SolidColorBrush(Color.Parse("#2D5B88"))   // reviewer (blue)
        : new SolidColorBrush(Color.Parse("#555555"));   // your own (grey)

    public string Author => string.IsNullOrWhiteSpace(Annotation.Author)
        ? (IsReviewerComment ? "Reviewer" : "You")
        : Annotation.Author!;

    public string Body => Annotation.EffectiveContents;
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);

    public string DateDisplay =>
        (Annotation.ModifiedUtc ?? Annotation.CreatedUtc)?.LocalDateTime.ToString("g") ?? "";

    public string PageDisplay => $"Page {PageIndex + 1}";

    public ReviewState State => Annotation.State;
    public ReviewState[] StateOptions { get; } =
        [ReviewState.None, ReviewState.Accepted, ReviewState.Rejected, ReviewState.Cancelled, ReviewState.Completed];

    public string TypeLabel => Annotation switch
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
        RefreshIfVisible();
    }

    private void Detach()
    {
        if (_vm is null) return;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.AnnotationsMutated -= OnAnnotationsMutated;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.ActiveTab))
            RefreshIfVisible();
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
        foreach (var (page, list) in file.Pages.OrderBy(kv => kv.Key))
        {
            foreach (var ann in list)
            {
                bool reviewer = ann.Source == AnnotationSource.InPdf;
                if (filter == 1 && !reviewer) continue;
                if (filter == 2 && reviewer) continue;
                entries.Add(new CommentEntryViewModel { Annotation = ann, PageIndex = page });
            }
        }

        CommentEntryList.ItemsSource = entries;
        CommentEmptyLabel.IsVisible = entries.Count == 0;
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
