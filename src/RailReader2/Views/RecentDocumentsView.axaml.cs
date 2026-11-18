using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using RailReader.Core;
using RailReader2.Library;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>One recent-document card. The thumbnail arrives after the card is shown.</summary>
public sealed partial class RecentCard(RecentDocument doc, bool hiddenFromAi = false, bool hiddenByFolder = false) : ObservableObject
{
    public RecentDocument Doc { get; } = doc;

    /// <summary>Hidden from AI tools (<see cref="AiToolsPreferences"/>), by itself or by a folder rule.</summary>
    public bool HiddenFromAi { get; } = hiddenFromAi || hiddenByFolder;
    /// <summary>Hidden by a folder rule, which is lifted in Settings, not per document.</summary>
    public bool HiddenByFolder { get; } = hiddenByFolder;
    /// <summary>A document opened before content ids existed has nothing to hide by until it is opened again.</summary>
    public bool HasContentId => Doc.ContentId is not null;
    public string AiMenuHeader => AiHideMenuItem.Header(HiddenByFolder);
    public string AiMenuTip => AiHideMenuItem.Tip(HiddenByFolder);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;
    public double CardOpacity => Doc.Missing ? 0.5 : 1.0;
    public bool HasProgress => Doc.Fraction is not null && !Doc.Missing;
    public double ProgressValue => Doc.Fraction ?? 0;
    public string Placeholder => Doc.Missing ? "File not found" : Doc.Locked ? "Password protected" : "";

    public string Detail
    {
        get
        {
            var parts = new List<string> { Doc.Missing ? "not found" : Doc.Progress };
            if (Doc.NoteCount > 0) parts.Add(Doc.NoteCount == 1 ? "1 note" : $"{Doc.NoteCount} notes");
            if (HiddenFromAi) parts.Add("hidden from AI tools");
            return string.Join(" · ", parts);
        }
    }

    public string AutomationName => $"{Doc.Title}, {Detail}";
}

/// <summary>
/// The welcome screen's dashboard (library design §7.2, M7 Phase 3): a library search box and up to
/// twelve recent documents with first-page thumbnails, reading progress and note counts. Loads only
/// while the welcome screen shows (no document open), off the UI thread; thumbnails are rendered once
/// and cached in <c>ConfigDir/library/thumbs</c>. Clicking a card opens it at the saved position.
/// </summary>
public partial class RecentDocumentsView : UserControl
{
    private MainWindowViewModel? _vm;
    private int _loadVersion;
    private bool _loading, _reloadPending;
    // Decoded thumbnails, reused across reloads (keyed by file and its write time) so cards don't flicker.
    private readonly Dictionary<string, (DateTime Written, Bitmap Bitmap)> _bitmaps = new();

    public RecentDocumentsView() => InitializeComponent();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is not MainWindowViewModel vm || ReferenceEquals(vm, _vm)) return;
        _vm = vm;
        vm.Tabs.CollectionChanged += OnTabsChanged;
        vm.LibraryChanged += OnLibraryChanged;
        vm.RecentFilesChanged += OnLibraryChanged;
        Reload();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.Tabs.CollectionChanged -= OnTabsChanged;
            _vm.LibraryChanged -= OnLibraryChanged;
            _vm.RecentFilesChanged -= OnLibraryChanged;
            _vm = null;
        }
        _loadVersion++;
        base.OnUnloaded(e);
    }

    private bool WelcomeShowing => _vm is { Tabs.Count: 0 };

    // The last document closed: the welcome screen is back, with this session's reading positions.
    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (WelcomeShowing) Reload();
        else _loadVersion++; // stop any thumbnail work for a screen no one sees
    }

    // Backfill indexed something (page and note counts may have arrived), or an entry was removed.
    private void OnLibraryChanged()
    {
        if (WelcomeShowing) Reload();
    }

    private async void Reload()
    {
        if (_vm is not { } vm || !WelcomeShowing) return;
        if (_loading) { _reloadPending = true; return; }
        _loading = true;
        int version = ++_loadVersion;
        try
        {
            var docs = await vm.LoadRecentDocumentsAsync();
            if (version != _loadVersion) return;
            var ai = AiToolsPreferences.Load();
            var cards = docs.Select(d =>
            {
                bool byId = d.ContentId is { } id && ai.HiddenDocuments.Contains(id);
                bool byFolder = !byId && d.ContentId is { } id2 && ai.IsHidden(id2, d.Path);
                return new RecentCard(d, byId, byFolder) { Thumbnail = CachedBitmap(d) };
            }).ToList();
            Cards.ItemsSource = cards;
            IsVisible = cards.Count > 0;
            await LoadThumbnails(vm, cards, version);
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[Library] dashboard failed", ex);
        }
        finally
        {
            _loading = false;
            if (_reloadPending)
            {
                _reloadPending = false;
                // A burst of index changes (backfill at startup) reloads at most every couple of seconds.
                await Task.Delay(2000);
                Reload();
            }
        }
    }

    private async Task LoadThumbnails(MainWindowViewModel vm, List<RecentCard> cards, int version)
    {
        foreach (var card in cards)
        {
            if (version != _loadVersion) return;
            var path = await vm.EnsureThumbnailAsync(card.Doc);
            if (path is null || version != _loadVersion) continue;
            var written = File.GetLastWriteTimeUtc(path);
            if (_bitmaps.TryGetValue(path, out var cached) && cached.Written == written)
            {
                card.Thumbnail = cached.Bitmap;
                continue;
            }
            var bitmap = await Task.Run(() =>
            {
                try { return new Bitmap(path); }
                catch (Exception) { return null; }
            });
            if (bitmap is null) continue;
            // Swap the card over even if this load is stale: it may be showing the old bitmap (from
            // CachedBitmap), and a card left holding a disposed bitmap would draw it when shown again.
            card.Thumbnail = bitmap;
            if (_bitmaps.TryGetValue(path, out var old) && !ReferenceEquals(old.Bitmap, bitmap)) old.Bitmap.Dispose();
            _bitmaps[path] = (written, bitmap);
        }
    }

    private Bitmap? CachedBitmap(RecentDocument doc)
    {
        if (doc.ContentId is not { } id) return null;
        var path = RecentDocuments.ThumbnailPath(id);
        return _bitmaps.TryGetValue(path, out var cached) && File.Exists(path) && cached.Written == File.GetLastWriteTimeUtc(path)
            ? cached.Bitmap : null;
    }

    private void OnCardClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || (sender as Control)?.DataContext is not RecentCard card) return;
        vm.FireAndForget(vm.OpenRecentDocument(card.Doc), nameof(vm.OpenRecentDocument));
    }

    // The card's context menu (#303). The menu items' DataContext is the card, inherited from the button.
    private void OnRemoveCard(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || (sender as Control)?.DataContext is not RecentCard card) return;
        vm.RemoveRecentFile(card.Doc.Path);
    }

    // Hide from AI Tools (roadmap M8). The preference is re-read at the click, not taken from the card, which may
    // predate a change made in the Library window, Settings or the CLI.
    private void OnHideFromAiCard(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || (sender as Control)?.DataContext is not RecentCard { Doc.ContentId: { } id } card) return;
        AiHideMenuItem.Toggle(id, card.Doc.Title, vm.ShowStatusToast, changed: Reload);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm is null) return;
        _vm.ShowLibrary(LibrarySearchBox.Text);
        e.Handled = true;
    }
}
