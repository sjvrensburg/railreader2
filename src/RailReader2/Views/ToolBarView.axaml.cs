using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class ToolBarView : UserControl
{
    private MainWindowViewModel? _vm;
    private readonly Flyout _colorFlyout = new();
    private readonly Flyout _thicknessFlyout = new();
    // Freeze-pane flyout hosts the reusable FreezePanesView (Rows / Columns / Both / Unfreeze); it binds
    // to the same VM. Freeze is page-wide and table-independent, so the button is always available.
    private readonly Flyout _freezeFlyout = new();
    private readonly FreezePanesView _freezeView = new();

    public MainWindowViewModel? ViewModel
    {
        get => _vm;
        set
        {
            if (_vm is not null)
                _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = value;
            _freezeView.DataContext = value;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
                RefreshAll();
            }
        }
    }

    public ToolBarView()
    {
        InitializeComponent();

        BrowseButton.Click += (_, _) => _vm?.SetAnnotationTool(AnnotationTool.None);
        SelectButton.Click += (_, _) => _vm?.SetAnnotationTool(AnnotationTool.TextSelect);
        CopyButton.Click += (_, _) => _vm?.CopySelectedText();
        RailHereButton.Click += (_, _) => _vm?.ToggleArmActivateRailClick();
        AnnotateButton.Click += (_, _) => _vm?.ToggleAnnotationMode();

        // Text-markup tools (drag over text) and drawing/note tools — all native Core tools.
        HighlightButton.Click += (_, _) => _vm?.SetAnnotationTool(AnnotationTool.Highlight);
        UnderlineButton.Click += (_, _) => _vm?.SetAnnotationTool(AnnotationTool.Underline);
        StrikeButton.Click += (_, _) => _vm?.SetAnnotationTool(AnnotationTool.StrikeOut);
        SquigglyButton.Click += (_, _) => _vm?.SetAnnotationTool(AnnotationTool.Squiggly);
        PenButton.Click += (_, _) => _vm?.SetAnnotationTool(AnnotationTool.Pen);
        RectButton.Click += (_, _) => _vm?.SetAnnotationTool(AnnotationTool.Rectangle);
        NoteButton.Click += (_, _) => _vm?.SetAnnotationTool(AnnotationTool.TextNote);
        FreeTextButton.Click += (_, _) => _vm?.SetAnnotationTool(AnnotationTool.FreeText);
        EraserButton.Click += (_, _) => _vm?.SetAnnotationTool(AnnotationTool.Eraser);

        ColorButton.Flyout = _colorFlyout;
        ThicknessButton.Flyout = _thicknessFlyout;

        _freezeFlyout.Content = _freezeView;
        FreezeButton.Flyout = _freezeFlyout;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(MainWindowViewModel.ActiveTool):
                UpdateToggleState();
                UpdateCopyVisibility();
                RebuildColorFlyout();
                RebuildThicknessFlyout();
                UpdateColorSwatch();
                UpdateColorThicknessEnabled();
                break;
            case nameof(MainWindowViewModel.IsAnnotationMode):
                UpdateModeState();
                break;
            case nameof(MainWindowViewModel.ArmActivateRailClick):
                UpdateRailHereState();
                break;
            case nameof(MainWindowViewModel.FreezeArmMode):
                UpdateFreezeState();
                // Picking a mode arms a placement; dismiss the flyout so the next click lands on the page.
                if (_vm?.FreezeArmMode != FreezeMode.None) _freezeFlyout.Hide();
                break;
            case nameof(MainWindowViewModel.IsFrozen):
            case nameof(MainWindowViewModel.CanFreeze):
                UpdateFreezeState();
                break;
            case "SelectedText":
                UpdateCopyVisibility();
                break;
        }
    }

    private void RefreshAll()
    {
        UpdateToggleState();
        UpdateModeState();
        UpdateRailHereState();
        UpdateFreezeState();
        UpdateCopyVisibility();
        RebuildColorFlyout();
        RebuildThicknessFlyout();
        UpdateColorSwatch();
        UpdateColorThicknessEnabled();
    }

    private void UpdateToggleState()
    {
        if (_vm is null) return;
        var t = _vm.ActiveTool;
        BrowseButton.IsChecked = t == AnnotationTool.None;
        SelectButton.IsChecked = t == AnnotationTool.TextSelect;
        HighlightButton.IsChecked = t == AnnotationTool.Highlight;
        UnderlineButton.IsChecked = t == AnnotationTool.Underline;
        StrikeButton.IsChecked = t == AnnotationTool.StrikeOut;
        SquigglyButton.IsChecked = t == AnnotationTool.Squiggly;
        PenButton.IsChecked = t == AnnotationTool.Pen;
        RectButton.IsChecked = t == AnnotationTool.Rectangle;
        NoteButton.IsChecked = t == AnnotationTool.TextNote;
        FreeTextButton.IsChecked = t == AnnotationTool.FreeText;
        EraserButton.IsChecked = t == AnnotationTool.Eraser;
    }

    private void UpdateModeState()
    {
        bool on = _vm?.IsAnnotationMode ?? false;
        AnnotationSection.IsVisible = on;
        AnnotateButton.IsChecked = on;
    }

    private void UpdateRailHereState()
        => RailHereButton.IsChecked = _vm?.ArmActivateRailClick ?? false;

    // The Freeze button accents (.active) while a placement is armed or a freeze is in place, and is
    // enabled whenever there's something to do (a page to freeze, an armed placement, or a live freeze).
    private void UpdateFreezeState()
    {
        bool armed = (_vm?.FreezeArmMode ?? FreezeMode.None) != FreezeMode.None;
        bool frozen = _vm?.IsFrozen ?? false;
        FreezeButton.IsEnabled = (_vm?.CanFreeze ?? false) || armed || frozen;
        FreezeButton.Classes.Set("active", armed || frozen);
    }

    private void UpdateCopyVisibility()
        => CopyButton.IsVisible = _vm?.SelectedText is not null;

    // Colour/thickness apply to the palette tools only. Highlight/Pen/Rectangle have colour
    // palettes; Pen/Rectangle additionally have thickness presets. Other tools use fixed
    // colours (e.g. underline=green, strikeout=red) set by Core on activation.
    private static bool HasColorPalette(AnnotationTool t)
        => t is AnnotationTool.Highlight or AnnotationTool.Pen or AnnotationTool.Rectangle;

    private void UpdateColorThicknessEnabled()
    {
        var t = _vm?.ActiveTool ?? AnnotationTool.None;
        ColorButton.IsEnabled = HasColorPalette(t);
        ThicknessButton.IsEnabled = t is AnnotationTool.Pen or AnnotationTool.Rectangle;
    }

    private static (string Color, float Opacity)[] PaletteFor(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Pen => AnnotationInteractionHandler.PenColors,
        AnnotationTool.Rectangle => AnnotationInteractionHandler.RectColors,
        _ => AnnotationInteractionHandler.HighlightColors,
    };

    private void UpdateColorSwatch()
    {
        // Show the current active colour for any tool (palette colour, or the fixed
        // markup colour Core assigns to underline/strikeout/squiggly).
        if (_vm is null) return;
        ColorSwatch.Background = new SolidColorBrush(Color.Parse(_vm.ActiveAnnotationColor));
    }

    private void RebuildColorFlyout()
    {
        if (_vm is null) return;
        var tool = _vm.ActiveTool;
        if (!HasColorPalette(tool)) { _colorFlyout.Content = null; return; }

        var palette = PaletteFor(tool);
        var panel = new WrapPanel { MaxWidth = 170 };
        for (int i = 0; i < palette.Length; i++)
        {
            int idx = i;
            var swatch = new Button
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(2),
                Background = new SolidColorBrush(Color.Parse(palette[i].Color)),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
            };
            swatch.Click += (_, _) =>
            {
                _vm.Controller.Annotations.SetAnnotationColorIndex(tool, idx);
                // Re-activate the tool so the chosen colour becomes the active colour.
                _vm.SetAnnotationTool(tool);
                UpdateColorSwatch();
                _colorFlyout.Hide();
            };
            panel.Children.Add(swatch);
        }
        _colorFlyout.Content = panel;
    }

    private void RebuildThicknessFlyout()
    {
        if (_vm is null) return;
        var tool = _vm.ActiveTool == AnnotationTool.Rectangle ? AnnotationTool.Rectangle : AnnotationTool.Pen;
        var presets = AnnotationInteractionHandler.ThicknessPresets;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        for (int i = 0; i < presets.Length; i++)
        {
            int idx = i;
            var dot = new Button
            {
                Width = 36,
                Height = 32,
                Content = new Border
                {
                    Width = 24,
                    Height = Math.Max(2, presets[i] * 2),
                    CornerRadius = new CornerRadius(2),
                    Background = Brushes.LightGray,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            dot.Click += (_, _) =>
            {
                _vm.Controller.Annotations.SetThicknessIndex(tool, idx);
                _thicknessFlyout.Hide();
            };
            panel.Children.Add(dot);
        }
        _thicknessFlyout.Content = panel;
    }
}
