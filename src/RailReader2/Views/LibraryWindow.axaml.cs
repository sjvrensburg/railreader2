using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using RailReader.Core;
using RailReader2.Controls;
using RailReader2.Library;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>A document's header row in the results.</summary>
public sealed class LibraryDocRow(LibraryResultGroup group)
{
    public LibraryResultGroup Group { get; } = group;
    public string Title => Group.DisplayName;
    /// <summary>The metadata authors, shown under the title; null hides the line.</summary>
    public string? Authors => Group.Authors;
    public bool HasAuthors => Group.Authors is not null;
    public bool Missing => Group.Missing;
    public double RowOpacity => Missing ? 0.55 : 1.0;
    public string Subtitle =>
        (Missing ? "Not found at " : "") + (Group.LastPath ?? "unknown location")
        + $" · {Count(Group.HitCount, "match", "matches")}"
        + (Group.NoteCount > 0 ? $" · {Count(Group.NoteCount, "note", "notes")}" : "");
    public string LocateName => $"Locate {Group.FileName}";
    public string RemoveName => $"Remove {Group.FileName} from the library";
    public string AutomationName => $"{Group.DisplayName}{(Group.Authors is { } a ? $", by {a}" : "")}, {Count(Group.HitCount, "match", "matches")}" + (Missing ? ", file not found" : "");

    internal static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}

/// <summary>One snippet row under a document.</summary>
public sealed class LibraryHitRow(LibraryResultGroup group, LibraryHit hit, string label)
{
    public LibraryResultGroup Group { get; } = group;
    public LibraryHit Hit { get; } = hit;
    public string Label { get; } = label;
    public FontStyle LabelStyle => Hit.FromOcr ? FontStyle.Italic : FontStyle.Normal;
    public double RowOpacity => Group.Missing ? 0.55 : 1.0;
    public string AutomationName => $"{Group.DisplayName}, {Label}: {HighlightedText.Plain(Hit.Snippet)}";
}

/// <summary>
/// Library search (design §7.1, M7 Phase 2): one query across every document the reader has opened.
/// Non-modal and single-instance (<see cref="MainWindowViewModel.ShowLibrary"/>), so it works with no
/// document open. Queries run on the thread pool against their own read-only connection to the index;
/// the app's writer thread keeps indexing meanwhile (WAL). Enter or a click on a result opens it.
/// </summary>
public partial class LibraryWindow : Window
{
    const int SearchLimit = 300; // per source; grouped to at most three snippets per document
    readonly MainWindowViewModel? _vm;
    CancellationTokenSource? _queryCts;
    LibraryQuery _query = LibraryQuery.Parse("");
    bool _settingCategories;

    public LibraryWindow() => InitializeComponent();

    internal LibraryWindow(MainWindowViewModel vm) : this()
    {
        _vm = vm;
        // Tunnel: the list handles Enter itself before a bubbling handler would see it.
        ResultsList.AddHandler(KeyDownEvent, OnResultsKeyDown, RoutingStrategies.Tunnel);
        ResultsList.ContainerPrepared += (_, e) =>
        {
            string? name = e.Container.DataContext switch
            {
                LibraryDocRow d => d.AutomationName,
                LibraryHitRow h => h.AutomationName,
                _ => null,
            };
            if (name is not null) AutomationProperties.SetName(e.Container, name);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        vm.LibraryChanged += OnLibraryChanged;
        Closed += (_, _) =>
        {
            vm.LibraryChanged -= OnLibraryChanged;
            _queryCts?.Cancel();
        };
        Opened += (_, _) => QueryBox.Focus();
        Refresh(debounce: false);
    }

    /// <summary>Puts <paramref name="query"/> in the box (null leaves it) and focuses it.</summary>
    public void SetQuery(string? query)
    {
        if (query is { Length: > 0 }) QueryBox.Text = query;
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    void OnQueryChanged(object? sender, TextChangedEventArgs e) => Refresh(debounce: true);

    void OnFilterChanged(object? sender, RoutedEventArgs e) => Refresh(debounce: false);

    void OnCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_settingCategories) Refresh(debounce: false);
    }

    // The writer indexed or removed something: re-run the current query (or just refresh the status).
    void OnLibraryChanged() => Refresh(debounce: false, quiet: true);

    void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Enter) || ResultsList.ItemCount == 0) return;
        // Down (or Enter) moves into the results; Enter opens the first one.
        ResultsList.SelectedIndex = Math.Max(0, ResultsList.SelectedIndex);
        if (e.Key == Key.Enter) OpenRow(ResultsList.SelectedItem);
        else (ResultsList.ContainerFromIndex(ResultsList.SelectedIndex) as Control)?.Focus();
        e.Handled = true;
    }

    void OnResultsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OpenRow(ResultsList.SelectedItem);
        e.Handled = true;
    }

    void OnResultsTapped(object? sender, TappedEventArgs e)
    {
        // A click on a row's Locate/Remove button is that button's, not "open".
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (e.Source is Visual s && s.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: { } row })
            OpenRow(row);
    }

    async void OpenRow(object? row)
    {
        if (_vm is null) return;
        try
        {
            switch (row)
            {
                case LibraryHitRow h when !h.Group.Missing:
                    await _vm.OpenLibraryHit(h.Hit, _query);
                    break;
                case LibraryDocRow d when !d.Missing:
                    await _vm.OpenLibraryDocument(d.Group.ContentId, d.Group.LastPath);
                    break;
                case LibraryHitRow or LibraryDocRow:
                    StatusText.Text = "That file is no longer where it was. Use Locate… to find it, or remove it from the library.";
                    break;
            }
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[Library] opening a result failed", ex);
            StatusText.Text = "Couldn't open that result — see the log for details.";
        }
    }

    async void OnLocateClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || (sender as Control)?.DataContext is not LibraryDocRow row) return;
        try
        {
            bool? ok = await _vm.LocateLibraryDocument(row.Group.ContentId, row.Group.FileName, this);
            if (ok == false)
                StatusText.Text = $"That file isn't {row.Group.FileName}: its contents differ, so it was not linked.";
            else if (ok == true)
                StatusText.Text = $"Found {row.Group.FileName}. Updating the library…";
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[Library] locate failed", ex);
            StatusText.Text = "Couldn't check that file — see the log for details.";
        }
    }

    void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || (sender as Control)?.DataContext is not LibraryDocRow row) return;
        _vm.RemoveFromLibrary(row.Group.ContentId);
        StatusText.Text = $"Removed {row.Group.FileName} from the library. Its notes are kept, and it comes back if you open it again.";
    }

    // ------------------------------------------------------------ querying

    async void Refresh(bool debounce, bool quiet = false)
    {
        _queryCts?.Cancel();
        var cts = _queryCts = new CancellationTokenSource();
        var query = LibraryQuery.Parse(QueryBox.Text);
        var sources = SelectedSources();
        // A colour category is a property of notes, so it applies only while notes are searched.
        CategoryFilter.IsEnabled = NoteFilter.IsChecked == true;
        string? category = CategoryFilter.IsEnabled && CategoryFilter.SelectedItem as string is { } c && c != AnyCategory ? c : null;
        bool withNotes = WithNotesFilter.IsChecked == true;
        string? reader = _vm?.LibraryReaderName;
        // The totals, categories and note counts scan whole tables, so they are read once and again only
        // after the index changed (quiet = a writer change), not on every keystroke.
        if (quiet) _metaStale = true; // stays set until a fresh read lands, even if this refresh is overtaken
        var meta = _metaStale ? null : _meta;
        try
        {
            if (debounce) await Task.Delay(200, cts.Token);
            var result = await Task.Run(() => Search(query, sources, category, withNotes, reader, meta), cts.Token);
            if (cts.IsCancellationRequested) return;
            _query = query;
            _meta = result.Meta;
            if (meta is null) _metaStale = false;
            Show(result, quiet);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[Library] search failed", ex);
            StatusText.Text = "The search failed — see the log for details.";
        }
    }

    const string AnyCategory = "Any colour category";

    HashSet<HitSource>? SelectedSources()
    {
        var set = new HashSet<HitSource>();
        if (PageFilter.IsChecked == true) set.Add(HitSource.Page);
        if (NoteFilter.IsChecked == true) set.Add(HitSource.Note);
        if (OutlineFilter.IsChecked == true) set.Add(HitSource.Outline);
        if (NameFilter.IsChecked == true) { set.Add(HitSource.Name); set.Add(HitSource.Details); }
        return set;
    }

    /// <summary>Whole-index facts, cached between keystrokes (<see cref="_meta"/>).</summary>
    sealed record IndexMeta(LibraryStatus Status, IReadOnlyList<string> Categories, IReadOnlyDictionary<string, int> NoteCounts);

    IndexMeta? _meta;
    bool _metaStale = true;

    sealed record SearchResult(IReadOnlyList<object> Rows, int Documents, int Matches, IndexMeta? Meta, bool Unavailable)
    {
        public LibraryStatus? Status => Meta?.Status;
        public IReadOnlyList<string> Categories => Meta?.Categories ?? [];
    }

    static SearchResult Search(LibraryQuery query, HashSet<HitSource>? sources, string? category, bool withNotes,
        string? reader, IndexMeta? meta)
    {
        using var index = LibraryIndex.OpenReadOnly(LibraryIndex.DefaultPath);
        if (index is null) return new SearchResult([], 0, 0, null, Unavailable: true);
        meta ??= new IndexMeta(index.GetStatus(), index.Categories(), index.NoteCounts());
        if (query.IsEmpty || sources is { Count: 0 }) return new SearchResult([], 0, 0, meta, false);

        // A category filter only applies to notes, so it narrows the search to them.
        if (category is not null) sources = [HitSource.Note];
        var hits = index.Search(query, SearchLimit, HighlightedText.Open.ToString(), HighlightedText.Close.ToString(), sources);
        if (category is not null)
            hits = hits.Where(h => string.Equals(h.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();
        var noteCounts = meta.NoteCounts;
        if (withNotes) hits = hits.Where(h => noteCounts.ContainsKey(h.ContentId)).ToList();

        var groups = LibraryResults.Group(hits, noteCounts: noteCounts);
        var rows = new List<object>();
        foreach (var g in groups)
        {
            rows.Add(new LibraryDocRow(g));
            foreach (var h in g.Hits)
                // Whole-document hits (file name, title/author) are what the header row already shows.
                if (h.Source is not (HitSource.Name or HitSource.Details)) rows.Add(new LibraryHitRow(g, h, LibraryResults.Label(h, reader)));
        }
        return new SearchResult(rows, groups.Count, hits.Count, meta, false);
    }

    void Show(SearchResult result, bool quiet)
    {
        UpdateCategories(result.Categories);
        // A background refresh keeps the reader's place unless the results actually changed.
        if (!quiet || !SameRows(result.Rows)) ResultsList.ItemsSource = result.Rows;

        if (result.Unavailable)
        {
            StatusText.Text = _vm?.IsLibraryIndexing == false
                ? "Library search is off."
                : "The library is empty so far. Documents are added as you open them.";
            return;
        }
        var s = result.Status!;
        string progress = _vm?.LibraryBackfillProgress is (var done, > 0 and var total)
            ? $" · Indexing {done} of {total} earlier documents…" : "";
        string summary = _query.IsEmpty
            ? $"{LibraryDocRow.Count(s.Documents, "document", "documents")} in the library"
            : result.Documents == 0
                ? "No matches"
                : $"{LibraryDocRow.Count(result.Matches, "match", "matches")} in {LibraryDocRow.Count(result.Documents, "document", "documents")}"
                  + (result.Matches >= SearchLimit ? " (showing the best)" : "");
        StatusText.Text = summary + progress + (_vm?.IsLibraryIndexing == false ? " · Indexing is off this session." : "");
    }

    bool SameRows(IReadOnlyList<object> rows)
    {
        if (ResultsList.ItemsSource is not IReadOnlyList<object> old || old.Count != rows.Count) return false;
        for (int i = 0; i < rows.Count; i++)
        {
            switch ((old[i], rows[i]))
            {
                case (LibraryDocRow a, LibraryDocRow b) when a.Group.ContentId == b.Group.ContentId && a.Missing == b.Missing
                                                             && a.Group.HitCount == b.Group.HitCount:
                case (LibraryHitRow c, LibraryHitRow d) when c.Hit == d.Hit:
                    continue;
                default:
                    return false;
            }
        }
        return true;
    }

    void UpdateCategories(IReadOnlyList<string> categories)
    {
        var current = CategoryFilter.SelectedItem as string;
        var items = new List<string> { AnyCategory };
        items.AddRange(categories);
        if (CategoryFilter.ItemsSource is IReadOnlyList<string> old && old.SequenceEqual(items)) return;
        _settingCategories = true;
        try
        {
            CategoryFilter.ItemsSource = items;
            CategoryFilter.SelectedItem = current is not null && items.Contains(current) ? current : AnyCategory;
            CategoryFilter.IsVisible = categories.Count > 0;
        }
        finally { _settingCategories = false; }
    }
}
