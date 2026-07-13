using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class StatusBarView : UserControl
{
    private static readonly IBrush RailModeBrush = new SolidColorBrush(Color.FromRgb(66, 133, 244));
    private static readonly IBrush AutoScrollBrush = new SolidColorBrush(Color.FromRgb(0, 180, 190));
    private static readonly IBrush AmberBrush = new SolidColorBrush(Color.FromRgb(255, 170, 0));
    private static readonly IBrush DangerBrush = new SolidColorBrush(Color.FromRgb(255, 100, 100));

    private MainWindowViewModel? _subscribedVm;
    private TabViewModel? _subscribedTab;
    private TextBlock? _zoomLabel;
    private TextBlock? _breadcrumbLabel;
    private TextBlock? _railStatusLabel;

    // Structural shape of the last full rebuild — everything that decides WHICH children exist (not
    // their text). ActiveTab is re-raised from inside the per-frame animation tick during rail scrolling
    // (see MainWindowViewModel.RunAnimationFrame), so UpdateStatus is a hot path there; when the shape is
    // unchanged from last time, UpdateInPlace() patches just the mutable label text instead of a full
    // StatusPanel.Children.Clear() + rebuild (fresh Buttons + lambda closures) every frame.
    private readonly record struct StatusShape(
        bool PendingRail, bool RailActive, bool AutoScrollActive, bool AutoScrollParked, bool JumpMode,
        bool IsViewRotated, bool IsAnnotating, AnnotationTool ActiveTool, bool HasBreadcrumb, bool HasToast);
    private StatusShape? _lastShape;
    private TabViewModel? _lastShapeTab;
    private TextBlock? _toastLabel;

    public StatusBarView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UpdateStatus();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is MainWindowViewModel vm)
        {
            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            SubscribeToTab(vm.ActiveTab);
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm = null;
        }
        SubscribeToTab(null);
        base.OnUnloaded(e);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainWindowViewModel.ActiveTab) or
            nameof(MainWindowViewModel.ActiveTabIndex) or
            nameof(MainWindowViewModel.ActiveTool) or
            nameof(MainWindowViewModel.AutoScrollActive) or
            nameof(MainWindowViewModel.AutoScrollParked) or
            nameof(MainWindowViewModel.JumpMode) or
            nameof(MainWindowViewModel.IsViewRotated) or
            nameof(MainWindowViewModel.StatusToast))
        {
            SubscribeToTab(_subscribedVm?.ActiveTab);
            UpdateStatus();
        }
    }

    private void SubscribeToTab(TabViewModel? tab)
    {
        if (_subscribedTab is not null)
            _subscribedTab.PropertyChanged -= OnTabPropertyChanged;
        _subscribedTab = tab;
        if (tab is not null)
            tab.PropertyChanged += OnTabPropertyChanged;
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TabViewModel.CurrentPage) or nameof(TabViewModel.PendingRailSetup))
            UpdateStatus();
    }

    private static Avalonia.Media.Geometry? Geo(string key)
        => Avalonia.Application.Current?.TryGetResource(key, null, out var g) == true
            ? g as Avalonia.Media.Geometry
            : null;

    private static Button MakeNavButton(string iconKey, EventHandler<RoutedEventArgs> handler,
        string? tooltip = null, string? automationId = null)
    {
        var btn = new Button
        {
            Content = new RailReader2.Controls.Icon { Data = Geo(iconKey) },
            Padding = new Avalonia.Thickness(6, 0),
            MinWidth = 0,
        };
        if (tooltip is not null)
        {
            ToolTip.SetTip(btn, tooltip);
            Avalonia.Automation.AutomationProperties.SetName(btn, tooltip);
        }
        if (automationId is not null)
            Avalonia.Automation.AutomationProperties.SetAutomationId(btn, automationId);
        btn.Click += handler;
        return btn;
    }

    private static Button MakeDangerButton(string iconKey, EventHandler<RoutedEventArgs> handler, string? tooltip = null)
    {
        var btn = MakeNavButton(iconKey, handler, tooltip);
        btn.Foreground = DangerBrush;
        return btn;
    }

    public bool IsEditing { get; private set; }
    private TextBlock? _pageLabel;

    /// <summary>
    /// Lightweight zoom-only update called from the camera invalidation path
    /// so the displayed zoom stays current during animations.
    /// </summary>
    public void UpdateZoom()
    {
        if (_zoomLabel is null) return;
        var vm = DataContext as MainWindowViewModel;
        if (vm?.ActiveTab is null) return;
        // The focused viewport's zoom (a split pane / tear-off can be at a different zoom than Primary).
        double zoom = vm.Controller.FocusedViewport?.Camera.Zoom ?? vm.ActiveTab.Camera.Zoom;
        int pct = (int)Math.Round(zoom * 100);
        _zoomLabel.Text = $"Zoom: {pct}%";
        Avalonia.Automation.AutomationProperties.SetName(_zoomLabel, $"Zoom {pct} percent");
    }

    private void BeginPageEdit(MainWindowViewModel vm, TabViewModel tab)
    {
        if (_pageLabel is null) return;
        int idx = StatusPanel.Children.IndexOf(_pageLabel);
        if (idx < 0) return;

        var input = new TextBox
        {
            Text = ((vm.Controller.FocusedViewport?.CurrentPage ?? tab.CurrentPage) + 1).ToString(),
            Width = 50,
            MinHeight = 0,
            Padding = new Avalonia.Thickness(4, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        IsEditing = true;
        // The edit box below replaces _pageLabel's panel slot in place, without updating the field —
        // force UpdateStatus's next call (from Commit/Escape) onto the full-rebuild path so it actually
        // restores the label, instead of the fast path patching the now-detached _pageLabel's text and
        // leaving this stale TextBox on screen.
        _lastShape = null;

        void Commit()
        {
            if (!IsEditing) return;
            IsEditing = false;
            if (int.TryParse(input.Text?.Trim(), out int page))
                vm.GoToPage(page - 1); // 1-based input → 0-based
            UpdateStatus();
        }

        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { IsEditing = false; UpdateStatus(); e.Handled = true; }
        };
        input.LostFocus += (_, _) => Commit();

        StatusPanel.Children[idx] = input;
        input.Focus();
        input.SelectAll();
    }

    private void BeginZoomEdit(MainWindowViewModel vm, TabViewModel tab)
    {
        if (_zoomLabel is null) return;
        int idx = StatusPanel.Children.IndexOf(_zoomLabel);
        if (idx < 0) return;

        var input = new TextBox
        {
            Text = ((int)Math.Round((vm.Controller.FocusedViewport?.Camera.Zoom ?? tab.Camera.Zoom) * 100)).ToString(),
            Width = 56,
            MinHeight = 0,
            Padding = new Avalonia.Thickness(4, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        IsEditing = true;
        // See BeginPageEdit: force the next UpdateStatus onto the full-rebuild path since this edit box
        // replaces _zoomLabel's panel slot without updating the field.
        _lastShape = null;

        void Commit()
        {
            if (!IsEditing) return;
            IsEditing = false;
            // Accept "150", "150%", "150 %".
            var text = input.Text?.Replace("%", "").Trim();
            if (double.TryParse(text, out double pct))
                vm.SetZoomPercent(pct); // clamped to 50–2000% in the VM
            UpdateStatus();
        }

        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { IsEditing = false; UpdateStatus(); e.Handled = true; }
        };
        input.LostFocus += (_, _) => Commit();

        StatusPanel.Children[idx] = input;
        input.Focus();
        input.SelectAll();
    }

    private void AddSeparator() =>
        StatusPanel.Children.Add(new TextBlock { Text = "|", Opacity = 0.5 });

    private const int BreadcrumbMaxChars = 60;
    private const string BreadcrumbSeparator = " \u203a ";  // ›

    /// <summary>The full (untruncated) breadcrumb path text for <paramref name="currentPage"/>, or null
    /// when the document has no outline / the page isn't under any outline entry.</summary>
    private static string? ComputeBreadcrumbText(TabViewModel tab, int currentPage)
    {
        var outline = tab.Outline;
        if (outline is null || outline.Count == 0) return null;
        var path = OutlineBreadcrumb.BuildPath(outline, currentPage);
        return path.Count == 0 ? null : string.Join(BreadcrumbSeparator, path.Select(e => e.Title));
    }

    private void AddBreadcrumb(TabViewModel tab, int currentPage)
    {
        var full = ComputeBreadcrumbText(tab, currentPage);
        if (full is null) return;

        AddSeparator();
        _breadcrumbLabel = new TextBlock
        {
            Text = TruncateBreadcrumb(full, BreadcrumbMaxChars),
            FontStyle = FontStyle.Italic,
            Opacity = 0.85,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        if (full.Length > BreadcrumbMaxChars)
            ToolTip.SetTip(_breadcrumbLabel, full);
        StatusPanel.Children.Add(_breadcrumbLabel);
    }

    /// <summary>
    /// If the path exceeds maxChars, keep the leaf and prepend an ellipsis.
    /// Tooltip carries the full path so context isn't lost.
    /// </summary>
    private static string TruncateBreadcrumb(string full, int maxChars)
    {
        if (full.Length <= maxChars) return full;
        const string ellipsis = "\u2026" + BreadcrumbSeparator;  // …›
        int sepIdx = full.LastIndexOf(BreadcrumbSeparator, StringComparison.Ordinal);
        if (sepIdx < 0) return ellipsis + full[^Math.Min(maxChars, full.Length)..];
        var leaf = full[(sepIdx + BreadcrumbSeparator.Length)..];
        return ellipsis + leaf;
    }

    private static TextBlock MakeBoldLabel(string text, IBrush foreground) => new()
    {
        Text = text,
        Foreground = foreground,
        FontWeight = FontWeight.Bold,
    };

    private void UpdateStatus()
    {
        if (IsEditing) return;
        var vm = DataContext as MainWindowViewModel;
        var tab = vm?.ActiveTab;
        if (tab is null)
        {
            _lastShape = null;
            _lastShapeTab = null;
            StatusPanel.Children.Clear();
            StatusPanel.Children.Add(new TextBlock { Text = "No document open" });
            return;
        }

        // The focused viewport drives the page/zoom/rail readout (a split pane / tear-off can sit on a
        // different page/zoom/rail than the Primary). Document-level facts (page count, outline) use tab.
        var vp = vm!.Controller.FocusedViewport;
        int curPage = vp?.CurrentPage ?? tab.CurrentPage;
        double zoom = vp?.Camera.Zoom ?? tab.Camera.Zoom;
        var rail = vp?.Rail ?? tab.Rail;
        bool pendingRail = vp?.PendingRailSetup ?? tab.PendingRailSetup;
        string? breadcrumbFull = ComputeBreadcrumbText(tab, curPage);

        var shape = new StatusShape(
            pendingRail, rail.Active, vm.AutoScrollActive, vm.AutoScrollParked, vm.JumpMode,
            vm.IsViewRotated, vm.IsAnnotating, vm.ActiveTool, breadcrumbFull is not null, vm.StatusToast is not null);

        // Fast path: the set of children hasn't changed since the last rebuild (the overwhelmingly common
        // case while continuously rail-reading — ActiveTab is re-raised every animation frame, see
        // RunAnimationFrame), so just patch the mutable label text instead of Children.Clear() + rebuilding
        // ~10 fresh Buttons/TextBlocks (with fresh lambda closures) on every frame. Requires the SAME tab
        // instance as last time too — BeginPageEdit/BeginZoomEdit's button handlers close over `tab`, so a
        // tab switch (ActiveTabIndex changed) that happens to keep the same shape must still rebuild, or
        // those handlers would keep editing the previous tab.
        if (_lastShape == shape && ReferenceEquals(_lastShapeTab, tab) && _pageLabel is not null && _zoomLabel is not null)
        {
            UpdateLabelsInPlace(vm, tab, curPage, zoom, rail, breadcrumbFull);
            return;
        }
        _lastShape = shape;
        _lastShapeTab = tab;

        StatusPanel.Children.Clear();
        int zoomPct = (int)Math.Round(zoom * 100);
        StatusPanel.Children.Add(MakeNavButton("IconChevronLeft", (_, _) =>
        { if (vm?.Controller.FocusedViewport is { } v) vm.GoToPage(v.CurrentPage - 1); }, "Previous page (PgUp)", "PreviousPage"));
        _pageLabel = new TextBlock
        {
            Text = $"Page {curPage + 1}/{tab.PageCount}",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(_pageLabel, "Click to go to page");
        // Spoken form reads cleaner than the compact "3/15"; AutomationId is a stable handle.
        Avalonia.Automation.AutomationProperties.SetName(_pageLabel, $"Page {curPage + 1} of {tab.PageCount}");
        Avalonia.Automation.AutomationProperties.SetAutomationId(_pageLabel, "PageIndicator");
        _pageLabel.Tapped += (_, _) => BeginPageEdit(vm!, tab);
        StatusPanel.Children.Add(_pageLabel);
        StatusPanel.Children.Add(MakeNavButton("IconChevronRight", (_, _) =>
        { if (vm?.Controller.FocusedViewport is { } v) vm.GoToPage(v.CurrentPage + 1); }, "Next page (PgDn)", "NextPage"));
        AddSeparator();
        _zoomLabel = new TextBlock
        {
            Text = $"Zoom: {zoomPct}%",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(_zoomLabel, "Click to set zoom");
        Avalonia.Automation.AutomationProperties.SetName(_zoomLabel, $"Zoom {zoomPct} percent");
        Avalonia.Automation.AutomationProperties.SetAutomationId(_zoomLabel, "ZoomIndicator");
        _zoomLabel.Tapped += (_, _) => BeginZoomEdit(vm!, tab);
        StatusPanel.Children.Add(_zoomLabel);

        AddBreadcrumb(tab, curPage);

        if (pendingRail)
        {
            AddSeparator();
            StatusPanel.Children.Add(new TextBlock
            {
                Text = "Analyzing\u2026",
                Opacity = 0.6,
                FontStyle = Avalonia.Media.FontStyle.Italic,
            });
        }
        else if (rail.Active)
        {
            AddSeparator();
            _railStatusLabel = new TextBlock { Text = RailStatusText(rail) };
            StatusPanel.Children.Add(_railStatusLabel);
            AddSeparator();
            StatusPanel.Children.Add(MakeBoldLabel("Rail Mode", RailModeBrush));

            if (vm is { AutoScrollActive: true })
            {
                AddSeparator();
                StatusPanel.Children.Add(vm.AutoScrollParked
                    ? MakeBoldLabel("Parked — press D to continue", AmberBrush)
                    : MakeBoldLabel("Auto-Scroll", AutoScrollBrush));
                StatusPanel.Children.Add(MakeDangerButton("IconPause", (_, _) => vm.StopAutoScroll(), "Stop auto-scroll (P)"));
            }

            if (vm is { JumpMode: true })
            {
                AddSeparator();
                StatusPanel.Children.Add(MakeBoldLabel("Jump", AmberBrush));
                StatusPanel.Children.Add(MakeDangerButton("IconClose", (_, _) => vm.JumpMode = false, "Exit jump mode (J)"));
            }
        }

        // Persistent rotation badge (the rotate toasts are transient): says why annotation tools are
        // greyed out and offers the way back.
        if (vm is { IsViewRotated: true })
        {
            AddSeparator();
            StatusPanel.Children.Add(MakeBoldLabel($"Rotated {vm.ViewRotationDegrees}°", AmberBrush));
            StatusPanel.Children.Add(MakeDangerButton("IconClose", (_, _) => vm.ResetViewRotation(), "Reset rotation"));
        }

        if (vm is { IsAnnotating: true })
        {
            AddSeparator();
            string toolName = vm.ActiveTool switch
            {
                AnnotationTool.Highlight => "Highlight",
                AnnotationTool.Pen => "Pen",
                AnnotationTool.TextNote => "Text Note",
                AnnotationTool.Rectangle => "Rectangle",
                AnnotationTool.Eraser => "Eraser",
                AnnotationTool.TextSelect => "Text Select",
                _ => "Annotating",
            };
            StatusPanel.Children.Add(MakeBoldLabel($"{toolName} Tool", AmberBrush));
            StatusPanel.Children.Add(MakeDangerButton("IconClose", (_, _) => vm.CancelAnnotationTool(), "Cancel tool (Escape)"));
        }

        if (vm?.StatusToast is { } toast)
        {
            AddSeparator();
            _toastLabel = MakeBoldLabel(toast, AmberBrush);
            StatusPanel.Children.Add(_toastLabel);
        }
    }

    private static string RailStatusText(RailNav rail) =>
        $"Block {rail.CurrentBlock + 1}/{rail.NavigableCount} | Line {rail.CurrentLine + 1}/{rail.CurrentLineCount}";

    /// <summary>Fast path for UpdateStatus: the panel's children are unchanged from the last full
    /// rebuild, so only patch the text/tooltip/automation-name of the labels that can still have changed
    /// (page, zoom, breadcrumb, rail position, toast message) — no new controls, no Children mutation.</summary>
    private void UpdateLabelsInPlace(
        MainWindowViewModel vm, TabViewModel tab, int curPage, double zoom, RailNav rail, string? breadcrumbFull)
    {
        int zoomPct = (int)Math.Round(zoom * 100);

        _pageLabel!.Text = $"Page {curPage + 1}/{tab.PageCount}";
        Avalonia.Automation.AutomationProperties.SetName(_pageLabel, $"Page {curPage + 1} of {tab.PageCount}");

        _zoomLabel!.Text = $"Zoom: {zoomPct}%";
        Avalonia.Automation.AutomationProperties.SetName(_zoomLabel, $"Zoom {zoomPct} percent");

        if (_breadcrumbLabel is not null && breadcrumbFull is not null)
        {
            _breadcrumbLabel.Text = TruncateBreadcrumb(breadcrumbFull, BreadcrumbMaxChars);
            ToolTip.SetTip(_breadcrumbLabel, breadcrumbFull.Length > BreadcrumbMaxChars ? breadcrumbFull : null);
        }

        if (rail.Active && _railStatusLabel is not null)
            _railStatusLabel.Text = RailStatusText(rail);

        if (vm.StatusToast is { } toast && _toastLabel is not null)
            _toastLabel.Text = toast;
    }
}
