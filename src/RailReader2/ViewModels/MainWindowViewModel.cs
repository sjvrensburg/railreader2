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

// TODO: Many methods are single-line passthroughs (controller call + invalidation).
//       Consider a helper like Dispatch(Action<DocumentController>, InvalidationFlags) to reduce boilerplate.
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly DocumentController _controller;
    private readonly ILogger _logger;
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
    [ObservableProperty] private bool _showFullScreenFooter;
    [ObservableProperty] private bool _isRadialMenuOpen;
    [ObservableProperty] private bool _showBookmarkDialog;
    [ObservableProperty] private double _radialMenuX;
    [ObservableProperty] private double _radialMenuY;

    /// <summary>True when the tab bar should be visible (not fullscreen, or hovering at top edge).</summary>
    public bool IsTabBarVisible => !IsFullScreen || ShowFullScreenHeader;

    /// <summary>True when the status bar should be visible (not fullscreen, or hovering at bottom edge).</summary>
    public bool IsStatusBarVisible => !IsFullScreen || ShowFullScreenFooter;

    partial void OnIsFullScreenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTabBarVisible));
        OnPropertyChanged(nameof(IsStatusBarVisible));
        if (!value)
        {
            ShowFullScreenHeader = false;
            ShowFullScreenFooter = false;
        }
    }

    partial void OnShowFullScreenHeaderChanged(bool value) => OnPropertyChanged(nameof(IsTabBarVisible));
    partial void OnShowFullScreenFooterChanged(bool value) => OnPropertyChanged(nameof(IsStatusBarVisible));

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    // Delegated state from controller subsystems
    public List<SearchMatch> SearchMatches => _controller.Search.SearchMatches;
    public List<SearchMatch>? CurrentPageSearchMatches => _controller.Search.CurrentPageSearchMatches;
    public int ActiveMatchIndex
    {
        get => _controller.Search.ActiveMatchIndex;
        set => _controller.Search.ActiveMatchIndex = value;
    }

    public AnnotationTool ActiveTool => _controller.Annotations.ActiveTool;
    public bool IsAnnotating => _controller.Annotations.IsAnnotating;
    public Annotation? SelectedAnnotation
    {
        get => _controller.Annotations.SelectedAnnotation;
        set => _controller.Annotations.SelectedAnnotation = value;
    }
    public Annotation? PreviewAnnotation => _controller.Annotations.PreviewAnnotation;
    public string ActiveAnnotationColor
    {
        get => _controller.Annotations.ActiveAnnotationColor;
        set => _controller.Annotations.ActiveAnnotationColor = value;
    }
    public float ActiveAnnotationOpacity
    {
        get => _controller.Annotations.ActiveAnnotationOpacity;
        set => _controller.Annotations.ActiveAnnotationOpacity = value;
    }
    public float ActiveStrokeWidth
    {
        get => _controller.Annotations.ActiveStrokeWidth;
        set => _controller.Annotations.ActiveStrokeWidth = value;
    }

    public string? SelectedText => _controller.Annotations.SelectedText;
    public List<HighlightRect>? TextSelectionRects => _controller.Annotations.TextSelectionRects;

    public Action<string>? CopyToClipboard
    {
        get => _controller.Annotations.CopyToClipboard;
        set => _controller.Annotations.CopyToClipboard = value;
    }

    public bool AutoScrollActive => _controller.AutoScrollActive;
    public bool RailPaused => _controller.RailPaused;

    public void ResumeRailFromPause()
    {
        _controller.ResumeRailFromPause();
        InvalidateCameraAndTab();
        RequestAnimationFrame();
    }

    [ObservableProperty] private bool _jumpMode;
    partial void OnJumpModeChanged(bool value) => _controller.JumpMode = value;

    public AppConfig Config => _controller.Config;
    public ColourEffectShaders ColourEffects { get; }
    public DocumentController Controller => _controller;

    public TabViewModel? ActiveTab =>
        ActiveTabIndex >= 0 && ActiveTabIndex < Tabs.Count ? Tabs[ActiveTabIndex] : null;

    /// <summary>Path to the current session log file, or null if file logging unavailable.</summary>
    public string? LogFilePath => _logger.LogFilePath;

    public MainWindowViewModel(AppConfig config, ILogger? logger = null)
    {
        _logger = logger ?? AppConfig.Logger;
        ColourEffects = new ColourEffectShaders(_logger);
        _controller = new DocumentController(config, new AvaloniaThreadMarshaller(),
            new RailReader.Renderer.Skia.SkiaPdfServiceFactory(), _logger);
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
            _logger.Debug($"[OpenDocument] Opening: {path}");

            TabViewModel? tab = null;
            await Task.Run(() =>
            {
                var state = _controller.CreateDocument(path);
                state.LoadPageBitmap();
                tab = new TabViewModel(state);
            });

            if (tab is null) return;

            _logger.Debug($"[OpenDocument] Loaded: {tab.PageCount} pages, {tab.PageWidth}x{tab.PageHeight}");
            tab.LoadAnnotations(_controller.AnnotationManager);

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

            // Navigate duplicate tab to the source tab's page
            if (_pendingDuplicatePage is { } dupPage)
            {
                _pendingDuplicatePage = null;
                _controller.GoToPage(dupPage);
            }

            InvalidateAll();

            Dispatcher.UIThread.Post(() => InvalidatePage(), DispatcherPriority.Background);
            RequestAnimationFrame();

            _logger.Debug("[OpenDocument] Tab added successfully");
        }
        catch (Exception ex)
        {
            _pendingDuplicatePage = null;
            _logger.Error($"Failed to open {path}", ex);
            ShowStatusToast($"Failed to open: {Path.GetFileName(path)}");
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
            _logger.Debug($"[OpenFile] Selected: {path}");
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
        {
            _pendingDuplicatePage = tab.CurrentPage;
            OpenDocument(tab.FilePath);
        }
    }

    private int? _pendingDuplicatePage;

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

    public void NavigateForward()
    {
        _controller.NavigateForward();
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

    public void HandlePan(double dx, double dy, bool ctrlHeld = false)
    {
        _controller.HandlePan(dx, dy, ctrlHeld);
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
        // In rail mode, the animation loop drives all camera updates via Tick().
        // Key repeats only keep StartScroll alive (a no-op when direction unchanged).
        // Calling InvalidateCamera here would redundantly update the MatrixTransform
        // at key-repeat rate (~30-40 Hz) on top of the animation frame updates.
        if (ActiveTab?.Rail.Active != true)
            InvalidateCamera();
        RequestAnimationFrame();
    }

    public void HandleArrowLeft(bool shortJump = false)
    {
        _controller.HandleArrowLeft(shortJump);
        if (ActiveTab?.Rail.Active != true)
            InvalidateCamera();
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
        var (handled, link) = _controller.HandleClick(canvasX, canvasY);
        if (link is UriDestination uriDest)
        {
            _ = PromptAndOpenUrl(uriDest.Uri);
            return;
        }
        if (handled)
            InvalidateNavigation();
    }

    private async Task PromptAndOpenUrl(string uri)
    {
        if (_window is null) return;

        // Only allow http/https URLs
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ShowStatusToast("Blocked non-HTTP link");
            return;
        }

        var dialog = new Views.ConfirmUrlDialog(uri);
        var result = await dialog.ShowDialog<bool?>(_window);
        if (result == true)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to open URL: {uri}", ex);
                ShowStatusToast("Failed to open link");
            }
        }
    }

    public bool IsOverLink(double pageX, double pageY)
        => _controller.HitTestLink(pageX, pageY) is not null;

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

    public void ToggleLineHighlight()
    {
        if (_controller.ActiveDocument is { } doc)
        {
            doc.LineHighlightEnabled = !doc.LineHighlightEnabled;
            InvalidateOverlay();
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
        _controller.Annotations.SetAnnotationTool(tool);

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
        _controller.Annotations.CancelAnnotationTool();
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(IsAnnotating));
        OnPropertyChanged(nameof(ActiveTool));
        InvalidateAnnotations();
    }

    public void HandleAnnotationPointerDown(double pageX, double pageY)
    {
        var (needsDialog, isEdit, existingNote, px, py) = _controller.Annotations.HandleAnnotationPointerDown(_controller.ActiveDocument, pageX, pageY);

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
        if (_controller.Annotations.HandleAnnotationPointerMove(_controller.ActiveDocument, pageX, pageY))
        {
            OnPropertyChanged(nameof(SelectedText));
            InvalidateAnnotations();
        }
    }

    public void HandleAnnotationPointerUp(double pageX, double pageY)
    {
        if (_controller.Annotations.HandleAnnotationPointerUp(_controller.ActiveDocument, pageX, pageY))
            InvalidateAnnotations();
    }

    private async void CreateTextNote(float pageX, float pageY)
    {
        if (_window is null) return;
        var dialog = new TextNoteDialog { FontSize = CurrentFontSize };
        var result = await dialog.ShowDialog<string?>(_window);
        if (string.IsNullOrEmpty(result)) return;

        _controller.Annotations.CompleteTextNote(_controller.ActiveDocument, pageX, pageY, result);
        InvalidateAnnotations();
    }

    private async void EditTextNote(TextNoteAnnotation note)
    {
        if (_window is null) return;
        var dialog = new TextNoteDialog(note.Text) { FontSize = CurrentFontSize };
        var result = await dialog.ShowDialog<string?>(_window);
        if (result is null) return;

        _controller.Annotations.CompleteTextNoteEdit(_controller.ActiveDocument, note, result);
        InvalidateAnnotations();
    }

    // --- Browse-mode annotation interaction ---

    /// <summary>
    /// Handle browse-mode pointer down. Returns true if an annotation was hit
    /// (caller should not start camera pan).
    /// </summary>
    public bool HandleBrowsePointerDown(float pageX, float pageY)
    {
        bool hit = _controller.Annotations.HandleBrowsePointerDown(_controller.ActiveDocument, pageX, pageY);
        OnPropertyChanged(nameof(SelectedAnnotation));
        InvalidateAnnotations();
        return hit;
    }

    public bool HandleBrowsePointerMove(float pageX, float pageY)
    {
        if (_controller.Annotations.HandleBrowsePointerMove(pageX, pageY))
        {
            InvalidateAnnotations();
            return true;
        }
        return false;
    }

    public bool HandleBrowsePointerUp(float pageX, float pageY)
    {
        if (_controller.Annotations.HandleBrowsePointerUp(_controller.ActiveDocument, pageX, pageY))
        {
            InvalidateAnnotations();
            return true;
        }
        return false;
    }

    public (bool Handled, TextNoteAnnotation? EditNote) HandleBrowseClick(float pageX, float pageY, bool isDoubleClick = false)
    {
        var result = _controller.Annotations.HandleBrowseClick(_controller.ActiveDocument, pageX, pageY, isDoubleClick);
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
        _controller.Annotations.CopySelectedText();
        CloseRadialMenu();
    }

    public void DeleteSelectedAnnotation()
    {
        if (_controller.Annotations.DeleteSelectedAnnotation(_controller.ActiveDocument))
        {
            OnPropertyChanged(nameof(SelectedAnnotation));
            InvalidateAnnotations();
        }
    }

    public void UndoAnnotation()
    {
        _controller.Annotations.UndoAnnotation(_controller.ActiveDocument);
        InvalidateAnnotations();
    }

    public void RedoAnnotation()
    {
        _controller.Annotations.RedoAnnotation(_controller.ActiveDocument);
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
                        _logger.Debug($"[Export] Page {page + 1} of {total}..."));
            });
            _logger.Info($"[Export] Saved to {outputPath}");
        }
        catch (Exception ex)
        {
            _logger.Error("[Export] Failed", ex);
        }
    }

    [RelayCommand]
    public async Task ExportAnnotationsJson()
    {
        if (_window is null || ActiveTab is not { } tab) return;

        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Annotations as JSON",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON Files") { Patterns = ["*.json"] }],
            SuggestedFileName = Path.GetFileNameWithoutExtension(tab.FilePath) + "_annotations.json",
        });
        if (file is null) return;

        var outputPath = file.TryGetLocalPath() ?? file.Path.LocalPath;
        if (outputPath is null) return;

        try
        {
            AnnotationService.ExportJson(tab.Annotations, outputPath);
            ShowStatusToast($"Annotations exported to {Path.GetFileName(outputPath)}");
        }
        catch (Exception ex)
        {
            _logger.Error("[Export JSON] Failed", ex);
        }
    }

    [RelayCommand]
    public async Task ImportAnnotationsJson()
    {
        if (_window is null || ActiveTab is not { } tab) return;

        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Annotations",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON Files") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0) return;

        var inputPath = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
        if (inputPath is null) return;

        try
        {
            var imported = AnnotationService.ImportJson(inputPath);
            if (imported is null)
            {
                ShowStatusToast("Failed to read annotation file");
                return;
            }

            int added = AnnotationService.MergeInto(tab.State.Annotations, imported);
            tab.State.MarkAnnotationsDirty();
            InvalidateAnnotations();
            ShowStatusToast(added > 0
                ? $"Imported {added} annotation(s) from {Path.GetFileName(inputPath)}"
                : "No new annotations found in file");
        }
        catch (Exception ex)
        {
            _logger.Error("[Import JSON] Failed", ex);
            ShowStatusToast("Failed to import annotations");
        }
    }

    [RelayCommand]
    public async Task ExportDiagnosticLog()
    {
        if (_window is null || LogFilePath is null || !File.Exists(LogFilePath)) return;

        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Diagnostic Log",
            DefaultExtension = "log",
            FileTypeChoices = [new FilePickerFileType("Log Files") { Patterns = ["*.log", "*.txt"] }],
            SuggestedFileName = $"railreader2-log-{DateTime.Now:yyyyMMdd-HHmmss}.log",
        });
        if (file is null) return;

        var outputPath = file.TryGetLocalPath() ?? file.Path.LocalPath;
        if (outputPath is null) return;

        try
        {
            using var output = File.Create(outputPath);
            using var writer = new StreamWriter(output);

            // Include previous session log if available (may contain crash info)
            if (_logger.PreviousLogFilePath is { } prevPath && File.Exists(prevPath))
            {
                writer.WriteLine("=== Previous session ===");
                writer.Flush();
                using (var prev = File.OpenRead(prevPath)) prev.CopyTo(output);
                writer.WriteLine();
                writer.WriteLine("=== Current session ===");
                writer.Flush();
            }

            using (var current = File.OpenRead(LogFilePath)) current.CopyTo(output);
            ShowStatusToast($"Log exported to {Path.GetFileName(outputPath)}");
        }
        catch (Exception ex)
        {
            _logger.Error("[Export Log] Failed", ex);
        }
    }

    // --- Search ---

    public event Action<string?>? SearchRequested;

    public void OpenSearch() => RequestSearch(null);

    public void SearchForSelectedText()
    {
        if (SelectedText?.Trim() is not { Length: > 0 } text) return;
        RequestSearch(text);
    }

    private void RequestSearch(string? prefill)
    {
        ShowOutline = true;
        SearchRequested?.Invoke(prefill);
    }

    public void CloseSearch()
    {
        _controller.Search.CloseSearch();
        InvalidateSearch();
    }

    public void ExecuteSearch(string query, bool caseSensitive, bool useRegex)
    {
        _controller.Search.ExecuteSearch(query, caseSensitive, useRegex);
        InvalidateSearch();
    }

    public void InvalidateSearchLayer() => InvalidateSearch();

    public void NextMatch()
    {
        _controller.Search.NextMatch();
        InvalidateAfterNavigation();
    }

    public void PreviousMatch()
    {
        _controller.Search.PreviousMatch();
        InvalidateAfterNavigation();
    }

    public void GoToMatch(int matchIndex)
    {
        _controller.Search.GoToMatch(matchIndex);
        InvalidateAfterNavigation();
    }

    public void UpdateCurrentPageMatches() => _controller.Search.UpdateCurrentPageMatches();

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
