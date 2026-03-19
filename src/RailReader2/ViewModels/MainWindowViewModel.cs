using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader2.Views;

namespace RailReader2.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly DocumentController _controller;
    private Window? _window;
    private DispatcherTimer? _pollTimer;
    private readonly Stopwatch _frameTimer = Stopwatch.StartNew();
    private InvalidationCallbacks? _invalidation;
    private bool _animationRequested;

    [ObservableProperty] private int _activeTabIndex;
    [ObservableProperty] private bool _showOutline;

    /// <summary>
    /// Callback set by the code-behind to read the current sidebar column width
    /// from the grid (which may have been resized via GridSplitter).
    /// </summary>
    public Func<double>? ReadSidePanelWidth { get; set; }

    partial void OnShowOutlineChanged(bool value)
    {
        // Keep the active tab's sidebar state in sync
        if (ActiveTab is { } tab)
            tab.ShowSidePanel = value;
    }

    /// <summary>Save sidebar visibility and width to the given tab.</summary>
    private void SaveSidebarState(TabViewModel tab)
    {
        tab.ShowSidePanel = ShowOutline;
        if (ShowOutline && ReadSidePanelWidth is { } getWidth)
            tab.SidePanelWidth = getWidth();
    }

    /// <summary>Restore sidebar visibility and width from the given tab.</summary>
    private void RestoreSidebarState(TabViewModel tab)
    {
        ShowOutline = tab.ShowSidePanel;
        // Width is applied by UpdateSidebarColumnWidth via the ShowOutline PropertyChanged handler
    }

    [ObservableProperty] private bool _showMinimap;
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private bool _showAbout;
    [ObservableProperty] private bool _showShortcuts;
    [ObservableProperty] private bool _showGoToPage;
    [ObservableProperty] private string? _cleanupMessage;

    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private bool _showFullScreenHeader;
    [ObservableProperty] private bool _isRadialMenuOpen;
    [ObservableProperty] private bool _showBookmarkDialog;
    [ObservableProperty] private double _radialMenuX;
    [ObservableProperty] private double _radialMenuY;

    /// <summary>True when the tab bar should be visible (not fullscreen, or hovering at top edge).</summary>
    public bool IsTabBarVisible => !IsFullScreen || ShowFullScreenHeader;

    partial void OnIsFullScreenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTabBarVisible));
        if (!value) ShowFullScreenHeader = false;
    }

    partial void OnShowFullScreenHeaderChanged(bool value) => OnPropertyChanged(nameof(IsTabBarVisible));

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    // Delegated state from controller
    public List<SearchMatch> SearchMatches => _controller.SearchMatches;
    public List<SearchMatch>? CurrentPageSearchMatches => _controller.CurrentPageSearchMatches;
    public int ActiveMatchIndex
    {
        get => _controller.ActiveMatchIndex;
        set => _controller.ActiveMatchIndex = value;
    }

    public AnnotationTool ActiveTool => _controller.ActiveTool;
    public bool IsAnnotating => _controller.IsAnnotating;
    public Annotation? SelectedAnnotation
    {
        get => _controller.SelectedAnnotation;
        set => _controller.SelectedAnnotation = value;
    }
    public Annotation? PreviewAnnotation => _controller.PreviewAnnotation;
    public string ActiveAnnotationColor
    {
        get => _controller.ActiveAnnotationColor;
        set => _controller.ActiveAnnotationColor = value;
    }
    public float ActiveAnnotationOpacity
    {
        get => _controller.ActiveAnnotationOpacity;
        set => _controller.ActiveAnnotationOpacity = value;
    }
    public float ActiveStrokeWidth
    {
        get => _controller.ActiveStrokeWidth;
        set => _controller.ActiveStrokeWidth = value;
    }

    public string? SelectedText => _controller.SelectedText;
    public List<HighlightRect>? TextSelectionRects => _controller.TextSelectionRects;

    public Action<string>? CopyToClipboard
    {
        get => _controller.CopyToClipboard;
        set => _controller.CopyToClipboard = value;
    }

    public bool AutoScrollActive => _controller.AutoScrollActive;

    [ObservableProperty] private bool _jumpMode;
    partial void OnJumpModeChanged(bool value) => _controller.JumpMode = value;

    public AppConfig Config => _controller.Config;
    public ColourEffectShaders ColourEffects => _controller.ColourEffects;
    public DocumentController Controller => _controller;

    public TabViewModel? ActiveTab =>
        ActiveTabIndex >= 0 && ActiveTabIndex < Tabs.Count ? Tabs[ActiveTabIndex] : null;

    public MainWindowViewModel(AppConfig config)
    {
        _controller = new DocumentController(config, new AvaloniaThreadMarshaller());
        try { _controller.InitializeWorker(); }
        catch (FileNotFoundException) { /* ONNX model not found — layout analysis disabled */ }
        _controller.StateChanged += OnControllerStateChanged;
        _controller.StatusMessage += ShowStatusToast;
        SetupPollTimer();
    }

    private void OnControllerStateChanged(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(AutoScrollActive):
                OnPropertyChanged(nameof(AutoScrollActive));
                break;
        }
    }

    public void SetWindow(Window window)
    {
        _window = window;
        ApplyFontScale();
    }
    public void SetInvalidation(InvalidationCallbacks callbacks) => _invalidation = callbacks;

    private void SetupPollTimer()
    {
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _pollTimer.Tick += (_, _) =>
        {
            if (_animationRequested) return;

            var (gotResults, needsAnim) = _controller.PollAnalysisResults();
            var tab = ActiveTab;
            if (tab is not null && !_animationRequested)
                tab.SubmitPendingLookahead(_controller.Worker);
            if (gotResults)
                InvalidateOverlay();
            if (needsAnim)
                RequestAnimationFrame();
            bool workerBusy = _controller.Worker is not null && !_controller.Worker.IsIdle;
            if (!workerBusy) _pollTimer?.Stop();
        };
    }

    private void OnAnimationFrame(TimeSpan _)
    {
        _animationRequested = false;

        double dt = _frameTimer.Elapsed.TotalSeconds;
        _frameTimer.Restart();

        var result = _controller.Tick(dt);

        if (result.PageChanged) InvalidatePage();
        if (result.OverlayChanged)
        {
            InvalidateOverlay();
            OnPropertyChanged(nameof(ActiveTab));
        }
        if (result.AnnotationsChanged) InvalidateAnnotations();
        if (result.CameraChanged) InvalidateCamera();
        if (result.StillAnimating) RequestAnimationFrame();
    }

    public void RequestAnimationFrame()
    {
        if (_pollTimer is not null && !_pollTimer.IsEnabled)
            _pollTimer.Start();

        if (_animationRequested) return;
        _animationRequested = true;
        _frameTimer.Restart();
        _window?.RequestAnimationFrame(OnAnimationFrame);
    }

    public void InvalidateCanvas()
    {
        InvalidateAll();
        OnPropertyChanged(nameof(ActiveTab));
    }

    // --- Document management ---

    public async void OpenDocument(string path)
    {
        try
        {
            Console.Error.WriteLine($"[OpenDocument] Opening: {path}");

            TabViewModel? tab = null;
            await Task.Run(() =>
            {
                var state = _controller.CreateDocument(path);
                state.LoadPageBitmap();
                tab = new TabViewModel(state);
            });

            if (tab is null) return;

            Console.Error.WriteLine($"[OpenDocument] Loaded: {tab.PageCount} pages, {tab.PageWidth}x{tab.PageHeight}");
            tab.LoadAnnotations();

            // Save sidebar state from outgoing tab before switching
            if (ActiveTab is { } oldTab)
                SaveSidebarState(oldTab);

            _controller.AddDocument(tab.State);
            Tabs.Add(tab);

            // New tab inherits the current sidebar state
            tab.ShowSidePanel = ShowOutline;
            if (ReadSidePanelWidth is { } getWidth)
                tab.SidePanelWidth = getWidth();

            ActiveTabIndex = Tabs.Count - 1;
            OnPropertyChanged(nameof(ActiveTab));
            InvalidateAll();

            Dispatcher.UIThread.Post(() => InvalidatePage(), DispatcherPriority.Background);
            RequestAnimationFrame();

            Console.Error.WriteLine("[OpenDocument] Tab added successfully");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to open {path}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    [RelayCommand]
    public async Task OpenFile()
    {
        if (_window is null) return;
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PDF",
            FileTypeFilter = [new FilePickerFileType("PDF Files") { Patterns = ["*.pdf"] }],
            AllowMultiple = false,
        });
        if (files is { Count: > 0 })
        {
            var path = files[0].TryGetLocalPath()
                       ?? files[0].Path.LocalPath;
            Console.Error.WriteLine($"[OpenFile] Selected: {path}");
            if (path is not null) OpenDocument(path);
        }
    }

    [RelayCommand]
    public void CloseTab(int index)
    {
        if (index < 0 || index >= Tabs.Count) return;
        var tab = Tabs[index];
        _controller.CloseDocument(_controller.Documents.IndexOf(tab.State));
        Tabs.RemoveAt(index);
        if (Tabs.Count == 0)
        {
            ActiveTabIndex = 0;
            ShowOutline = false;
        }
        else
        {
            if (ActiveTabIndex >= Tabs.Count) ActiveTabIndex = Tabs.Count - 1;
            RestoreSidebarState(Tabs[ActiveTabIndex]);
        }
        OnPropertyChanged(nameof(ActiveTab));
        InvalidateAll();
    }

    public void SaveAllReadingPositions() => _controller.SaveAllReadingPositions();

    [RelayCommand]
    public void SelectTab(int index)
    {
        if (index >= 0 && index < Tabs.Count)
        {
            if (ActiveTab is { } oldTab)
                SaveSidebarState(oldTab);

            // Drop out of any annotation mode when switching tabs
            if (IsAnnotating)
                CancelAnnotationTool();

            ActiveTabIndex = index;
            _controller.SelectDocument(index);
            RestoreSidebarState(Tabs[index]);

            OnPropertyChanged(nameof(ActiveTab));
            InvalidateAll();
        }
    }

    public void MoveTab(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= Tabs.Count) return;
        if (toIndex < 0 || toIndex >= Tabs.Count) return;

        var selectedTab = ActiveTab;
        Tabs.Move(fromIndex, toIndex);
        _controller.MoveDocument(fromIndex, toIndex);

        if (selectedTab is not null)
            ActiveTabIndex = Tabs.IndexOf(selectedTab);

        OnPropertyChanged(nameof(ActiveTab));
        InvalidateAll();
    }

    [RelayCommand]
    public void DuplicateTab()
    {
        if (ActiveTab is { } tab)
            OpenDocument(tab.FilePath);
    }

    public void DetachTab(int index)
    {
        if (index < 0 || index >= Tabs.Count) return;
        var tab = Tabs[index];
        var filePath = tab.FilePath;

        // Create a new window with a cloned config so settings are current and independent
        var newVm = new MainWindowViewModel(Config.Clone());
        var newWindow = new MainWindow { DataContext = newVm };
        newVm.SetWindow(newWindow);
        newWindow.Closing += (_, _) => newVm.SaveAllReadingPositions();
        newWindow.Show();
        newVm.OpenDocument(filePath);

        // Close the tab in this window
        CloseTab(index);
    }

    // --- Navigation ---

    public void NavigateToBookmark(int index)
    {
        _controller.NavigateToBookmark(index);
        InvalidateAfterNavigation();
    }

    public void NavigateBack()
    {
        _controller.NavigateBack();
        InvalidateAfterNavigation();
    }

    [RelayCommand]
    public void GoToPage(int page)
    {
        _controller.GoToPage(page);
        InvalidateAfterNavigation();
    }

    [RelayCommand]
    public void FitPage()
    {
        _controller.FitPage();
        InvalidateCameraAndTab();
    }

    [RelayCommand]
    public void FitWidth()
    {
        _controller.FitWidth();
        InvalidateCameraAndTab();
    }

    // --- Camera ---

    public void HandleZoom(double scrollDelta, double cursorX, double cursorY, bool ctrlHeld)
    {
        _controller.HandleZoom(scrollDelta, cursorX, cursorY, ctrlHeld);
        InvalidateCameraAndTab();
        RequestAnimationFrame();
    }

    public void HandlePan(double dx, double dy)
    {
        _controller.HandlePan(dx, dy);
        InvalidateCameraAndTab();
    }

    public void HandleZoomKey(bool zoomIn)
    {
        _controller.HandleZoomKey(zoomIn);
        InvalidateCameraAndTab();
        RequestAnimationFrame();
    }

    public void HandleResetZoom() => FitPage();

    // --- Rail navigation ---

    public void HandleArrowDown()
    {
        _controller.HandleArrowDown();
        InvalidateNavigation();
    }

    public void HandleArrowUp()
    {
        _controller.HandleArrowUp();
        InvalidateNavigation();
    }

    public void HandleArrowRight(bool shortJump = false)
    {
        _controller.HandleArrowRight(shortJump);
        InvalidateCameraAndTab();
        RequestAnimationFrame();
    }

    public void HandleArrowLeft(bool shortJump = false)
    {
        _controller.HandleArrowLeft(shortJump);
        InvalidateCameraAndTab();
        RequestAnimationFrame();
    }

    public void HandleLineHome()
    {
        _controller.HandleLineHome();
        InvalidateCameraAndTab();
    }

    public void HandleLineEnd()
    {
        _controller.HandleLineEnd();
        InvalidateCameraAndTab();
    }

    public void HandleArrowRelease(bool isHorizontal)
    {
        _controller.HandleArrowRelease(isHorizontal);
        RequestAnimationFrame();
    }

    public void HandleClick(double canvasX, double canvasY)
    {
        if (_controller.HandleClick(canvasX, canvasY))
            InvalidateNavigation();
    }

    // --- Auto-scroll ---

    public void ToggleAutoScroll()
    {
        _controller.ToggleAutoScroll();
        OnPropertyChanged(nameof(AutoScrollActive));
        RequestAnimationFrame();
    }

    public void StopAutoScroll()
    {
        _controller.StopAutoScroll();
        OnPropertyChanged(nameof(AutoScrollActive));
    }

    public void ToggleAutoScrollExclusive()
    {
        _controller.ToggleAutoScrollExclusive();
        JumpMode = _controller.JumpMode;
        OnPropertyChanged(nameof(AutoScrollActive));
        RequestAnimationFrame();
    }

    public void ToggleJumpModeExclusive()
    {
        _controller.ToggleJumpModeExclusive();
        JumpMode = _controller.JumpMode;
        OnPropertyChanged(nameof(AutoScrollActive));
    }

    public void ToggleLineFocusBlur()
    {
        if (_controller.ActiveDocument is { } doc)
        {
            doc.LineFocusBlur = !doc.LineFocusBlur;
            InvalidatePage();
            InvalidateOverlay();
        }
    }

    public void ToggleBionicReading()
    {
        if (_controller.ActiveDocument is { } doc)
        {
            doc.BionicReading = !doc.BionicReading;
            ShowStatusToast(doc.BionicReading ? "Bionic reading ON" : "Bionic reading OFF");
            InvalidatePage();
        }
    }

    // --- Colour effects ---

    [RelayCommand]
    public void SetColourEffect(ColourEffect effect)
    {
        _controller.SetColourEffect(effect);
        InvalidatePage();
        InvalidateOverlay();
    }

    public ColourEffect CycleColourEffect()
    {
        var effect = _controller.CycleColourEffect();
        InvalidatePage();
        InvalidateOverlay();
        return effect;
    }

    // --- Status toast ---

    [ObservableProperty] private string? _statusToast;
    private Timer? _toastTimer;

    public void ShowStatusToast(string message)
    {
        StatusToast = message;
        _toastTimer?.Dispose();
        _toastTimer = new Timer(_ =>
            Dispatcher.UIThread.Post(() => StatusToast = null),
            null, 1500, Timeout.Infinite);
    }

    // --- Config ---

    [RelayCommand]
    public void RunCleanup()
    {
        var (removed, freed) = CleanupService.RunCleanup();
        CleanupMessage = CleanupService.FormatReport(removed, freed);
    }

    public void SetDarkMode(bool dark)
    {
        Config.DarkMode = dark;
        Config.Save();
        Avalonia.Application.Current!.RequestedThemeVariant =
            dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
    }

    public void OnConfigChanged()
    {
        _controller.OnConfigChanged();
        ApplyFontScale();
        InvalidateAll();
        OnPropertyChanged(nameof(ActiveTab));
    }

    public void OnSliderChanged() => _controller.OnSliderChanged();

    private const double BaseFontSize = 14.0;

    private void ApplyFontScale()
    {
        if (_window is not null)
            _window.FontSize = BaseFontSize * Config.UiFontScale;
    }

    public double CurrentFontSize => BaseFontSize * Config.UiFontScale;

    public void SetViewportSize(double w, double h) => _controller.SetViewportSize(w, h);

    // --- Annotations ---

    public void OpenRadialMenu(double screenX, double screenY)
    {
        double menuSize = 210 * Config.UiFontScale;
        RadialMenuX = screenX - menuSize / 2;
        RadialMenuY = screenY - menuSize / 2;
        IsRadialMenuOpen = true;
    }

    public void CloseRadialMenu() => IsRadialMenuOpen = false;

    public void SetAnnotationTool(AnnotationTool tool)
    {
        _controller.SetAnnotationTool(tool);

        if (tool != AnnotationTool.TextSelect)
        {
            OnPropertyChanged(nameof(SelectedText));
            InvalidateAnnotations();
        }

        CloseRadialMenu();
        OnPropertyChanged(nameof(IsAnnotating));
        OnPropertyChanged(nameof(ActiveTool));
    }

    public void CancelAnnotationTool()
    {
        _controller.CancelAnnotationTool();
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(IsAnnotating));
        OnPropertyChanged(nameof(ActiveTool));
        InvalidateAnnotations();
    }

    public void HandleAnnotationPointerDown(double pageX, double pageY)
    {
        var (needsDialog, isEdit, existingNote, px, py) = _controller.HandleAnnotationPointerDown(pageX, pageY);

        if (needsDialog)
        {
            if (isEdit && existingNote is not null)
                EditTextNote(existingNote);
            else
                CreateTextNote(px, py);
        }
        else
        {
            OnPropertyChanged(nameof(SelectedText));
            InvalidateAnnotations();
        }
    }

    public void HandleAnnotationPointerMove(double pageX, double pageY)
    {
        if (_controller.HandleAnnotationPointerMove(pageX, pageY))
        {
            OnPropertyChanged(nameof(SelectedText));
            InvalidateAnnotations();
        }
    }

    public void HandleAnnotationPointerUp(double pageX, double pageY)
    {
        if (_controller.HandleAnnotationPointerUp(pageX, pageY))
            InvalidateAnnotations();
    }

    private async void CreateTextNote(float pageX, float pageY)
    {
        if (_window is null) return;
        var dialog = new TextNoteDialog { FontSize = CurrentFontSize };
        var result = await dialog.ShowDialog<string?>(_window);
        if (string.IsNullOrEmpty(result)) return;

        _controller.CompleteTextNote(pageX, pageY, result);
        InvalidateAnnotations();
    }

    private async void EditTextNote(TextNoteAnnotation note)
    {
        if (_window is null) return;
        var dialog = new TextNoteDialog(note.Text) { FontSize = CurrentFontSize };
        var result = await dialog.ShowDialog<string?>(_window);
        if (result is null) return;

        _controller.CompleteTextNoteEdit(note, result);
        InvalidateAnnotations();
    }

    // --- Browse-mode annotation interaction ---

    /// <summary>
    /// Handle browse-mode pointer down. Returns true if an annotation was hit
    /// (caller should not start camera pan).
    /// </summary>
    public bool HandleBrowsePointerDown(float pageX, float pageY)
    {
        bool hit = _controller.HandleBrowsePointerDown(pageX, pageY);
        OnPropertyChanged(nameof(SelectedAnnotation));
        InvalidateAnnotations();
        return hit;
    }

    public bool HandleBrowsePointerMove(float pageX, float pageY)
    {
        if (_controller.HandleBrowsePointerMove(pageX, pageY))
        {
            InvalidateAnnotations();
            return true;
        }
        return false;
    }

    public bool HandleBrowsePointerUp(float pageX, float pageY)
    {
        if (_controller.HandleBrowsePointerUp(pageX, pageY))
        {
            InvalidateAnnotations();
            return true;
        }
        return false;
    }

    public (bool Handled, TextNoteAnnotation? EditNote) HandleBrowseClick(float pageX, float pageY, bool isDoubleClick = false)
    {
        var result = _controller.HandleBrowseClick(pageX, pageY, isDoubleClick);
        if (result.Handled)
        {
            OnPropertyChanged(nameof(SelectedAnnotation));
            InvalidateAnnotations();
        }
        if (result.EditNote is not null)
            EditTextNote(result.EditNote);
        return result;
    }

    public void CopySelectedText()
    {
        _controller.CopySelectedText();
        CloseRadialMenu();
    }

    public void DeleteSelectedAnnotation()
    {
        if (_controller.DeleteSelectedAnnotation())
        {
            OnPropertyChanged(nameof(SelectedAnnotation));
            InvalidateAnnotations();
        }
    }

    public void UndoAnnotation()
    {
        _controller.UndoAnnotation();
        InvalidateAnnotations();
    }

    public void RedoAnnotation()
    {
        _controller.RedoAnnotation();
        InvalidateAnnotations();
    }

    [RelayCommand]
    public async Task ExportAnnotated()
    {
        if (_window is null || ActiveTab is not { } tab) return;

        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export with Annotations",
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF Files") { Patterns = ["*.pdf"] }],
            SuggestedFileName = Path.GetFileNameWithoutExtension(tab.FilePath) + "_annotated.pdf",
        });
        if (file is null) return;

        var outputPath = file.TryGetLocalPath() ?? file.Path.LocalPath;
        if (outputPath is null) return;

        try
        {
            await Task.Run(() =>
            {
                AnnotationExportService.Export(tab.Pdf, tab.Annotations, outputPath,
                    onProgress: (page, total) =>
                        Console.Error.WriteLine($"[Export] Page {page + 1} of {total}..."));
            });
            Console.Error.WriteLine($"[Export] Saved to {outputPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Export] Failed: {ex.Message}");
        }
    }

    // --- Search ---

    public event Action<string?>? SearchRequested;

    public void OpenSearch()
    {
        ShowOutline = true;
        SearchRequested?.Invoke(null);
    }

    public void SearchForSelectedText()
    {
        if (SelectedText is not { Length: > 0 } text) return;
        ShowOutline = true;
        SearchRequested?.Invoke(text);
    }

    public void CloseSearch()
    {
        _controller.CloseSearch();
        InvalidateSearch();
    }

    public void ExecuteSearch(string query, bool caseSensitive, bool useRegex)
    {
        _controller.ExecuteSearch(query, caseSensitive, useRegex);
        InvalidateSearch();
    }

    public void InvalidateSearchLayer() => InvalidateSearch();

    public void NextMatch()
    {
        _controller.NextMatch();
        InvalidateAfterNavigation();
    }

    public void PreviousMatch()
    {
        _controller.PreviousMatch();
        InvalidateAfterNavigation();
    }

    public void GoToMatch(int matchIndex)
    {
        _controller.GoToMatch(matchIndex);
        InvalidateAfterNavigation();
    }

    public void UpdateCurrentPageMatches() => _controller.UpdateCurrentPageMatches();

    // --- Invalidation helpers ---

    private void InvalidateCamera() => _invalidation?.InvalidateCamera?.Invoke();
    private void InvalidatePage() => _invalidation?.InvalidatePage?.Invoke();
    private void InvalidateOverlay() => _invalidation?.InvalidateOverlay?.Invoke();
    private void InvalidateSearch() => _invalidation?.InvalidateSearch?.Invoke();
    private void InvalidateAnnotations() => _invalidation?.InvalidateAnnotations?.Invoke();

    private void InvalidateAfterNavigation()
    {
        OnPropertyChanged(nameof(ActiveTab));
        InvalidateAll();
        RequestAnimationFrame();
    }

    private void InvalidateCameraAndTab()
    {
        InvalidateCamera();
        OnPropertyChanged(nameof(ActiveTab));
    }

    private void InvalidateNavigation()
    {
        InvalidateCamera();
        InvalidateOverlay();
        InvalidatePage();
        OnPropertyChanged(nameof(ActiveTab));
        RequestAnimationFrame();
    }

    private void InvalidateAll()
    {
        InvalidateCamera();
        InvalidatePage();
        InvalidateOverlay();
        InvalidateSearch();
        InvalidateAnnotations();
    }

    public void RequestCameraUpdate() => InvalidateCamera();
}

public sealed class InvalidationCallbacks
{
    public Action? InvalidateCamera { get; init; }
    public Action? InvalidatePage { get; init; }
    public Action? InvalidateOverlay { get; init; }
    public Action? InvalidateSearch { get; init; }
    public Action? InvalidateAnnotations { get; init; }
}
