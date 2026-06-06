using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using RailReader.Core;
using RailReader.Core.Models;
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
            nameof(MainWindowViewModel.JumpMode) or
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
        var tab = (DataContext as MainWindowViewModel)?.ActiveTab;
        if (tab is null) return;
        int pct = (int)Math.Round(tab.Camera.Zoom * 100);
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
            Text = (tab.CurrentPage + 1).ToString(),
            Width = 50,
            MinHeight = 0,
            Padding = new Avalonia.Thickness(4, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        IsEditing = true;

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
            Text = ((int)Math.Round(tab.Camera.Zoom * 100)).ToString(),
            Width = 56,
            MinHeight = 0,
            Padding = new Avalonia.Thickness(4, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        IsEditing = true;

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

    private void AddBreadcrumb(TabViewModel tab)
    {
        var outline = tab.Outline;
        if (outline is null || outline.Count == 0) return;

        var path = OutlineBreadcrumb.BuildPath(outline, tab.CurrentPage);
        if (path.Count == 0) return;

        var full = string.Join(BreadcrumbSeparator, path.Select(e => e.Title));
        AddSeparator();
        var label = new TextBlock
        {
            Text = TruncateBreadcrumb(full, BreadcrumbMaxChars),
            FontStyle = FontStyle.Italic,
            Opacity = 0.85,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        if (full.Length > BreadcrumbMaxChars)
            ToolTip.SetTip(label, full);
        StatusPanel.Children.Add(label);
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
        StatusPanel.Children.Clear();
        var vm = DataContext as MainWindowViewModel;
        var tab = vm?.ActiveTab;
        if (tab is null)
        {
            StatusPanel.Children.Add(new TextBlock { Text = "No document open" });
            return;
        }

        int zoomPct = (int)Math.Round(tab.Camera.Zoom * 100);
        StatusPanel.Children.Add(MakeNavButton("IconChevronLeft", (_, _) =>
        { if (vm?.ActiveTab is { } t) vm.GoToPage(t.CurrentPage - 1); }, "Previous page (PgUp)", "PreviousPage"));
        _pageLabel = new TextBlock
        {
            Text = $"Page {tab.CurrentPage + 1}/{tab.PageCount}",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(_pageLabel, "Click to go to page");
        // Spoken form reads cleaner than the compact "3/15"; AutomationId is a stable handle.
        Avalonia.Automation.AutomationProperties.SetName(_pageLabel, $"Page {tab.CurrentPage + 1} of {tab.PageCount}");
        Avalonia.Automation.AutomationProperties.SetAutomationId(_pageLabel, "PageIndicator");
        _pageLabel.Tapped += (_, _) => BeginPageEdit(vm!, tab);
        StatusPanel.Children.Add(_pageLabel);
        StatusPanel.Children.Add(MakeNavButton("IconChevronRight", (_, _) =>
        { if (vm?.ActiveTab is { } t) vm.GoToPage(t.CurrentPage + 1); }, "Next page (PgDn)", "NextPage"));
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

        AddBreadcrumb(tab);

        if (tab.PendingRailSetup)
        {
            AddSeparator();
            StatusPanel.Children.Add(new TextBlock
            {
                Text = "Analyzing\u2026",
                Opacity = 0.6,
                FontStyle = Avalonia.Media.FontStyle.Italic,
            });
        }
        else if (tab.Rail.Active)
        {
            AddSeparator();
            StatusPanel.Children.Add(new TextBlock
            {
                Text = $"Block {tab.Rail.CurrentBlock + 1}/{tab.Rail.NavigableCount} | " +
                       $"Line {tab.Rail.CurrentLine + 1}/{tab.Rail.CurrentLineCount}"
            });
            AddSeparator();
            StatusPanel.Children.Add(MakeBoldLabel("Rail Mode", RailModeBrush));

            if (vm is { AutoScrollActive: true })
            {
                AddSeparator();
                StatusPanel.Children.Add(MakeBoldLabel("Auto-Scroll", AutoScrollBrush));
                StatusPanel.Children.Add(MakeDangerButton("IconPause", (_, _) => vm.StopAutoScroll(), "Stop auto-scroll (P)"));
            }

            if (vm is { JumpMode: true })
            {
                AddSeparator();
                StatusPanel.Children.Add(MakeBoldLabel("Jump", AmberBrush));
                StatusPanel.Children.Add(MakeDangerButton("IconClose", (_, _) => vm.JumpMode = false, "Exit jump mode (J)"));
            }
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
            StatusPanel.Children.Add(MakeBoldLabel(toast, AmberBrush));
        }
    }
}
